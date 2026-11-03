using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ContactManage.Core;

namespace ContactManage.Adapters.MicrosoftGraph;

/// <summary>
/// Turns a Graph contact into a <see cref="Contact"/> and back. The fields the app understands become properties; the other
/// properties of an Outlook contact (nickname, birthday, spouse, manager, IM addresses ...) are kept as one raw line each,
/// <c>X-MS-&lt;PROPERTY&gt;:&lt;json&gt;</c>, in <see cref="Contact.Extra"/>, so merging and backups lose nothing and a write-back can
/// restore them.
/// </summary>
public static class MicrosoftContactMapper
{
    // Properties kept as raw lines, with Graph's exact spelling.
    private static readonly string[] Passthrough =
    [
        "nickName", "initials", "birthday", "spouseName", "children", "assistantName", "manager", "officeLocation", "profession",
        "imAddresses", "businessHomePage", "fileAs", "yomiGivenName", "yomiSurname", "yomiCompanyName",
    ];

    private static readonly Dictionary<string, string> PassthroughByKey =
        Passthrough.ToDictionary(p => p.ToUpperInvariant(), p => p, StringComparer.Ordinal);

    public static Contact FromGraph(JsonElement item, string folderId)
    {
        var c = new Contact { SourceFolderId = folderId };
        c.RemoteId = c.Uid = Str(item, "id");
        c.RemoteEtag = Str(item, "changeKey");

        c.DisplayName = Str(item, "displayName");
        c.GivenName = Str(item, "givenName");
        c.FamilyName = Str(item, "surname");
        c.MiddleName = Str(item, "middleName");
        c.NamePrefix = Str(item, "title");
        c.NameSuffix = Str(item, "generation");
        c.Company = Str(item, "companyName");
        c.JobTitle = Str(item, "jobTitle");
        c.Department = Str(item, "department");
        c.Notes = Str(item, "personalNotes");

        if (item.TryGetProperty("emailAddresses", out var emails) && emails.ValueKind == JsonValueKind.Array)
            foreach (var e in emails.EnumerateArray()) Add(c, c.Emails, Str(e, "address"), null);

        foreach (var p in Strings(item, "businessPhones")) Add(c, c.Phones, p, ";TYPE=WORK");
        foreach (var p in Strings(item, "homePhones")) Add(c, c.Phones, p, ";TYPE=HOME");
        Add(c, c.Phones, Str(item, "mobilePhone"), ";TYPE=CELL");

        Add(c, c.Addresses, Adr(item, "homeAddress"), ";TYPE=HOME");
        Add(c, c.Addresses, Adr(item, "businessAddress"), ";TYPE=WORK");
        Add(c, c.Addresses, Adr(item, "otherAddress"), ";TYPE=OTHER");

        foreach (var category in Strings(item, "categories"))
            if (!c.Groups.Contains(category, StringComparer.OrdinalIgnoreCase)) c.Groups.Add(category);

        foreach (var name in Passthrough)
            if (item.TryGetProperty(name, out var v) && HasValue(name, v))
                c.Extra.Add($"X-MS-{name.ToUpperInvariant()}:{v.GetRawText()}");
        return c;
    }

    /// <summary>Graph reports "no birthday" as the year 1 and empty lists as [], neither of which is worth keeping.</summary>
    private static bool HasValue(string name, JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String => !string.IsNullOrWhiteSpace(v.GetString()) && !(name == "birthday" && v.GetString()!.StartsWith("0001-", StringComparison.Ordinal)),
        JsonValueKind.Array => v.GetArrayLength() > 0,
        JsonValueKind.Null or JsonValueKind.Undefined => false,
        _ => true,
    };

    // ---- Contact to Graph

    /// <param name="Body">The contact as Graph takes it.</param>
    /// <param name="Skipped">Lines of the contact (vCard fields from other sources) that an Outlook contact has no place for.</param>
    public sealed record GraphDraft(JsonObject Body, int Skipped);

    /// <param name="forUpdate">
    /// An update names every property the app manages, including the empty ones, so a value the user cleared while merging is cleared
    /// in Outlook too. A new contact only carries what it has.
    /// </param>
    public static GraphDraft ToGraph(Contact c, bool forUpdate)
    {
        var body = new JsonObject();
        void Text(string key, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) body[key] = value.Trim();
            else if (forUpdate) body[key] = null;
        }

        // Outlook may replace the display name by one it builds itself when other name parts change, so it is always sent.
        Text("displayName", string.IsNullOrWhiteSpace(c.DisplayName) ? $"{c.GivenName} {c.FamilyName}".Trim() : c.DisplayName);
        Text("givenName", c.GivenName);
        Text("middleName", c.MiddleName);
        Text("surname", c.FamilyName);
        Text("title", c.NamePrefix);
        Text("generation", c.NameSuffix);
        Text("companyName", c.Company);
        Text("jobTitle", c.JobTitle);
        Text("department", c.Department);
        Text("personalNotes", c.Notes);

