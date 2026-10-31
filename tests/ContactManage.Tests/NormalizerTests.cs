using ContactManage.Core;
using Xunit;

namespace ContactManage.Tests;

public class NormalizerTests
{
    private readonly Normalizer _n = new("US");

    [Theory]
    [InlineData("John@Example.COM", "john@example.com")]
    [InlineData("  mailto:john@example.com ", "john@example.com")]
    [InlineData("John Smith <john@example.com>", "john@example.com")]
    public void Email_is_lowercased_and_unwrapped(string raw, string expected) =>
        Assert.Equal(expected, _n.Email(raw));

    [Theory]
    [InlineData("not an address")]
    [InlineData("")]
    [InlineData(null)]
    public void Email_returns_null_without_an_address(string? raw) => Assert.Null(_n.Email(raw));

    [Theory]
    [InlineData("(201) 555-0123")]
    [InlineData("201-555-0123")]
    [InlineData("201.555.0123")]
    [InlineData("+1 201 555 0123")]
    [InlineData("1-201-555-0123")]
    [InlineData("201 555 0123 x45")]
    public void Phone_formats_normalize_to_one_key(string raw) =>
        Assert.Equal("+12015550123", _n.Phone(raw));

    [Fact]
    public void Phone_with_another_country_code_is_kept_distinct() =>
        Assert.Equal("+442071838750", _n.Phone("+44 20 7183 8750"));

    [Theory]
    [InlineData("12")]
    [InlineData("")]
    [InlineData(null)]
    public void Phone_returns_null_when_too_short(string? raw) => Assert.Null(_n.Phone(raw));

    [Theory]
    [InlineData("Smith, John", "john smith")]
    [InlineData("  JOHN   smith ", "john smith")]
    [InlineData("José Núñez", "jose nunez")]
    [InlineData("Dr. John A. Smith", "dr john a smith")]
    public void Name_removes_case_accents_and_punctuation(string raw, string expected) =>
        Assert.Equal(expected, _n.Name(raw));

    [Theory]
    [InlineData("john smith", "smith john", true)]
    [InlineData("john smith", "john a smith", true)]
    [InlineData("john", "john smith", true)]
    [InlineData("", "john smith", true)]
    [InlineData("john smith", "john doe", false)]
    public void NamesCompatible_cases(string a, string b, bool expected) =>
        Assert.Equal(expected, Normalizer.NamesCompatible(a, b));
}
