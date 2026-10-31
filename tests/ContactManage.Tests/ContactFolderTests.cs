using ContactManage.Core;
using Xunit;

namespace ContactManage.Tests;

public class ContactFolderTests
{
    private static ContactFolder F(int? contacts, int lists = 0) =>
        new("id", "Contacts", "/Contacts", "acct", "server", FolderKind.CardDav, true, contacts, lists);

    [Theory]
    [InlineData(null, 0, "")]
    [InlineData(0, 0, "0")]
    [InlineData(2096, 0, "2,096")]
    [InlineData(339, 1, "339 + 1 list")]
    [InlineData(339, 3, "339 + 3 lists")]
    public void CountText(int? contacts, int lists, string expected) =>
        Assert.Equal(expected, F(contacts, lists).CountText);

    private static ContactFolder Of(string account, FolderKind kind) =>
        new($"{account}:/x", "Name", "/x", "Home Gmail", "Google", kind, false);

    [Theory]
    [InlineData(FolderKind.CardDav, true)]
    [InlineData(FolderKind.GmailApi, true)]
    [InlineData(FolderKind.Local, true)]          // shown greyed
    [InlineData(FolderKind.GmailLabel, true)]     // works on that label's contacts
    [InlineData(FolderKind.GmailSignIn, false)]   // only points to Add Google account
    public void Only_selectable_kinds_get_a_checkbox(FolderKind kind, bool expected) =>
        Assert.Equal(expected, Of("a", kind).HasCheckbox);

    [Fact]
    public void Label_rows_hide_when_their_account_is_collapsed_and_nothing_else_does()
    {
        var collapsed = new HashSet<string> { "gmail-a" };

        Assert.False(FolderTree.IsVisible(Of("gmail-a", FolderKind.GmailLabel), collapsed));
        Assert.True(FolderTree.IsVisible(Of("gmail-a", FolderKind.GmailApi), collapsed));            // the main list stays
        Assert.True(FolderTree.IsVisible(Of("gmail-b", FolderKind.GmailLabel), collapsed));          // another account's labels stay
        Assert.True(FolderTree.IsVisible(Of("gmail-a", FolderKind.GmailLabel), new HashSet<string>()));
        Assert.True(FolderTree.IsVisible(Of("gmail-a", FolderKind.CardDav), collapsed));
    }

    [Fact]
    public void A_label_is_indented_under_its_main_list()
    {
        Assert.StartsWith("    ", Of("a", FolderKind.GmailLabel).ListName);
        Assert.Equal("Name", Of("a", FolderKind.GmailApi).ListName);
    }
}
