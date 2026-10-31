using System.Text.Json;
using System.Text.Json.Nodes;
using ContactManage.Core;

namespace ContactManage.Adapters.Google;

/// <summary>
/// Turns a People API person into a <see cref="Contact"/>. The fields the app understands become properties; every other field
/// (birthdays, URLs, events, relations, custom fields ...) is kept as one raw line per field, <c>X-GOOGLE-&lt;FIELD&gt;:&lt;json&gt;</c>,
/// in <see cref="Contact.Extra"/>, so merging and backups lose nothing and a later write-back can restore them.
/// </summary>
public static class GoogleContactMapper
{
    /// <summary>The editable person fields to ask for: everything a merge could lose.</summary>
    public const string PersonFields =
        "names,emailAddresses,phoneNumbers,addresses,organizations,biographies,memberships,birthdays,calendarUrls,clientData," +
        "events,externalIds,genders,imClients,interests,locales,locations,miscKeywords,nicknames,occupations,relations,sipAddresses,urls,userDefined";

    private static readonly HashSet<string> Handled = new(StringComparer.Ordinal)
    {
        "resourceName", "etag", "metadata", "names", "organizations", "emailAddresses", "phoneNumbers", "addresses",
        "biographies", "memberships", "photos", "coverPhotos",
    };

