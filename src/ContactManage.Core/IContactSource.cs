namespace ContactManage.Core;

/// <summary>One implementation per mail client.</summary>
public interface IContactSource
{
    ClientInfo Detect();
    IReadOnlyList<ContactFolder> ListFolders();
}
