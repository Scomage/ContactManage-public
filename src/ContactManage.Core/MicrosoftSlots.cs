namespace ContactManage.Core;

/// <summary>
/// An Outlook contact has fixed places for its values, not an open list: three email addresses, two business and two home phones plus
/// one mobile, and one home, one business and one other address. This decides which value goes where, so a merge that gathers more
/// values than fit is noticed before saving instead of losing them quietly.
/// </summary>
public sealed record MicrosoftSlots(
    IReadOnlyList<string> Emails,
    IReadOnlyList<string> BusinessPhones,
    IReadOnlyList<string> HomePhones,
    string? MobilePhone,
    string? HomeAddress,
    string? BusinessAddress,
    string? OtherAddress,
    int Overflow)
{
    public const int MaxEmails = 3, MaxBusinessPhones = 2, MaxHomePhones = 2;

    public static MicrosoftSlots Of(Contact c)
    {
        var overflow = 0;

        var emails = c.Emails.Where(e => !string.IsNullOrWhiteSpace(e)).Select(e => e.Trim()).Take(MaxEmails).ToList();
        overflow += Math.Max(0, c.Emails.Count(e => !string.IsNullOrWhiteSpace(e)) - MaxEmails);

        // Each phone goes to the place its type names; a number whose place is full takes any place still free before it counts as lost.
        var business = new List<string>();
        var home = new List<string>();
        string? mobile = null;
        var leftover = new List<string>();
        foreach (var phone in c.Phones.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()))
        {
            var types = TypesOf(c, phone);
            if (types.Contains("CELL") && mobile is null) mobile = phone;
            else if (types.Contains("HOME") && !types.Contains("CELL") && home.Count < MaxHomePhones) home.Add(phone);
            else if (!types.Contains("HOME") && !types.Contains("CELL") && business.Count < MaxBusinessPhones) business.Add(phone);
            else leftover.Add(phone);
        }
        foreach (var phone in leftover)
        {
            if (mobile is null) mobile = phone;
            else if (business.Count < MaxBusinessPhones) business.Add(phone);
            else if (home.Count < MaxHomePhones) home.Add(phone);
            else overflow++;
        }

        string? homeAdr = null, businessAdr = null, otherAdr = null;
        var spare = new List<string>();
        foreach (var adr in c.Addresses.Where(a => !string.IsNullOrWhiteSpace(a)))
        {
            var types = TypesOf(c, adr);
            if (types.Contains("HOME") && homeAdr is null) homeAdr = adr;
            else if (types.Contains("WORK") && businessAdr is null) businessAdr = adr;
            else if (!types.Contains("HOME") && !types.Contains("WORK") && otherAdr is null) otherAdr = adr;
            else spare.Add(adr);
        }
        foreach (var adr in spare)
        {
            if (otherAdr is null) otherAdr = adr;
            else if (homeAdr is null) homeAdr = adr;
            else if (businessAdr is null) businessAdr = adr;
            else overflow++;
        }

        return new MicrosoftSlots(emails, business, home, mobile, homeAdr, businessAdr, otherAdr, overflow);
    }

    /// <summary>Values (emails, phones, addresses) across the contacts that have no place in an Outlook contact.</summary>
    public static int CountOverflow(IEnumerable<Contact> contacts) => contacts.Sum(c => Of(c).Overflow);

    /// <summary>The sentence to add to a confirmation for a Microsoft target, or "" when everything fits.</summary>
    public static string Warning(IEnumerable<ContactFolder> targets, IEnumerable<Contact> contacts)
    {
        if (!targets.Any(t => t.Kind == FolderKind.Microsoft)) return "";
        var n = CountOverflow(contacts);
        return n == 0 ? "" :
            $"\n\nNote: an Outlook contact holds at most {MaxEmails} email addresses, {MaxBusinessPhones + MaxHomePhones + 1} phone numbers and 3 addresses. " +
            $"{n:N0} {(n == 1 ? "value does" : "values do")} not fit and would be left out of the saved contacts. They stay in the backup.";
    }

    private static HashSet<string> TypesOf(Contact c, string value) =>
        c.Labels.TryGetValue(value, out var label)
            ? label.Split(';', ',', '=').Select(t => t.Trim().Trim('"').ToUpperInvariant()).Where(t => t.Length > 0 && t != "TYPE").ToHashSet()
            : [];
}
