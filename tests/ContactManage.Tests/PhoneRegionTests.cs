using ContactManage.Core;
using Xunit;

namespace ContactManage.Tests;

public class PhoneRegionTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), "cm-region-" + Guid.NewGuid().ToString("N") + ".json");

    public void Dispose() => File.Delete(_file);

    [Fact]
    public void The_region_is_US_until_the_user_changes_it() => Assert.Equal("US", new AppSettings().PhoneRegion);

    [Fact]
    public void A_chosen_region_is_remembered()
    {
        new AppSettings { PhoneRegion = "GB" }.Save(_file);
        Assert.Equal("GB", AppSettings.Load(_file).PhoneRegion);
    }

    [Theory]
    [InlineData("gb", "GB")]
    [InlineData(" de ", "DE")]
    [InlineData("ZZ", "US")]
    [InlineData("", "US")]
    [InlineData(null, "US")]
    [InlineData("not a region", "US")]
    public void A_region_the_parser_does_not_know_falls_back_to_US(string? stored, string expected) =>
        Assert.Equal(expected, PhoneRegions.Normalize(stored));

    [Fact]
    public void A_settings_file_with_a_bad_region_still_loads()
    {
        File.WriteAllText(_file, "{ \"PhoneRegion\": \"XX\", \"Start\": \"Online\" }");
        var settings = AppSettings.Load(_file);
        Assert.Equal("US", settings.PhoneRegion);
        Assert.Equal(StartPreference.Online, settings.Start);
    }

    [Fact]
    public void The_list_has_named_countries_with_their_calling_codes()
    {
        var all = PhoneRegions.All();
        var us = all.Single(r => r.Code == "US");
        Assert.Equal("United States", us.Name);
        Assert.Equal("United States (+1)", us.ToString());
        Assert.Contains(all, r => r.Code == "GB" && r.CallingCode == 44);
        Assert.Equal(all.Select(r => r.Name).Order(StringComparer.CurrentCultureIgnoreCase), all.Select(r => r.Name));
    }

    [Fact]
    public void A_local_UK_number_matches_its_international_form_only_with_the_UK_region()
    {
        Assert.Equal(new Normalizer("GB").Phone("+44 20 7946 0958"), new Normalizer("GB").Phone("020 7946 0958"));
        Assert.NotEqual(new Normalizer("US").Phone("+44 20 7946 0958"), new Normalizer("US").Phone("020 7946 0958"));
    }

    [Fact]
    public void The_merge_engine_links_a_local_and_an_international_number_when_given_the_matching_region()
    {
        static List<Contact> Pair() =>
        [
            new() { DisplayName = "Ann Lee", Phones = { "020 7946 0958" } },
            new() { DisplayName = "A. Lee", Phones = { "+44 20 7946 0958" } },
        ];

        Assert.Single(new MergeEngine(new Normalizer("GB")).Merge(Pair()).Groups);
        Assert.Empty(new MergeEngine(new Normalizer("US")).Merge(Pair()).Groups);
    }
}
