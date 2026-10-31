using ContactManage.Core;
using Microsoft.Data.Sqlite;

namespace ContactManage.Adapters.IceWarp;

/// <summary>
/// Reads the contacts of one folder of IceWarp's Local Folders store from <c>contact_index.dat</c>. Unlike the server folders this
/// is an ordinary relational layout: <c>ContactItems</c> holds the name fields and one child table per kind of value
/// (emails, telephones, addresses, homepages, instant messaging) plus <c>ContactCategoryNames</c> for labels. The vCard stored in
/// <c>contact_data.dat</c> is only a stub and is not used.
/// </summary>
public static class LocalContactReader
{
    // Labels Google adds to every cached contact; they say nothing about the user's own grouping.
    internal static readonly HashSet<string> BuiltInLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        "My Contacts", "Most Contacted", "Unfiled", "starred", "Other Contacts",
    };

    // IceWarp's phone purposes ("CELL_1", "FAXWORK_2" ...) as vCard TYPE values. Free-text purposes ("Personal_1") have no vCard type.
    private static readonly Dictionary<string, string> PhoneTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["CELL"] = "CELL", ["HOME"] = "HOME", ["WORK"] = "WORK", ["FAXHOME"] = "HOME,FAX", ["FAXWORK"] = "WORK,FAX",
        ["PAGER"] = "PAGER", ["MAIN"] = "MAIN",
    };

    private static readonly Dictionary<string, string> PlaceTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["HOME"] = "HOME", ["WORK"] = "WORK",
    };

    /// <param name="folderRowId"><c>Folders.id</c> of the folder in <c>folders.dat</c>, which is <c>ContactItems.folder</c>.</param>
    /// <param name="folderId">The id the app gives the folder; stored on each contact as its source.</param>
    public static IReadOnlyList<Contact> Read(string contactIndexPath, long folderRowId, string folderId)
    {
        using var copy = new SqliteCopy(contactIndexPath);
        using var conn = copy.Open();

        var contacts = new Dictionary<long, Contact>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                select id, componentUid, salutation, firstName, middleName, lastName, suffix, displayName, nickName,
                       comment, description, organization, title, prefix, department
                from ContactItems
                where folder = $folder and type = 0
                  and id not in (select id from NonUploadedAndMarkedForDeleteContactItems)
                order by id
                """;
            cmd.Parameters.AddWithValue("$folder", folderRowId);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var c = new Contact { SourceFolderId = folderId, RemoteId = r.GetInt64(0).ToString(), Uid = Text(r, 1) };
                c.NamePrefix = Text(r, 13) ?? Text(r, 2);
                c.GivenName = Text(r, 3);
                c.MiddleName = Text(r, 4);
                c.FamilyName = Text(r, 5);
                c.NameSuffix = Text(r, 6);
                c.DisplayName = Text(r, 7) ?? Join(c.GivenName, c.FamilyName);
                c.Notes = Text(r, 9) ?? Text(r, 10);
                c.Company = Text(r, 11);
                c.JobTitle = Text(r, 12);
                c.Department = Text(r, 14);
                if (Text(r, 8) is { } nick) c.Extra.Add("NICKNAME:" + VCard.Escape(nick));
                contacts[r.GetInt64(0)] = c;
            }
        }

        Each(conn, "select parentId, email, purpose from ContactEmails order by id", (c, r) =>
            Add(c, c.Emails, Text(r, 1), PlaceTypes, Text(r, 2)));

        Each(conn, "select parentId, telephone, purpose from ContactTelephones order by id", (c, r) =>
            Add(c, c.Phones, Text(r, 1), PhoneTypes, Text(r, 2)));

        Each(conn, "select parentId, street, city, county, zip, country, purpose from ContactAddresses order by id", (c, r) =>
        {
            var adr = AddressText(Text(r, 1), Text(r, 2), Text(r, 3), Text(r, 4), Text(r, 5));
            if (adr != ";;;;;;") Add(c, c.Addresses, adr, PlaceTypes, Text(r, 6));
        });

        Each(conn, "select parentId, url, purpose from ContactHomepages order by id", (c, r) =>
        {
            if (Text(r, 1) is { } url) c.Extra.Add("URL" + TypeParam(PlaceTypes, Text(r, 2)) + ":" + url);
        });

        Each(conn, "select parentId, uin, imType from ContactIM order by id", (c, r) =>
        {
            if (Text(r, 1) is { } uin) c.Extra.Add($"X-ICEWARP-IM;TYPE={r.GetInt64(2)}:{VCard.Escape(uin)}");
        });

        Each(conn, "select id, categoryName from ContactCategoryNames order by categoryId", (c, r) =>
        {
            if (Text(r, 1) is { } label && !BuiltInLabels.Contains(label) && !c.Groups.Contains(label, StringComparer.OrdinalIgnoreCase))
                c.Groups.Add(label);
        });

        return contacts.Values.ToList();

        void Each(SqliteConnection connection, string sql, Action<Contact, SqliteDataReader> use)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            using var r = cmd.ExecuteReader();
            while (r.Read())
                if (contacts.TryGetValue(r.GetInt64(0), out var c)) use(c, r);
        }
    }

    /// <summary>An IceWarp address row as the ADR value the rest of the app uses (";;street;city;region;postal;country").</summary>
    internal static string AddressText(string? street, string? city, string? county, string? zip, string? country) =>
        $";;{VCard.Escape(street)};{VCard.Escape(city)};{VCard.Escape(county)};{VCard.Escape(zip)};{VCard.Escape(country)}";

    private static void Add(Contact c, List<string> list, string? value, Dictionary<string, string> types, string? purpose)
    {
        if (value is null || list.Contains(value, StringComparer.OrdinalIgnoreCase)) return;
        list.Add(value);
        var param = TypeParam(types, purpose);
        if (param.Length > 0) c.Labels[value] = param;
    }

    /// <summary>";TYPE=WORK" from a purpose such as "WORK_2"; empty when the purpose is free text or absent.</summary>
    private static string TypeParam(Dictionary<string, string> types, string? purpose)
    {
        if (purpose is null) return "";
        var us = purpose.LastIndexOf('_');
        var key = us > 0 && purpose[(us + 1)..].All(char.IsDigit) ? purpose[..us] : purpose;
        return types.TryGetValue(key, out var t) ? ";TYPE=" + t : "";
    }

    private static string? Text(SqliteDataReader r, int i) =>
        r.IsDBNull(i) || string.IsNullOrWhiteSpace(r.GetValue(i).ToString()) ? null : r.GetValue(i).ToString()!.Trim();

    private static string? Join(string? a, string? b) =>
        string.Join(' ', new[] { a, b }.Where(s => s is not null)) is { Length: > 0 } s ? s : null;
}
