using System.Globalization;
using PhoneNumbers;

namespace ContactManage.Core;

/// <summary>A country the phone number parser knows, as the region list shows it.</summary>
public sealed record PhoneRegion(string Code, string Name)
{
    public override string ToString() => $"{Name} (+{CallingCode})";

    public int CallingCode => PhoneNumberUtil.GetInstance().GetCountryCodeForRegion(Code);
}

/// <summary>The countries a number without a country code can be read as (the "phone region" setting).</summary>
public static class PhoneRegions
{
    public const string Default = "US";

    /// <summary>Every region the parser supports that has a country name, sorted by name.</summary>
    public static IReadOnlyList<PhoneRegion> All() =>
        PhoneNumberUtil.GetInstance().GetSupportedRegions()
            .Select(code => (Code: code, Name: NameOf(code)))
            .Where(r => r.Name is not null)
            .Select(r => new PhoneRegion(r.Code, r.Name!))
            .OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    /// <summary>The code in upper case when the parser supports it, otherwise the default (a damaged settings file must not break matching).</summary>
    public static string Normalize(string? code)
    {
        var upper = code?.Trim().ToUpperInvariant();
        return upper is { Length: 2 } && PhoneNumberUtil.GetInstance().GetSupportedRegions().Contains(upper) ? upper : Default;
    }

    private static string? NameOf(string code)
    {
        try { return new RegionInfo(code).EnglishName; }
        catch (ArgumentException) { return null; } // a code Windows has no country for (for example "001")
    }
}
