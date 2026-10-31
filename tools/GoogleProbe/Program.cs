using System.Diagnostics;
using System.Text.Json;
using ContactManage.Adapters.Google;

// Read-only Gmail probe. Opens Google's sign-in page in the browser (the Google password is typed there, never here),
// then reads the account's labels and contacts and prints COUNTS and LABEL NAMES only, never names, emails or phone numbers.
// Tokens stay in memory and are not saved. Nothing in the account is changed.
//   dotnet run --project tools/GoogleProbe -- [--log out.log] [--account you@gmail.com]

var logIndex = Array.IndexOf(args, "--log");
if (logIndex >= 0 && logIndex + 1 < args.Length)
    Console.SetOut(new Tee(Console.Out, new StreamWriter(args[logIndex + 1]) { AutoFlush = true }));
var accountIndex = Array.IndexOf(args, "--account");
var loginHint = accountIndex >= 0 && accountIndex + 1 < args.Length ? args[accountIndex + 1] : null;

try
{
    var config = GoogleClientConfig.Load();
    Console.WriteLine("Google client file found and readable.");
    using var auth = new GoogleAuth(config);

    Console.WriteLine("\n== Sign in");
    Console.WriteLine("Your browser will open Google's sign-in page. Sign in and approve the contacts permission.");
    Console.WriteLine("If Google says the app is not verified, choose Advanced, then continue (expected while the app is in Testing).");
    var tokens = await auth.SignInAsync(url =>
    {
        try { Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Console.WriteLine("Could not open the browser. Open this address yourself:\n" + url);
        }
        return Task.CompletedTask;
    }, loginHint);
    Console.WriteLine($"Signed in as {tokens.Email ?? "(email not returned)"}.");
    Console.WriteLine($"Refresh token issued: {(tokens.RefreshToken is null ? "NO" : "yes")}");

    if (tokens.RefreshToken is not null)
    {
        var again = await auth.RefreshAsync(tokens.RefreshToken);
        Console.WriteLine($"Refreshing the access token works: {(again.AccessToken.Length > 0 ? "yes" : "no")}");
    }

    using var people = new PeopleApiClient(_ => Task.FromResult(tokens.AccessToken));

    Console.WriteLine("\n== Labels (contact groups)");
    var groups = await people.ListContactGroupsAsync();
    foreach (var g in groups.OrderBy(g => g.IsSystem ? 0 : 1).ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase))
        Console.WriteLine($"  {(g.IsSystem ? "[built-in]" : "[label]   ")} {g.Name,-30} {g.MemberCount,6} contacts   {g.ResourceName}");

    Console.WriteLine("\n== Contacts");
    var all = await people.ListConnectionsAsync("names,emailAddresses,phoneNumbers,memberships");
    Console.WriteLine($"  Contacts in the account: {all.Count}");

    static IEnumerable<JsonElement> Items(JsonElement p, string name) =>
        p.TryGetProperty(name, out var list) ? list.EnumerateArray() : [];

    Console.WriteLine($"  with a name: {all.Count(p => Items(p, "names").Any())}");
    Console.WriteLine($"  with an email: {all.Count(p => Items(p, "emailAddresses").Any())}");
    Console.WriteLine($"  with a phone: {all.Count(p => Items(p, "phoneNumbers").Any())}");

    var systemNames = groups.Where(g => g.IsSystem).Select(g => g.ResourceName).ToHashSet();
    IEnumerable<string> Memberships(JsonElement p) => Items(p, "memberships")
        .Select(m => m.TryGetProperty("contactGroupMembership", out var cg) && cg.TryGetProperty("contactGroupResourceName", out var r) ? r.GetString() : null)
        .OfType<string>();

    Console.WriteLine($"  in the main list (myContacts): {all.Count(p => Memberships(p).Contains("contactGroups/myContacts"))}");
    var labelCounts = all.GroupBy(p => Memberships(p).Count(m => !systemNames.Contains(m))).OrderBy(g => g.Key);
    foreach (var g in labelCounts)
        Console.WriteLine($"  contacts with {g.Key} label(s): {g.Count()}");

    // Duplicate preview by shared email, counts only.
    var byEmail = all.SelectMany((p, i) => Items(p, "emailAddresses")
            .Select(e => e.TryGetProperty("value", out var v) ? v.GetString()?.Trim().ToLowerInvariant() : null)
            .OfType<string>().Distinct().Select(v => (Email: v, Index: i)))
        .GroupBy(x => x.Email).Where(g => g.Select(x => x.Index).Distinct().Count() > 1).ToList();
    Console.WriteLine($"  emails shared by more than one contact: {byEmail.Count} (covering {byEmail.SelectMany(g => g.Select(x => x.Index)).Distinct().Count()} contacts)");

    Console.WriteLine("\nRead-only probe finished. Nothing was changed in the account.");
    return 0;
}
catch (GoogleAuthException ex)
{
    Console.WriteLine("\nStopped: " + ex.Message);
    return 1;
}
