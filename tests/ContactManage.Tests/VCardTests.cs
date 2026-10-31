using ContactManage.Core;
using Xunit;

namespace ContactManage.Tests;

public class VCardTests
{
    private const string Rich = "BEGIN:VCARD\r\nVERSION:3.0\r\nUID:abc-1\r\nFN:José Müller-Łukasz\r\nN:Müller-Łukasz;José;Q.;Dr.;Jr.\r\n" +
        "ORG:Acme\\, Inc.;Purchasing\r\nTITLE:Senior Buyer\r\nEMAIL;TYPE=INTERNET,WORK:jose@acme.example\r\nEMAIL;TYPE=HOME:jose@home.example\r\n" +
        "TEL;TYPE=CELL:+14155550101\r\nADR;TYPE=WORK:;;1 Main St\\, Suite 4;Springfield;IL;62701;USA\r\nBDAY:1980-02-29\r\n" +
        "item1.URL:https://acme.example\r\nCATEGORIES:Friends,Work\r\nX-CUSTOM;FOO=bar:keep me\r\n" +
        "NOTE:Line one\\nLine two\\; with semicolon\\, comma and \\\\ backslash\r\nEND:VCARD\r\n";

    [Fact]
    public void Parses_modeled_fields()
    {
        var c = Assert.Single(VCard.ParseAll(Rich));

        Assert.Equal("abc-1", c.Uid);
        Assert.Equal("José Müller-Łukasz", c.DisplayName);
        Assert.Equal("Müller-Łukasz", c.FamilyName);
        Assert.Equal("José", c.GivenName);
        Assert.Equal("Q.", c.MiddleName);
        Assert.Equal("Dr.", c.NamePrefix);
        Assert.Equal("Jr.", c.NameSuffix);
        Assert.Equal("Acme, Inc.", c.Company);
        Assert.Equal("Purchasing", c.Department);
        Assert.Equal("Senior Buyer", c.JobTitle);
        Assert.Equal(["jose@acme.example", "jose@home.example"], c.Emails);
        Assert.Equal(";TYPE=INTERNET,WORK", c.Labels["jose@acme.example"]);
        Assert.Equal(";TYPE=CELL", c.Labels["+14155550101"]);
        Assert.Equal("Line one\nLine two; with semicolon, comma and \\ backslash", c.Notes);
    }

    [Fact]
    public void Unmodeled_properties_are_kept_as_raw_lines()
    {
        var c = VCard.ParseAll(Rich)[0];

        Assert.Equal(["BDAY:1980-02-29", "item1.URL:https://acme.example", "X-CUSTOM;FOO=bar:keep me"], c.Extra);
        Assert.Equal(["Friends", "Work"], c.Groups);                        // categories are the contact's labels, written back as CATEGORIES
    }

    [Fact]
    public void Write_then_parse_loses_nothing()
    {
        var original = VCard.ParseAll(Rich)[0];

        var again = VCard.ParseAll(VCard.Write(original))[0];

        Assert.Equal(original.DisplayName, again.DisplayName);
        Assert.Equal(original.FamilyName, again.FamilyName);
        Assert.Equal(original.MiddleName, again.MiddleName);
        Assert.Equal(original.NamePrefix, again.NamePrefix);
        Assert.Equal(original.Company, again.Company);
        Assert.Equal(original.Department, again.Department);
        Assert.Equal(original.Emails, again.Emails);
        Assert.Equal(original.Phones, again.Phones);
        Assert.Equal(original.Addresses, again.Addresses);
        Assert.Equal(original.Notes, again.Notes);
        Assert.Equal(original.Extra, again.Extra);
        Assert.Equal(original.Labels, again.Labels);
        Assert.Equal("abc-1", again.Uid);
    }

    [Fact]
    public void Written_card_uses_crlf_and_folds_long_lines()
    {
        var c = new Contact { DisplayName = "A", Notes = new string('x', 300) };

        var text = VCard.Write(c);

        Assert.All(text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries), l => Assert.True(l.Length <= 75, l.Length.ToString()));
        Assert.Equal(c.Notes, VCard.ParseAll(text)[0].Notes);
    }

    [Fact]
    public void Display_name_falls_back_to_name_parts_then_email()
    {
        Assert.Equal("Ann Lee", VCard.FormattedName(new Contact { GivenName = "Ann", FamilyName = "Lee" }));
        var c = new Contact();
        c.Emails.Add("a@b.example");
        Assert.Equal("a@b.example", VCard.FormattedName(c));
    }

    [Fact]
    public void Reads_several_cards_from_one_file()
    {
        var text = "BEGIN:VCARD\nVERSION:3.0\nFN:One\nEND:VCARD\nBEGIN:VCARD\nVERSION:3.0\nFN:Two\nEND:VCARD\n";

        Assert.Equal(["One", "Two"], VCard.ParseAll(text).Select(c => c.DisplayName));
    }

    [Fact]
    public void Merged_contact_keeps_labels_extras_and_name_parts_of_its_sources()
    {
        var a = VCard.ParseAll("BEGIN:VCARD\nFN:Ann Lee\nN:Lee;Ann;;Dr.;\nEMAIL;TYPE=WORK:ann@x.example\nBDAY:1990-01-01\nURL:https://a.example\nEND:VCARD")[0];
        var b = VCard.ParseAll("BEGIN:VCARD\nFN:Ann Lee\nN:Lee;Ann;;;\nEMAIL;TYPE=HOME:ANN@x.example\nTEL;TYPE=CELL:415-555-0100\nBDAY:1990-01-02\nURL:https://a.example\nEND:VCARD")[0];

        var merged = new MergeEngine().Merge([a, b]).Groups.Single().BuildMerged();

        Assert.Equal("Dr.", merged.NamePrefix);
        Assert.Equal(";TYPE=WORK", merged.Labels["ann@x.example"]);
        Assert.Equal(";TYPE=CELL", merged.Labels["415-555-0100"]);
        Assert.Equal(["BDAY:1990-01-01", "URL:https://a.example"], merged.Extra);
    }
}
