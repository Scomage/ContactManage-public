namespace ContactManage.Core;

/// <summary>
/// The source used when the user works with online accounts only. It reads no mail program, so it lists no folders: the accounts
/// (Gmail, Microsoft) are added to the list by their own store once the user has signed in.
/// </summary>
public sealed class OnlineContactSource : IContactSource
{
    public ClientInfo Detect() => new("online", "Online accounts", null, true);

    public IReadOnlyList<ContactFolder> ListFolders() => [];
}
