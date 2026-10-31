using System.Text.RegularExpressions;
using ContactManage.Core;
using Microsoft.Data.Sqlite;

namespace ContactManage.Adapters.IceWarp;

/// <summary>
/// Changes the contacts of one folder of IceWarp's Local Folders in place. This is the only code that writes to the client's own
/// databases, so it is deliberately narrow:
/// <list type="bullet">
/// <item>It works on <c>contact_index.dat</c> (the rows) and <c>contact_data.dat</c> (name and comment search, the stub card)
/// through one connection and one transaction, so a failure leaves both files as they were.</item>
/// <item>It uses ordinary locking, so IceWarp Desktop Client may stay open: SQLite serialises the two writers and this code waits
/// up to 15 seconds for the lock. The open client picked the change up straight away on the first live run, without a restart.</item>
/// <item>It refuses a database layout it does not know (<see cref="SupportedIndexVersion"/>), checks before it commits that the
/// search rows still match the contact rows, and rolls back if they do not.</item>
/// <item>Email, address and IM search tables are kept right by IceWarp's own triggers, so plain SQL is enough for them; the name and
/// comment search rows have no triggers and are maintained here.</item>
/// </list>
/// </summary>
internal static class LocalContactWriter
{
    /// <summary>PRAGMA user_version of contact_index.dat on the IceWarp Desktop Client this was written against.</summary>
    public const long SupportedIndexVersion = 17;

    private static readonly string[] IndexTables =
    [
        "ContactItems", "ContactEmails", "ContactTelephones", "ContactAddresses", "ContactHomepages", "ContactIM",
        "ContactLanguages", "ContactKeys", "ContactCategoryNames", "NonUploadedAndMarkedForDeleteContactItems",
    ];

    private static readonly string[] DataTables = ["NameIndex", "CommentIndex", "ContactItemContents"];

    private static readonly string[] FtsIndexes = ["ContactEmailIndex", "ContactAddressIndex", "ContactIMIndex"];

    private static SqliteConnection Open(string path)
    {
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
        conn.Open();
        return conn;
    }

    /// <summary>Throws when the files are not a layout this code knows, before anything is backed up or changed.</summary>
    public static void Preflight(string indexPath, string dataPath)
    {
        using (var conn = Open(indexPath))
        {
            var db = new Db(conn);
            db.Exec("pragma busy_timeout = 15000");
            var version = Convert.ToInt64(db.One("pragma user_version"));
            if (version != SupportedIndexVersion)
                throw new InvalidOperationException($"This IceWarp Desktop Client stores Local Folders in a layout ContactManage does not know (version {version}, expected {SupportedIndexVersion}). Nothing was changed.");
            RequireTables(db, IndexTables);
        }
        using (var conn = Open(dataPath))
        {
            var db = new Db(conn);
            db.Exec("pragma busy_timeout = 15000");
            RequireTables(db, DataTables);
        }
        SqliteConnection.ClearAllPools();
    }

    private static void RequireTables(Db db, IEnumerable<string> tables)
    {
        foreach (var t in tables)
            if (db.One("select 1 from sqlite_master where name = ?", t) is null)
                throw new InvalidOperationException($"IceWarp's Local Folders database has no table {t}; this layout is not supported. Nothing was changed.");
    }