        var slots = MicrosoftSlots.Of(c);
        if (slots.Emails.Count > 0 || forUpdate)
            body["emailAddresses"] = new JsonArray(slots.Emails.Select(e => (JsonNode)new JsonObject { ["name"] = e, ["address"] = e }).ToArray());
        if (slots.BusinessPhones.Count > 0 || forUpdate) body["businessPhones"] = Strings(slots.BusinessPhones);
        if (slots.HomePhones.Count > 0 || forUpdate) body["homePhones"] = Strings(slots.HomePhones);
        Text("mobilePhone", slots.MobilePhone);
        Address(body, "homeAddress", slots.HomeAddress, forUpdate);
        Address(body, "businessAddress", slots.BusinessAddress, forUpdate);
        Address(body, "otherAddress", slots.OtherAddress, forUpdate);
        if (c.Groups.Count > 0 || forUpdate) body["categories"] = Strings(c.Groups.Distinct(StringComparer.OrdinalIgnoreCase));

        var skipped = slots.Overflow;
        foreach (var line in c.Extra)
        {
            var prop = VCard.PropertyName(line);
            var colon = line.IndexOf(':');
            if (colon <= 0) { skipped++; continue; }
            var value = line[(colon + 1)..];

            if (prop.StartsWith("X-MS-", StringComparison.OrdinalIgnoreCase) && PassthroughByKey.TryGetValue(prop["X-MS-".Length..].ToUpperInvariant(), out var name))
            {
                if (body[name] is null && Parse(value) is { } node) body[name] = node;
            }
            else if (prop.Equals("BDAY", StringComparison.OrdinalIgnoreCase) && Birthday(value) is { } date)
            {
                if (body["birthday"] is null) body["birthday"] = date;
            }
            else if (prop.Equals("URL", StringComparison.OrdinalIgnoreCase))
            {
                if (body["businessHomePage"] is null) body["businessHomePage"] = value.Trim();
                else skipped++;   // Outlook keeps one web page
            }
            else skipped++;
        }
        return new GraphDraft(body, skipped);
    }

    private static JsonArray Strings(IEnumerable<string> values) => new(values.Select(v => (JsonNode)JsonValue.Create(v)!).ToArray());

    private static JsonNode? Parse(string json)
    {
        try { return JsonNode.Parse(json); }
        catch (JsonException) { return null; }
    }

    private static string? Birthday(string value)
    {
        var v = value.Trim();
        return DateTime.TryParseExact(v, ["yyyyMMdd", "yyyy-MM-dd"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d.ToString("yyyy-MM-dd'T'00:00:00'Z'", CultureInfo.InvariantCulture)
            : null;
    }

    // ---- addresses: Graph's street/city/state/postalCode/countryOrRegion <-> the vCard ADR value ";;street;city;region;postal;country"

    private static void Address(JsonObject body, string key, string? adr, bool forUpdate)
    {
        if (adr is null) { if (forUpdate) body[key] = null; return; }
        var p = VCard.SplitStructured(adr);
        string At(int i) => i < p.Count ? p[i].Trim() : "";
        var street = string.Join("\n", new[] { At(0), At(1), At(2) }.Where(s => s.Length > 0));
        var o = new JsonObject();
        void Put(string name, string value) { if (value.Length > 0) o[name] = value; }
        Put("street", street); Put("city", At(3)); Put("state", At(4)); Put("postalCode", At(5)); Put("countryOrRegion", At(6));
        if (o.Count > 0) body[key] = o;
        else if (forUpdate) body[key] = null;
    }

    private static string? Adr(JsonElement item, string key)
    {
        if (!item.TryGetProperty(key, out var a) || a.ValueKind != JsonValueKind.Object) return null;
        string Part(string name) => VCard.Escape(Str(a, name));
        var value = $";;{Part("street")};{Part("city")};{Part("state")};{Part("postalCode")};{Part("countryOrRegion")}";
        return value == ";;;;;;" ? null : value;
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()) ? v.GetString() : null;

    private static IEnumerable<string> Strings(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(i => i.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(i.GetString())).Select(i => i.GetString()!.Trim())
            : [];

    private static void Add(Contact c, List<string> list, string? value, string? param)
    {
        if (value is null) return;
        value = value.Trim();
        if (list.Contains(value, StringComparer.OrdinalIgnoreCase)) return;
        list.Add(value);
        if (param is not null) c.Labels[value] = param;
    }
}
