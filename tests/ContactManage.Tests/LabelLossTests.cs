using ContactManage.Core;
using Xunit;

namespace ContactManage.Tests;

public class LabelLossTests
{
    private static ContactFolder Folder(FolderKind kind, string? url) =>
        new("acct:/0/Contacts", "Contacts", "/0/Contacts", "Work", "host", kind, IsWritable: true, ServerUrl: url);

    private static Contact WithEmail(string email, string? parameters)
    {
        var c = new Contact { DisplayName = "Pat" };
        c.Emails.Add(email);
        if (parameters is not null) c.Labels[email] = parameters;
        return c;
    }

    [Fact]
    public void Applies_to_an_icewarp_carddav_folder() =>
        Assert.True(LabelLoss.Applies(Folder(FolderKind.CardDav, "https://mail.example.com/webdav/")));

    [Fact]
    public void Does_not_apply_to_aol_which_keeps_the_type() =>
        Assert.False(LabelLoss.Applies(Folder(FolderKind.CardDav, "https://carddav.aol.com/dav/x/Contacts/")));

    [Theory]
    [InlineData(FolderKind.GmailApi)]
    [InlineData(FolderKind.Local)]
    public void Does_not_apply_to_other_kinds_of_folder(FolderKind kind) =>
        Assert.False(LabelLoss.Applies(Folder(kind, null)));

    [Theory]
    [InlineData(";TYPE=WORK", 1)]
    [InlineData(";TYPE=HOME,INTERNET", 1)]
    [InlineData(";WORK", 1)]
    [InlineData(";TYPE=INTERNET,PREF", 0)]
    [InlineData(";TYPE=INTERNET", 0)]
    [InlineData("", 0)]
    public void Counts_only_types_the_user_would_see(string parameters, int expected) =>
        Assert.Equal(expected, LabelLoss.CountLabeledEmails([WithEmail("a@example.com", parameters)]));

    [Fact]
    public void An_email_without_a_label_is_not_counted() =>
        Assert.Equal(0, LabelLoss.CountLabeledEmails([WithEmail("a@example.com", null)]));

    [Fact]
    public void Warning_names_the_count_for_icewarp()
    {
        var text = LabelLoss.Warning([Folder(FolderKind.CardDav, "https://mail.example.com/webdav/")],
            [WithEmail("a@example.com", ";TYPE=WORK"), WithEmail("b@example.com", ";TYPE=HOME"), WithEmail("c@example.com", null)]);
        Assert.Contains("2 email addresses have a type", text);
    }

    [Fact]
    public void Warning_is_empty_when_nothing_would_be_lost() =>
        Assert.Equal("", LabelLoss.Warning([Folder(FolderKind.CardDav, "https://mail.example.com/webdav/")], [WithEmail("a@example.com", null)]));

    [Fact]
    public void Warning_is_empty_for_a_server_that_keeps_types() =>
        Assert.Equal("", LabelLoss.Warning([Folder(FolderKind.CardDav, "https://carddav.aol.com/dav/x/Contacts/")], [WithEmail("a@example.com", ";TYPE=WORK")]));
}
