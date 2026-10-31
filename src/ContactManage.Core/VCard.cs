using System.Text;
using System.Text.RegularExpressions;

namespace ContactManage.Core;

/// <summary>
/// Reads and writes vCard 3.0 (and reads 4.0) for the fields <see cref="Contact"/> models. Everything else
/// is kept as raw lines in <see cref="Contact.Extra"/>, so a read-then-write round trip does not drop data.
/// </summary>
public static class VCard
{
    private static readonly Regex Fold = new(@"\n[ \t]", RegexOptions.Compiled);

    /// <summary>Property name of a raw line, without group prefix or parameters, upper-cased.</summary>
    public static string PropertyName(string line)
    {
        var end = line.IndexOfAny([';', ':']);
        var head = end < 0 ? line : line[..end];
        return head[(head.LastIndexOf('.') + 1)..].ToUpperInvariant();
    }

    public static IReadOnlyList<Contact> ParseAll(string text)
    {
        var result = new List<Contact>();
        var unfolded = Fold.Replace(text.Replace("\r\n", "\n").Replace('\r', '\n'), "");
        Contact? current = null;
        foreach (var raw in unfolded.Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.Length == 0) continue;
            if (line.Equals("BEGIN:VCARD", StringComparison.OrdinalIgnoreCase)) { current = new Contact(); continue; }
            if (line.Equals("END:VCARD", StringComparison.OrdinalIgnoreCase))
            {
                if (current is not null) result.Add(current);
                current = null;
                continue;
            }
            if (current is not null) ApplyLine(current, line);
        }
        return result;
    }

    private static void ApplyLine(Contact c, string line)
    {
        var colon = line.IndexOf(':');
        if (colon <= 0) return;
        var head = line[..colon];
        var value = line[(colon + 1)..];
        var semi = head.IndexOf(';');
        var nameWithGroup = semi < 0 ? head : head[..semi];
        var prms = semi < 0 ? "" : head[semi..];
        var name = nameWithGroup[(nameWithGroup.LastIndexOf('.') + 1)..].ToUpperInvariant();

        switch (name)
        {
            case "VERSION" or "PRODID" or "REV":
                break;
            case "UID": c.Uid = Unescape(value); break;
            case "FN": c.DisplayName = NullIfEmpty(Unescape(value)); break;
            case "N":
                var n = SplitEscaped(value, ';');
                c.FamilyName = Part(n, 0); c.GivenName = Part(n, 1); c.MiddleName = Part(n, 2);
                c.NamePrefix = Part(n, 3); c.NameSuffix = Part(n, 4);
                break;
            case "ORG":
                var o = SplitEscaped(value, ';');
                c.Company = Part(o, 0);
                c.Department = o.Count > 1 ? NullIfEmpty(string.Join(";", o.Skip(1).Where(x => x.Length > 0))) : null;
                break;
            case "TITLE": c.JobTitle = NullIfEmpty(Unescape(value)); break;
            case "NOTE": c.Notes = NullIfEmpty(Unescape(value)); break;
            case "EMAIL": AddValue(c, c.Emails, value.Trim(), prms); break;
            case "TEL": AddValue(c, c.Phones, value.Trim(), prms); break;
            case "ADR": AddValue(c, c.Addresses, value.Trim(), prms); break; // kept in its escaped vCard form
            case "CATEGORIES":
                // Gmail labels travel as categories; reading them back is what lets a backup restore them.
                foreach (var g in SplitEscaped(value, ',').Select(g => g.Trim()).Where(g => g.Length > 0))
                    if (!c.Groups.Contains(g, StringComparer.OrdinalIgnoreCase)) c.Groups.Add(g);
                break;
            default: c.Extra.Add(line); break;
        }
    }

    private static void AddValue(Contact c, List<string> list, string value, string prms)
    {
        if (value.Length == 0 || list.Contains(value, StringComparer.OrdinalIgnoreCase)) return;
        list.Add(value);
        if (prms.Length > 0) c.Labels[value] = prms;
    }

    /// <param name="uid">Written instead of the contact's own UID; used to overwrite an existing entry without changing its UID.</param>
    public static string Write(Contact c, string? uid = null)
    {
        var sb = new StringBuilder();
        void Line(string s) => sb.Append(FoldLine(s)).Append("\r\n");

        Line("BEGIN:VCARD");
        Line("VERSION:3.0");
        Line("UID:" + (uid ?? c.Uid ?? c.Id));
        Line("FN:" + Escape(FormattedName(c)));
        Line($"N:{Escape(c.FamilyName)};{Escape(c.GivenName)};{Escape(c.MiddleName)};{Escape(c.NamePrefix)};{Escape(c.NameSuffix)}");
        if (!string.IsNullOrWhiteSpace(c.Company) || !string.IsNullOrWhiteSpace(c.Department))
            Line($"ORG:{Escape(c.Company)}{(string.IsNullOrWhiteSpace(c.Department) ? "" : ";" + Escape(c.Department))}");
        if (!string.IsNullOrWhiteSpace(c.JobTitle)) Line("TITLE:" + Escape(c.JobTitle));
        foreach (var e in c.Emails) Line($"EMAIL{Label(c, e)}:{e}");
        foreach (var p in c.Phones) Line($"TEL{Label(c, p)}:{p}");
        foreach (var a in c.Addresses) Line($"ADR{Label(c, a)}:{a}");
        if (!string.IsNullOrWhiteSpace(c.Notes)) Line("NOTE:" + Escape(c.Notes));
        if (c.Groups.Count > 0) Line("CATEGORIES:" + string.Join(",", c.Groups.Select(Escape))); // Gmail labels
        foreach (var x in c.Extra) Line(x);
        Line("END:VCARD");
        return sb.ToString();
    }

    public static string WriteAll(IEnumerable<Contact> contacts) => string.Concat(contacts.Select(c => Write(c)));

    /// <summary>The display name vCard requires: FN, else the parts of N, else something identifying.</summary>
    public static string FormattedName(Contact c)
    {
        if (!string.IsNullOrWhiteSpace(c.DisplayName)) return c.DisplayName.Trim();
        var parts = string.Join(" ", new[] { c.NamePrefix, c.GivenName, c.MiddleName, c.FamilyName, c.NameSuffix }
            .Where(s => !string.IsNullOrWhiteSpace(s))).Trim();
        if (parts.Length > 0) return parts;
        return c.Emails.FirstOrDefault() ?? c.Company ?? c.Phones.FirstOrDefault() ?? "(no name)";
    }

    private static string Label(Contact c, string value) => c.Labels.TryGetValue(value, out var l) ? l : "";
    private static string? Part(List<string> parts, int i) => i < parts.Count ? NullIfEmpty(parts[i]) : null;
    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    public static string Escape(string? value) =>
        (value ?? "").Replace("\\", "\\\\").Replace(";", "\\;").Replace(",", "\\,").Replace("\r\n", "\\n").Replace("\n", "\\n");

    public static string Unescape(string value)
    {
        if (!value.Contains('\\')) return value;
        var sb = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\' && i + 1 < value.Length)
            {
                var next = value[++i];
                sb.Append(next is 'n' or 'N' ? '\n' : next);
            }
            else sb.Append(value[i]);
        }
        return sb.ToString();
    }

    /// <summary>The parts of a structured value such as an ADR (";;street;city;region;postal;country"), unescaped.</summary>
    public static IReadOnlyList<string> SplitStructured(string value) => SplitEscaped(value, ';');

    /// <summary>Splits on a separator that is not escaped, then unescapes each part.</summary>
    private static List<string> SplitEscaped(string value, char separator)
    {
        var parts = new List<string>();
        var sb = new StringBuilder();
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\' && i + 1 < value.Length) sb.Append(value[i]).Append(value[++i]);
            else if (value[i] == separator) { parts.Add(Unescape(sb.ToString())); sb.Clear(); }
            else sb.Append(value[i]);
        }
        parts.Add(Unescape(sb.ToString()));
        return parts;
    }

    // RFC 6350 folds lines longer than 75 octets; fold by characters without splitting a surrogate pair.
    private static string FoldLine(string line)
    {
        if (line.Length <= 74) return line;
        var sb = new StringBuilder();
        var count = 0;
        for (var i = 0; i < line.Length; i++)
        {
            if (count >= 74 && !char.IsLowSurrogate(line[i])) { sb.Append("\r\n "); count = 1; }
            sb.Append(line[i]);
            count++;
        }
        return sb.ToString();
    }
}
