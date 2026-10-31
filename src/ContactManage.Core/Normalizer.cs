using System.Globalization;
using System.Text;
using PhoneNumbers;

namespace ContactManage.Core;

/// <summary>Turns raw contact values into comparable keys. Keys are for matching only; stored values keep their original text.</summary>
public sealed class Normalizer(string defaultRegion = "US")
{
    private static readonly PhoneNumberUtil PhoneUtil = PhoneNumberUtil.GetInstance();

    /// <summary>Lower-cased address, or null when the value holds no address.</summary>
    public string? Email(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var s = value.Trim();
        if (s.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)) s = s[7..];
        var lt = s.IndexOf('<');
        var gt = s.IndexOf('>');
        if (lt >= 0 && gt > lt) s = s[(lt + 1)..gt];
        s = s.Trim().ToLowerInvariant();
        return s.Contains('@') ? s : null;
    }

    /// <summary>
    /// E.164 when the number parses (extensions are dropped). Otherwise the digits, if there are at least 7.
    /// A 7-digit local number therefore only matches the same 7 digits, not a full number with area code.
    /// </summary>
    public string? Phone(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try
        {
            var number = PhoneUtil.Parse(value, defaultRegion);
            if (PhoneUtil.IsPossibleNumber(number))
                return PhoneUtil.Format(number, PhoneNumberFormat.E164);
        }
        catch (NumberParseException)
        {
            // fall through to digits
        }
        var digits = new string(value.Where(char.IsDigit).ToArray());
        return digits.Length >= 7 ? digits : null;
    }

    /// <summary>Name key: accents, case and punctuation removed; "Last, First" becomes "first last".</summary>
    public string Name(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var s = value.Trim();
        var comma = s.IndexOf(',');
        if (comma > 0 && s.IndexOf(',', comma + 1) < 0)
            s = s[(comma + 1)..] + " " + s[..comma];
        return Fold(s);
    }

    /// <summary>Case, accent, punctuation and spacing insensitive key for comparing free text.</summary>
    public string Loose(string? value) => Fold(value ?? "");

    /// <summary>Name key of a contact: given + family when present, otherwise the display name.</summary>
    public string NameKey(Contact c) =>
        !string.IsNullOrWhiteSpace(c.GivenName) || !string.IsNullOrWhiteSpace(c.FamilyName)
            ? Fold($"{c.GivenName} {c.FamilyName}")
            : Name(c.DisplayName);

    /// <summary>
    /// Two name keys can belong to one person when either is empty, or one token set contains the other
    /// ("john smith" and "smith john" and "john a smith" are compatible; "john smith" and "john doe" are not).
    /// </summary>
    public static bool NamesCompatible(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0) return true;
        var ta = a.Split(' ').ToHashSet();
        var tb = b.Split(' ').ToHashSet();
        return ta.IsSubsetOf(tb) || tb.IsSubsetOf(ta);
    }

    private static string Fold(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : ' ');
        }
        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}