    // Google's phone and address types as vCard TYPE values, so the rest of the app can treat them like any other contact.
    private static readonly Dictionary<string, string> PhoneTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["mobile"] = "CELL", ["home"] = "HOME", ["work"] = "WORK", ["homeFax"] = "HOME,FAX", ["workFax"] = "WORK,FAX",
        ["otherFax"] = "FAX", ["pager"] = "PAGER", ["workMobile"] = "WORK,CELL", ["workPager"] = "WORK,PAGER", ["main"] = "MAIN",
    };

    /// <param name="labelNames">Resource name to label name, for the user's own labels only (not the built-in groups).</param>
    public static Contact FromPerson(JsonElement person, IReadOnlyDictionary<string, string> labelNames, string folderId)
    {
        var c = new Contact { SourceFolderId = folderId };
        c.RemoteId = c.Uid = Str(person, "resourceName");
        c.RemoteEtag = Str(person, "etag");

        var names = Items(person, "names").ToList();
        if (Primary(names) is { } n)
        {
            c.DisplayName = Str(n, "displayName");
            c.GivenName = Str(n, "givenName");
            c.FamilyName = Str(n, "familyName");
            c.MiddleName = Str(n, "middleName");
            c.NamePrefix = Str(n, "honorificPrefix");
            c.NameSuffix = Str(n, "honorificSuffix");
        }

        var orgs = Items(person, "organizations").ToList();
        if (Primary(orgs) is { } o)
        {
            c.Company = Str(o, "name");
            c.JobTitle = Str(o, "title");
            c.Department = Str(o, "department");
        }

        foreach (var e in Items(person, "emailAddresses")) Add(c, c.Emails, Str(e, "value"), TypeParam(e, null));
        foreach (var p in Items(person, "phoneNumbers")) Add(c, c.Phones, Str(p, "value"), TypeParam(p, PhoneTypes));
        foreach (var a in Items(person, "addresses")) Add(c, c.Addresses, Adr(a), TypeParam(a, null));

        c.Notes = Items(person, "biographies").Select(b => Str(b, "value")).FirstOrDefault(v => v is not null);

        foreach (var m in Items(person, "memberships"))
            if (m.TryGetProperty("contactGroupMembership", out var cg) && Str(cg, "contactGroupResourceName") is { } rn &&
                labelNames.TryGetValue(rn, out var label) && !c.Groups.Contains(label, StringComparer.OrdinalIgnoreCase))
                c.Groups.Add(label);

        // Whatever was not mapped, one compact line per field; and any second name or organization beyond the first.
        foreach (var field in person.EnumerateObject().Where(f => !Handled.Contains(f.Name) && f.Value.ValueKind == JsonValueKind.Array))
            c.Extra.Add($"X-GOOGLE-{field.Name.ToUpperInvariant()}:{field.Value.GetRawText()}");
        AddRest(c, "NAMES", names);
        AddRest(c, "ORGANIZATIONS", orgs);
        return c;
    }

    // ---- Contact to person, for writing back

    /// <param name="Person">The contact as the People API takes it (no memberships; the store adds those).</param>
    /// <param name="UpdateFields">The person fields an in-place update must set.</param>
    /// <param name="Skipped">Lines of the contact (vCard fields from other sources) that Gmail has no place for.</param>
    public sealed record PersonDraft(JsonObject Person, string UpdateFields, int Skipped);

    private static readonly string[] BaseFields = ["names", "emailAddresses", "phoneNumbers", "addresses", "organizations", "biographies"];

    // Output-only members of Google's items: sending them back is at best ignored.
    private static readonly string[] OutputOnly = ["metadata", "formattedType", "canonicalForm", "formattedValue"];

    private static readonly Dictionary<string, string> ExtraFieldNames =
        PersonFields.Split(',').Where(f => !Handled.Contains(f)).ToDictionary(f => f.ToUpperInvariant(), f => f, StringComparer.Ordinal);

    public static PersonDraft ToPerson(Contact c)
    {
        var person = new JsonObject();
        var fields = new List<string>(BaseFields);
        var skipped = 0;

        var name = new JsonObject();
        Put(name, "givenName", c.GivenName); Put(name, "familyName", c.FamilyName); Put(name, "middleName", c.MiddleName);
        Put(name, "honorificPrefix", c.NamePrefix); Put(name, "honorificSuffix", c.NameSuffix);
        if (name.Count == 0) Put(name, "unstructuredName", c.DisplayName);
        if (name.Count > 0) person["names"] = new JsonArray(name);

        var org = new JsonObject();
        Put(org, "name", c.Company); Put(org, "title", c.JobTitle); Put(org, "department", c.Department);
        var orgs = new JsonArray();
        if (org.Count > 0) orgs.Add(org);

        person["emailAddresses"] = ArrayOf(c.Emails, v => Typed(v, c, "value", EmailType));
        person["phoneNumbers"] = ArrayOf(c.Phones, v => Typed(v, c, "value", PhoneType));
        person["addresses"] = ArrayOf(c.Addresses, v => Address(v, c));
        if (!string.IsNullOrWhiteSpace(c.Notes))
            person["biographies"] = new JsonArray(new JsonObject { ["value"] = c.Notes, ["contentType"] = "TEXT_PLAIN" });

        var extras = new Dictionary<string, JsonArray>(StringComparer.Ordinal);
        foreach (var line in c.Extra)
        {
            var prop = VCard.PropertyName(line);
            var colon = line.IndexOf(':');
            if (prop.StartsWith("X-GOOGLE-", StringComparison.OrdinalIgnoreCase) && colon > 0)
            {
                var key = prop["X-GOOGLE-".Length..].ToUpperInvariant();
                if (key == "MORE-ORGANIZATIONS") { AddItems(orgs, line[(colon + 1)..]); continue; }
                if (key == "MORE-NAMES") continue;   // the user settled the name when merging
                if (ExtraFieldNames.TryGetValue(key, out var field))
                {
                    if (!extras.TryGetValue(field, out var list)) extras[field] = list = [];
                    AddItems(list, line[(colon + 1)..]);
                    continue;
                }
                skipped++;
            }
            else if (prop.Equals("BDAY", StringComparison.OrdinalIgnoreCase) && colon > 0 && Birthday(line[(colon + 1)..]) is { } date)
            {
                if (!extras.TryGetValue("birthdays", out var list)) extras["birthdays"] = list = [];
                if (list.Count == 0) list.Add(new JsonObject { ["date"] = date });   // one birthday: a Google one wins
            }
            else if (prop.Equals("URL", StringComparison.OrdinalIgnoreCase) && colon > 0)
            {
                if (!extras.TryGetValue("urls", out var list)) extras["urls"] = list = [];
                var url = line[(colon + 1)..].Trim();
                if (!list.Any(i => string.Equals(i?["value"]?.GetValue<string>(), url, StringComparison.OrdinalIgnoreCase)))
                    list.Add(new JsonObject { ["value"] = url });
            }
            else skipped++;
        }

        if (orgs.Count > 0) person["organizations"] = orgs;
        foreach (var (field, items) in extras)
        {
            person[field] = items;
            fields.Add(field);
        }
        return new PersonDraft(person, string.Join(",", fields), skipped);
    }

    private static void Put(JsonObject o, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) o[name] = value.Trim();
    }

    private static JsonArray ArrayOf(IEnumerable<string> values, Func<string, JsonObject?> make) =>
        new(values.Select(make).Where(o => o is not null).Select(o => (JsonNode)o!).ToArray());

    private static JsonObject? Typed(string value, Contact c, string key, Func<string, string?> type)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var o = new JsonObject { [key] = value.Trim() };
        if (c.Labels.TryGetValue(value, out var label) && type(label) is { } t) o["type"] = t;
        return o;
    }

    /// <summary>The TYPE values of a stored label such as ";TYPE=WORK,VOICE", upper-cased.</summary>
    private static HashSet<string> Types(string label) =>
        label.Split(';', ',', '=').Select(t => t.Trim().ToUpperInvariant()).Where(t => t.Length > 0 && t != "TYPE").ToHashSet();

    private static string? EmailType(string label)
    {
        var t = Types(label);
        return t.Contains("WORK") ? "work" : t.Contains("HOME") ? "home" : t.Contains("OTHER") ? "other" : null;
    }

    private static string? PhoneType(string label)
    {
        var t = Types(label);
        bool Has(string x) => t.Contains(x);
        if (Has("FAX")) return Has("HOME") ? "homeFax" : Has("WORK") ? "workFax" : "otherFax";
        if (Has("PAGER")) return Has("WORK") ? "workPager" : "pager";
        if (Has("CELL")) return Has("WORK") ? "workMobile" : "mobile";
        if (Has("MAIN")) return "main";
        return Has("WORK") ? "work" : Has("HOME") ? "home" : null;
    }

    private static JsonObject? Address(string value, Contact c)
    {
        var p = VCard.SplitStructured(value);
        string? At(int i) => i < p.Count && !string.IsNullOrWhiteSpace(p[i]) ? p[i].Trim() : null;
        var o = new JsonObject();
        Put(o, "poBox", At(0)); Put(o, "extendedAddress", At(1)); Put(o, "streetAddress", At(2)); Put(o, "city", At(3));
        Put(o, "region", At(4)); Put(o, "postalCode", At(5)); Put(o, "country", At(6));
        if (o.Count == 0) return null;
        if (c.Labels.TryGetValue(value, out var label) && EmailType(label) is { } t) o["type"] = t;
        return o;
    }

    private static JsonObject? Birthday(string value)
    {
        var v = value.Trim();
        if (DateTime.TryParseExact(v, ["yyyyMMdd", "yyyy-MM-dd"], System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d))
            return new JsonObject { ["year"] = d.Year, ["month"] = d.Month, ["day"] = d.Day };
        if (DateTime.TryParseExact("2000" + v.TrimStart('-').Replace("-", ""), "yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var md) && v.StartsWith("--"))
            return new JsonObject { ["month"] = md.Month, ["day"] = md.Day };
        return null;
    }

    /// <summary>Adds the items of a JSON array to <paramref name="target"/>, without Google's output-only members and without repeats.</summary>
    private static void AddItems(JsonArray target, string json)
    {
        JsonArray? items;
        try { items = JsonNode.Parse(json)?.AsArray(); }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { return; }
        foreach (var item in items ?? [])
        {
            if (item is not JsonObject o) continue;
            var copy = o.DeepClone().AsObject();
            foreach (var key in OutputOnly) copy.Remove(key);
            if (!target.Any(t => t?.ToJsonString() == copy.ToJsonString())) target.Add(copy);
        }
    }

    private static IEnumerable<JsonElement> Items(JsonElement person, string field) =>
        person.TryGetProperty(field, out var list) && list.ValueKind == JsonValueKind.Array ? list.EnumerateArray() : [];

    /// <summary>The entry Google marks as primary, else the first.</summary>
    private static JsonElement? Primary(List<JsonElement> items) =>
        items.Count == 0 ? null
        : items.FirstOrDefault(i => i.TryGetProperty("metadata", out var m) && m.TryGetProperty("primary", out var p) && p.ValueKind == JsonValueKind.True) is { ValueKind: JsonValueKind.Object } primary
            ? primary
            : items[0];

    private static void AddRest(Contact c, string name, List<JsonElement> items)
    {
        if (items.Count < 2) return;
        var primary = Primary(items)!.Value.GetRawText();
        var rest = items.Where(i => i.GetRawText() != primary).ToList();
        if (rest.Count > 0)
            c.Extra.Add($"X-GOOGLE-MORE-{name}:[{string.Join(",", rest.Select(i => i.GetRawText()))}]");
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()) ? v.GetString() : null;

    private static void Add(Contact c, List<string> list, string? value, string? param)
    {
        if (value is null) return;
        value = value.Trim();
        if (list.Contains(value, StringComparer.OrdinalIgnoreCase)) return;
        list.Add(value);
        if (param is not null) c.Labels[value] = param;
    }

    /// <summary>";TYPE=CELL"-style parameter from Google's type, or null when it has none.</summary>
    private static string? TypeParam(JsonElement item, Dictionary<string, string>? map)
    {
        var type = Str(item, "type");
        if (type is null) return null;
        return ";TYPE=" + (map is not null && map.TryGetValue(type, out var t) ? t : type.ToUpperInvariant());
    }

    /// <summary>A structured address as the vCard ADR value the rest of the app keeps: ";;street;city;region;postal;country".</summary>
    private static string? Adr(JsonElement a)
    {
        string Part(string name) => VCard.Escape(Str(a, name));
        var value = $";;{Part("streetAddress")};{Part("city")};{Part("region")};{Part("postalCode")};{Part("country")}";
        return value == ";;;;;;" ? Str(a, "formattedValue") is { } f ? $";;{VCard.Escape(f)};;;;" : null : value;
    }
}
