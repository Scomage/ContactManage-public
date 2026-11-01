namespace ContactManage.Core;

/// <summary>
/// IceWarp's CardDAV server drops the type of an email address (WORK, HOME ...) when it stores a contact. This finds out whether a
/// write to such a folder would lose any, so the user can be told before it happens.
/// </summary>
public static class LabelLoss
{
    // Types every server adds by itself; they say nothing the user chose.
    private static readonly HashSet<string> Plain = new(StringComparer.OrdinalIgnoreCase) { "INTERNET", "PREF" };

    /// <summary>True for a CardDAV folder on a server that is not known to keep email types. AOL keeps them; IceWarp does not.</summary>
    public static bool Applies(ContactFolder target) =>
        target.Kind == FolderKind.CardDav &&
        !(Uri.TryCreate(target.ServerUrl, UriKind.Absolute, out var u) && u.Host.EndsWith("aol.com", StringComparison.OrdinalIgnoreCase));

    /// <summary>How many email addresses carry a type the user would see (WORK, HOME, OTHER ...).</summary>
    public static int CountLabeledEmails(IEnumerable<Contact> contacts) =>
        contacts.Sum(c => c.Emails.Count(e => c.Labels.TryGetValue(e, out var parameters) && HasUserType(parameters)));

    private static bool HasUserType(string parameters) =>
        parameters.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.StartsWith("TYPE=", StringComparison.OrdinalIgnoreCase) ? p[5..] : p) // vCard 2.1 writes the bare type
            .SelectMany(p => p.Split(',', StringSplitOptions.RemoveEmptyEntries))
            .Select(t => t.Trim().Trim('"'))
            .Any(t => t.Length > 0 && !t.Contains('=') && !Plain.Contains(t));

    /// <summary>The sentence to add to a confirmation, or "" when nothing would be lost.</summary>
    public static string Warning(IEnumerable<ContactFolder> targets, IEnumerable<Contact> contacts)
    {
        if (!targets.Any(Applies)) return "";
        var n = CountLabeledEmails(contacts);
        return n == 0 ? "" :
            $"\n\nNote: {n:N0} email {(n == 1 ? "address has" : "addresses have")} a type such as Work or Home. IceWarp's server does not keep email types, " +
            "so after saving they show without it. The addresses themselves are not changed, and the backup keeps the types.";
    }
}
