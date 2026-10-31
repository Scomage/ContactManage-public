namespace ContactManage.Core;

/// <summary>Single-value fields. When merged contacts disagree on one of these, the user resolves a conflict.</summary>
public enum ContactField
{
    DisplayName,
    GivenName,
    FamilyName,
    Company,
    JobTitle,
    Notes,
}

/// <summary>Where one original contact lived, so a write-back can update one of a merged contact's sources in place.</summary>
public sealed record ContactSource(string? FolderId, string? RemoteId, string? Etag, string? Uid = null);

public sealed class Contact
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string? SourceFolderId { get; set; }

    public string? DisplayName { get; set; }
    public string? GivenName { get; set; }
    public string? FamilyName { get; set; }
    public string? Company { get; set; }
    public string? JobTitle { get; set; }
    public string? Notes { get; set; }

    /// <summary>Parts of the vCard N property that the merge does not compare. Kept so a write-back loses nothing.</summary>
    public string? NamePrefix { get; set; }
    public string? MiddleName { get; set; }
    public string? NameSuffix { get; set; }
    public string? Department { get; set; }

    /// <summary>The vCard UID. Merged contacts have none until written.</summary>
    public string? Uid { get; set; }

    /// <summary>Where the contact lives on its server (the href), so a write can leave unchanged contacts alone.</summary>
    public string? RemoteId { get; set; }

    /// <summary>Gmail labels (contact groups) the contact belongs to, by name. Merged contacts carry the labels of every member.</summary>
    public List<string> Groups { get; } = [];

    /// <summary>The server's version tag of the contact when it was read (Gmail needs it to update the contact later).</summary>
    public string? RemoteEtag { get; set; }

    /// <summary>Raw vCard parameters per value (for example ";TYPE=WORK,VOICE"), keyed by the exact text in Emails, Phones or Addresses.</summary>
    public Dictionary<string, string> Labels { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Raw vCard lines the model does not understand (birthday, URL, categories, photo, X- fields). Carried through unchanged.</summary>
    public List<string> Extra { get; } = [];

    public List<string> Emails { get; } = [];
    public List<string> Phones { get; } = [];
    public List<string> Addresses { get; } = [];

    /// <summary>For a merged contact: the ids of the contacts it was built from. Empty for originals.</summary>
    public List<string> SourceIds { get; } = [];

    /// <summary>For a merged contact: where each of its originals lived (folder, server id, version tag).</summary>
    public List<ContactSource> Sources { get; } = [];

    public string? Get(ContactField field) => field switch
    {
        ContactField.DisplayName => DisplayName,
        ContactField.GivenName => GivenName,
        ContactField.FamilyName => FamilyName,
        ContactField.Company => Company,
        ContactField.JobTitle => JobTitle,
        ContactField.Notes => Notes,
        _ => throw new ArgumentOutOfRangeException(nameof(field)),
    };

    public void Set(ContactField field, string? value)
    {
        switch (field)
        {
            case ContactField.DisplayName: DisplayName = value; break;
            case ContactField.GivenName: GivenName = value; break;
            case ContactField.FamilyName: FamilyName = value; break;
            case ContactField.Company: Company = value; break;
            case ContactField.JobTitle: JobTitle = value; break;
            case ContactField.Notes: Notes = value; break;
            default: throw new ArgumentOutOfRangeException(nameof(field));
        }
    }
}