    /// <summary>
    /// Copies both databases to <paramref name="targetDir"/> with SQLite's online backup, which gives a consistent snapshot even
    /// while IceWarp is running (a plain file copy of a live database can be torn). Restoring: close IceWarp, delete the
    /// two files and their -wal and -shm companions from the Local Folders directory, and copy these two files there.
    /// </summary>
    public static void Backup(string indexPath, string dataPath, string targetDir)
    {
        Directory.CreateDirectory(targetDir);
        foreach (var source in new[] { indexPath, dataPath })
        {
            using var from = Open(source);
            new Db(from).Exec("pragma busy_timeout = 15000");
            using var to = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(targetDir, Path.GetFileName(source)), Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false,
            }.ToString());
            to.Open();
            from.BackupDatabase(to);
        }
        SqliteConnection.ClearAllPools();
    }

    public static WriteReport Replace(string indexPath, string dataPath, long folderRow, string folderId, string folderName, IReadOnlyList<Contact> contacts)
    {
        try
        {
            using var conn = Open(indexPath);
            var db = new Db(conn);
            db.Exec("pragma busy_timeout = 15000");
            db.Exec("attach database ? as d", dataPath);
            db.Exec("begin immediate");
            try
            {
                var report = new Applier(db, folderRow, folderId, folderName).Apply(contacts);
                db.Exec("commit");
                return report;
            }
            catch
            {
                try { db.Exec("rollback"); } catch (SqliteException) { /* no transaction was open */ }
                throw;
            }
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode is 5 or 6)
        {
            throw new InvalidOperationException("IceWarp Desktop Client is using its contact database right now and did not let go for 15 seconds. Nothing was changed; try again in a moment.", ex);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    // ---- the work, inside the transaction

    private sealed class Applier(Db db, long folderRow, string folderId, string folderName)
    {
        private readonly long _now = DateTime.UtcNow.Ticks;
        private readonly HashSet<string> _skipped = new(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, object?>? _template;
        private byte[]? _card;
        private string _uidPrefix = "urn:uuid:";
        private bool _addMyContacts;

        public WriteReport Apply(IReadOnlyList<Contact> contacts)
        {
            var rows = db.Rows("select id, componentUid from ContactItems where folder = ? and type = 0 and id not in (select id from NonUploadedAndMarkedForDeleteContactItems)", folderRow);
            if (contacts.Count == 0 && rows.Count > 0)
                throw new InvalidOperationException("Refusing to empty a folder: the merged list has no contacts.");

            var existing = rows.ToDictionary(r => Convert.ToInt64(r[0]), r => r[1] as string);
            var existingIds = existing.Keys.Select(k => k.ToString()).ToHashSet(StringComparer.Ordinal);
            var byUid = existing.Where(kv => !string.IsNullOrEmpty(kv.Value)).GroupBy(kv => kv.Value!, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Key, StringComparer.Ordinal);

            // The same rules as the CardDAV store: untouched contacts stay, a merged contact is written over one of its own originals,
            // the rest are added, and only contacts nobody claims are removed.
            bool IsUntouched(Contact c) => c.Sources.Count == 0 && c.SourceFolderId == folderId && c.RemoteId is not null && existingIds.Contains(c.RemoteId);
            var keep = contacts.Where(IsUntouched).Select(c => long.Parse(c.RemoteId!)).ToHashSet();
            var untouched = keep.Count;

            var updates = new List<(Contact Contact, long Id)>();
            var adds = new List<Contact>();
            foreach (var c in contacts.Where(c => !IsUntouched(c)))
            {
                long? target = null;
                foreach (var s in c.Sources)
                    if (s.FolderId == folderId && long.TryParse(s.RemoteId, out var sid) && existing.ContainsKey(sid) && !keep.Contains(sid)) { target = sid; break; }
                // A contact read back from a backup carries its old UID; if that row is still here it is updated, with its picture, not duplicated.
                if (target is null && c.Sources.Count == 0 && c.Uid is { } uid && byUid.TryGetValue(uid, out var found) && !keep.Contains(found)) target = found;
                if (target is null) { adds.Add(c); continue; }
                keep.Add(target.Value);
                updates.Add((c, target.Value));
            }
            var removals = existing.Keys.Where(k => !keep.Contains(k)).ToList();

            LoadTemplates(existing.Count);
            var baselineConsistent = Consistent();
            var baselineFts = FtsHealthy();

            foreach (var (c, id) in updates) Update(id, c);
            foreach (var c in adds) Insert(c);
            foreach (var id in removals) Delete(id);

            if (baselineConsistent && !Consistent())
                throw new InvalidOperationException("IceWarp's name search rows would not match its contacts after this change. Nothing was changed.");
            if (baselineFts && !FtsHealthy())
                throw new InvalidOperationException("IceWarp's search tables would be inconsistent after this change. Nothing was changed.");

            var after = Convert.ToInt32(db.One("select count(*) from ContactItems where folder = ? and type = 0 and id not in (select id from NonUploadedAndMarkedForDeleteContactItems)", folderRow));
            var expected = untouched + updates.Count + adds.Count;
            if (after != expected)
                throw new InvalidOperationException($"The folder would hold {after:N0} contacts instead of the expected {expected:N0}. Nothing was changed.");

            var note = _skipped.Count == 0 ? null
                : $"IceWarp's Local Folders have no place for some details, which were left out: {string.Join(", ", _skipped.Order().Take(8))}" + (_skipped.Count > 8 ? " and more." : ".");
            return new WriteReport(folderName, adds.Count, removals.Count, untouched, after, updates.Count, note);
        }

        private void LoadTemplates(int existingCount)
        {
            _template = db.Row("select * from ContactItems where folder = ? and type = 0 order by id limit 1", folderRow);
            _card = db.One("select properties from d.ContactItemContents where propertyName = 'card' limit 1") as byte[];
            if (db.One("select componentUid from ContactItems where folder = ? and length(componentUid) > 36 limit 1", folderRow) is string sample)
                _uidPrefix = sample[..^36];
            var withMy = Convert.ToInt32(db.One("select count(distinct id) from ContactCategoryNames where categoryName = 'My Contacts' and id in (select id from ContactItems where folder = ? and type = 0)", folderRow));
            _addMyContacts = existingCount > 0 && withMy * 2 >= existingCount;
        }

        // ---- scalar fields

        private static string Clean(string? s) => string.IsNullOrWhiteSpace(s) ? "" : s.Trim();

        private sealed record Extras(string? Nick, List<(string Url, string Type)> Urls, List<(string Uin, long Type)> Ims);

        private Extras ParseExtra(Contact c)
        {
            string? nick = null;
            var urls = new List<(string, string)>();
            var ims = new List<(string, long)>();
            foreach (var line in c.Extra)
            {
                var colon = line.IndexOf(':');
                var name = VCard.PropertyName(line);
                if (colon < 0) { _skipped.Add(name); continue; }
                var head = line[..colon];
                var value = line[(colon + 1)..];
                var type = Regex.Match(head, @"TYPE=([^;:]*)", RegexOptions.IgnoreCase) is { Success: true } m ? m.Groups[1].Value : "";
                switch (name)
                {
                    case "NICKNAME": nick ??= VCard.Unescape(value); break;
                    case "URL": if (Clean(value).Length > 0) urls.Add((value.Trim(), type)); break;
                    case "X-ICEWARP-IM": if (Clean(value).Length > 0) ims.Add((VCard.Unescape(value).Trim(), long.TryParse(type, out var t) ? t : 0)); break;
                    case "CATEGORIES": break; // labels are in Contact.Groups
                    default: _skipped.Add(name); break;
                }
            }
            return new Extras(nick, urls, ims);
        }

        private Dictionary<string, string> Fields(Contact c, Extras e, string fallbackNick, string fallbackFileAs, string oldDisplay)
        {
            var display = Clean(c.DisplayName);
            if (display.Length == 0) display = Clean(string.Join(' ', new[] { c.GivenName, c.FamilyName }.Where(s => !string.IsNullOrWhiteSpace(s))));
            return new Dictionary<string, string>
            {
                ["firstName"] = Clean(c.GivenName),
                ["middleName"] = Clean(c.MiddleName),
                ["lastName"] = Clean(c.FamilyName),
                ["suffix"] = Clean(c.NameSuffix),
                ["displayName"] = display,
                ["nickName"] = e.Nick is null ? fallbackNick : Clean(e.Nick),
                ["fileAs"] = display == oldDisplay ? fallbackFileAs : display,
                ["comment"] = Clean(c.Notes),
                ["organization"] = Clean(c.Company),
                ["title"] = Clean(c.JobTitle),
                ["prefix"] = Clean(c.NamePrefix),
                ["department"] = Clean(c.Department),
            };
        }

        private void RefreshSearch(long id)
        {
            var r = db.Row("select firstName, middleName, lastName, fileAs, displayName, nickName, prefix, title, organization, comment from ContactItems where id = ?", id)!;
            string S(string col) => r[col] as string ?? "";
            db.Exec("delete from d.NameIndex where rowid = ?", id);
            db.Exec("insert into d.NameIndex(rowid, firstName, middleName, lastName, fileAs, displayName, nickName, prefix, title, organization) values (?,?,?,?,?,?,?,?,?,?)",
                id, S("firstName"), S("middleName"), S("lastName"), S("fileAs"), S("displayName"), S("nickName"), S("prefix"), S("title"), S("organization"));
            db.Exec("delete from d.CommentIndex where rowid = ?", id);
            db.Exec("insert into d.CommentIndex(rowid, plainTextComment) values (?,?)", id, S("comment"));
        }

        // ---- update, insert, delete

        private void Update(long id, Contact c)
        {
            var old = db.Row("select * from ContactItems where id = ?", id)!;
            string O(string col) => old[col] as string ?? "";
            var extras = ParseExtra(c);
            var want = Fields(c, extras, O("nickName"), O("fileAs"), O("displayName"));
            var changed = want.Where(kv => kv.Value != O(kv.Key)).ToList();
            if (changed.Count > 0)
            {
                var args = changed.Select(kv => (object?)kv.Value).Append(_now).Append(id).ToArray();
                db.Exec($"update ContactItems set {string.Join(", ", changed.Select(kv => kv.Key + " = ?"))}, updated = ? where id = ?", args);
                RefreshSearch(id);
            }
            SyncChildren(id, c, extras);
        }

        private void Insert(Contact c)
        {
            var extras = ParseExtra(c);
            var want = Fields(c, extras, "", "", "");
            if (want["fileAs"].Length == 0) want["fileAs"] = want["displayName"];

            var uid = c.Uid is { } u && u.Length == _uidPrefix.Length + 36 && u.StartsWith(_uidPrefix, StringComparison.Ordinal) ? u : _uidPrefix + Guid.NewGuid();
            var values = new Dictionary<string, object?>
            {
                ["uniqueId"] = null, ["versionId"] = null, ["avatarHash"] = null, ["birthdayDay"] = null, ["anniversaryDay"] = null,
                ["folder"] = folderRow, ["type"] = 0L, ["componentUid"] = uid, ["created"] = _now, ["updated"] = _now,
                ["sendCount"] = 0L, ["lastSend"] = 0L,
            };
            if (_template is null) values["gender"] = 2L;
            foreach (var kv in want) values[kv.Key] = kv.Value;

            var id = InsertRow("ContactItems", values, _template);
            RefreshSearch(id);
            db.Exec("delete from d.ContactItemContents where id = ?", id);
            if (_card is not null)
                db.Exec("insert into d.ContactItemContents(id, propertyName, propertyId, properties) values (?, 'card', '', ?)", id, _card);
            SyncChildren(id, c, extras);
        }

        private void Delete(long id)
        {
            foreach (var table in new[] { "ContactEmails", "ContactTelephones", "ContactAddresses", "ContactHomepages", "ContactIM", "ContactLanguages", "ContactKeys" })
                db.Exec($"delete from {table} where parentId = ?", id);
            db.Exec("delete from ContactCategoryNames where id = ?", id);
            db.Exec("delete from ContactItems where id = ?", id);
            db.Exec("delete from d.NameIndex where rowid = ?", id);
            db.Exec("delete from d.CommentIndex where rowid = ?", id);
            db.Exec("delete from d.ContactItemContents where id = ?", id);
        }

        // ---- values: emails, phones, addresses, homepages, instant messaging, labels

        private sealed record Wanted(string Value, string Original);

        private static List<Wanted> Distinct(IEnumerable<string> originals, StringComparer comparer) =>
            originals.Select(o => new Wanted(o.Trim(), o)).Where(w => w.Value.Length > 0).DistinctBy(w => w.Value, comparer).ToList();

        private static HashSet<string> Types(Contact c, string original)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!c.Labels.TryGetValue(original, out var parameters)) return set;
            foreach (var part in parameters.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var list = part.StartsWith("TYPE=", StringComparison.OrdinalIgnoreCase) ? part[5..] : part.Contains('=') ? "" : part;
                foreach (var t in list.Split(',', StringSplitOptions.RemoveEmptyEntries)) set.Add(t.Trim().Trim('"'));
            }
            return set;
        }

        private static string PlaceKey(HashSet<string> types, string otherwise) =>
            types.Contains("WORK") ? "WORK" : types.Contains("HOME") ? "HOME" : otherwise;

        private static string PhoneKey(HashSet<string> types) =>
            types.Contains("FAX") ? (types.Contains("HOME") ? "FAXHOME" : "FAXWORK")
            : types.Contains("CELL") ? "CELL"
            : types.Contains("PAGER") ? "PAGER"
            : types.Contains("MAIN") ? "main"
            : types.Contains("WORK") ? "WORK" : types.Contains("HOME") ? "HOME" : "OTHER";

        private void SyncChildren(long id, Contact c, Extras e)
        {
            Sync("ContactEmails", id, Distinct(c.Emails, StringComparer.OrdinalIgnoreCase), true, true,
                "select id, email, purpose from ContactEmails where parentId = ? order by id", r => r[1] as string ?? "",
                w => PlaceKey(Types(c, w.Original), "EMAIL"),
                (w, purpose, primary) => new() { ["email"] = w.Value, ["purpose"] = purpose, ["isPrimary"] = primary });

            Sync("ContactTelephones", id, Distinct(c.Phones, StringComparer.Ordinal), true, true,
                "select id, telephone, purpose from ContactTelephones where parentId = ? order by id", r => (r[1] as string ?? "").Trim(),
                w => PhoneKey(Types(c, w.Original)),
                (w, purpose, primary) => new() { ["telephone"] = w.Value, ["purpose"] = purpose, ["isPrimary"] = primary });

            Sync("ContactAddresses", id, Distinct(c.Addresses.Where(a => a.Trim() != ";;;;;;"), StringComparer.OrdinalIgnoreCase), true, true,
                "select id, coalesce(street,'') || char(31) || coalesce(city,'') || char(31) || coalesce(county,'') || char(31) || coalesce(zip,'') || char(31) || coalesce(country,''), purpose " +
                "from ContactAddresses where parentId = ? order by id",
                r => AddressKey(r[1] as string ?? ""),
                w => PlaceKey(Types(c, w.Original), "OTHER"),
                (w, purpose, primary) =>
                {
                    var p = AddressParts(w.Value);
                    return new() { ["street"] = p[0], ["city"] = p[1], ["county"] = p[2], ["zip"] = p[3], ["country"] = p[4], ["purpose"] = purpose, ["isPrimary"] = primary };
                },
                matchKey: w => AddressKey(string.Join('\u001f', AddressParts(w.Value))));

            // Homepages and instant-messaging entries are only ever added: a survivor keeps what it had.
            Sync("ContactHomepages", id, Distinct(e.Urls.Select(u => u.Url), StringComparer.OrdinalIgnoreCase), false, false,
                "select id, url, purpose from ContactHomepages where parentId = ? order by id", r => r[1] as string ?? "",
                w => PlaceKey(new HashSet<string>(e.Urls.First(u => u.Url == w.Original).Type.Split(','), StringComparer.OrdinalIgnoreCase), "HOMEPAGE"),
                (w, purpose, _) => new() { ["url"] = w.Value, ["purpose"] = purpose });

            Sync("ContactIM", id, Distinct(e.Ims.Select(i => i.Uin), StringComparer.OrdinalIgnoreCase), false, false,
                "select id, uin, purpose from ContactIM where parentId = ? order by id", r => r[1] as string ?? "",
                _ => "IM",
                (w, _, _) => new() { ["displayName"] = w.Value, ["imType"] = e.Ims.First(i => i.Uin == w.Original).Type, ["uin"] = w.Value, ["specificUin"] = w.Value, ["contactType"] = 1L });

            SyncLabels(id, c);
        }

        /// <summary>Street, city, region, postal code and country of an ADR value; the PO box and extended parts have no column.</summary>
        private static string[] AddressParts(string adr)
        {
            var p = VCard.SplitStructured(adr);
            string P(int i) => i < p.Count ? p[i].Trim() : "";
            return [P(2), P(3), P(4), P(5), P(6)];
        }

        /// <summary>The comparable form of an address: the same text the reader makes from a row. Input is five parts joined by U+001F.</summary>
        private static string AddressKey(string joined)
        {
            var p = joined.Split('\u001f');
            string P(int i) => i < p.Length ? p[i].Trim() : "";
            return LocalContactReader.AddressText(P(0), P(1), P(2), P(3), P(4));
        }

        private void Sync(string table, long parent, List<Wanted> wanted, bool allowDelete, bool hasPrimary, string rowsSql,
            Func<object?[], string> keyOf, Func<Wanted, string> purposeKey, Func<Wanted, string, long, Dictionary<string, object?>> makeRow,
            Func<Wanted, string>? matchKey = null)
        {
            var match = matchKey ?? (w => w.Value);
            var rows = db.Rows(rowsSql, parent).Select(r => (Id: Convert.ToInt64(r[0]), Key: keyOf(r), Purpose: r[2] as string ?? "")).ToList();
            var wantedKeys = new HashSet<string>(wanted.Select(match), StringComparer.OrdinalIgnoreCase);

            if (allowDelete)
                foreach (var gone in rows.Where(r => !wantedKeys.Contains(r.Key)))
                    db.Exec($"delete from {table} where id = ?", gone.Id);
            var kept = rows.Where(r => !allowDelete || wantedKeys.Contains(r.Key)).ToList();
            var haveKeys = kept.Select(r => r.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var purposes = kept.Select(r => r.Purpose).ToList();

            var hasRows = kept.Count > 0;
            foreach (var w in wanted.Where(w => !haveKeys.Contains(match(w))))
            {
                var key = purposeKey(w);
                var next = purposes.Where(p => p.StartsWith(key + "_", StringComparison.OrdinalIgnoreCase) && int.TryParse(p[(key.Length + 1)..], out _))
                    .Select(p => int.Parse(p[(key.Length + 1)..])).DefaultIfEmpty(0).Max() + 1;
                var purpose = $"{key}_{next}";
                purposes.Add(purpose);
                var row = makeRow(w, purpose, hasRows ? 100L : 1L);
                row["parentId"] = parent;
                if (!hasPrimary) row.Remove("isPrimary");
                InsertRow(table, row, null);
                hasRows = true;
            }

            // Deleting the primary entry must leave another one primary: IceWarp reads the first row marked 1.
            if (hasPrimary && allowDelete)
                db.Exec($"update {table} set isPrimary = 1 where id = (select min(id) from {table} where parentId = ?) and not exists (select 1 from {table} where parentId = ? and isPrimary = 1)", parent, parent);
        }

        private void SyncLabels(long id, Contact c)
        {
            var wanted = c.Groups.Select(g => g.Trim()).Where(g => g.Length > 0 && !LocalContactReader.BuiltInLabels.Contains(g)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var rows = db.Rows("select categoryId, categoryName from ContactCategoryNames where id = ?", id).Select(r => (Id: Convert.ToInt64(r[0]), Name: r[1] as string ?? "")).ToList();
            foreach (var gone in rows.Where(r => !LocalContactReader.BuiltInLabels.Contains(r.Name) && !wanted.Contains(r.Name, StringComparer.OrdinalIgnoreCase)))
                db.Exec("delete from ContactCategoryNames where categoryId = ?", gone.Id);
            var have = rows.Select(r => r.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (rows.Count == 0 && _addMyContacts) wanted.Add("My Contacts"); // every contact of this folder carries Google's own label
            foreach (var name in wanted.Where(n => !have.Contains(n)))
                InsertRow("ContactCategoryNames", new() { ["id"] = id, ["categoryName"] = name }, null);
        }

        // ---- plumbing

        private readonly Dictionary<string, List<Col>> _columns = new();

        private sealed record Col(string Name, string Type, bool HasDefault, bool IsKey)
        {
            public bool IsText => Type.Equals("TEXT", StringComparison.OrdinalIgnoreCase);
        }

        private List<Col> Columns(string table)
        {
            if (_columns.TryGetValue(table, out var cols)) return cols;
            cols = db.Rows($"select name, type, dflt_value, pk from pragma_table_info('{table}')")
                .Select(r => new Col((string)r[0]!, (r[1] as string) ?? "", r[2] is not null, Convert.ToInt32(r[3]) > 0)).ToList();
            return _columns[table] = cols;
        }

        /// <summary>
        /// Inserts one row. Columns given in <paramref name="values"/> are used; the key column is left to SQLite; other text columns
        /// are empty text (as IceWarp writes them); columns with a default in the table keep it; remaining numbers come from the
        /// template row, or 0.
        /// </summary>
        private long InsertRow(string table, Dictionary<string, object?> values, Dictionary<string, object?>? template)
        {
            var names = new List<string>();
            var args = new List<object?>();
            foreach (var col in Columns(table))
            {
                if (col.IsKey) continue;
                object? v;
                if (values.TryGetValue(col.Name, out var given)) v = given;
                else if (col.IsText && !col.HasDefault) v = "";
                else if (col.HasDefault) continue;
                else v = template is not null && template.TryGetValue(col.Name, out var t) && t is not null ? t : 0L;
                names.Add(col.Name);
                args.Add(v);
            }
            db.Exec($"insert into {table} ({string.Join(", ", names)}) values ({string.Join(", ", names.Select(_ => "?"))})", args.ToArray());
            return Convert.ToInt64(db.One("select last_insert_rowid()"));
        }

        // ---- checks

        private bool Consistent()
        {
            HashSet<long> Ids(string sql) => db.Rows(sql).Select(r => Convert.ToInt64(r[0])).ToHashSet();
            var items = Ids("select id from ContactItems");
            return items.SetEquals(Ids("select rowid from d.NameIndex")) && items.SetEquals(Ids("select rowid from d.CommentIndex"))
                && Ids("select distinct id from d.ContactItemContents").IsSubsetOf(items);
        }

        private bool FtsHealthy()
        {
            foreach (var index in FtsIndexes)
            {
                try { db.Exec($"insert into {index}({index}, rank) values('integrity-check', 1)"); }
                catch (SqliteException) { return false; }
            }
            return true;
        }
    }

    // ---- thin SQL helper

    private sealed class Db(SqliteConnection conn)
    {
        private SqliteCommand Make(string sql, object?[] args)
        {
            var cmd = conn.CreateCommand();
            var i = 0;
            cmd.CommandText = Regex.Replace(sql, @"\?", _ => "@p" + i++);
            for (var k = 0; k < args.Length; k++) cmd.Parameters.AddWithValue("@p" + k, args[k] ?? DBNull.Value);
            return cmd;
        }

        public int Exec(string sql, params object?[] args)
        {
            using var cmd = Make(sql, args);
            return cmd.ExecuteNonQuery();
        }

        public object? One(string sql, params object?[] args)
        {
            using var cmd = Make(sql, args);
            var v = cmd.ExecuteScalar();
            return v is DBNull ? null : v;
        }

        public List<object?[]> Rows(string sql, params object?[] args)
        {
            using var cmd = Make(sql, args);
            using var r = cmd.ExecuteReader();
            var list = new List<object?[]>();
            while (r.Read())
            {
                var row = new object?[r.FieldCount];
                for (var i = 0; i < row.Length; i++) row[i] = r.IsDBNull(i) ? null : r.GetValue(i);
                list.Add(row);
            }
            return list;
        }

        public Dictionary<string, object?>? Row(string sql, params object?[] args)
        {
            using var cmd = Make(sql, args);
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return null;
            var row = new Dictionary<string, object?>(StringComparer.Ordinal);
            for (var i = 0; i < r.FieldCount; i++) row[r.GetName(i)] = r.IsDBNull(i) ? null : r.GetValue(i);
            return row;
        }
    }
}
