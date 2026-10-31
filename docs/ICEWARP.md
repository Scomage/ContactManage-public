# IceWarp internals

What ContactManage relies on, as found on a Windows PC running IceWarp Desktop Client. Everything here is undocumented and may change between client versions, so all reads are read-only (on a copy) and tolerant of missing pieces. The only writes are to Local Folders (see below).

## Installation and profile

- Executable: `C:\Program Files (x86)\IceWarp Desktop Client\MailClient.exe`, found through `HKLM\SOFTWARE\Clients\Mail\IceWarp Desktop Client\shell\open\command` (64-bit and 32-bit registry views are both checked).
- Profile: `%APPDATA%\IceWarp Desktop Client\`.
- The client is a .NET app with an embedded Chromium UI. It exposes no public contact API.

## Accounts: `accounts.dat`

- SQLite database in WAL mode. Tables: `Accounts`, `AccountsJson`, `CloudStorageProviders`, `OnlineMeetingProviders`, all key/value.
- `AccountsJson` rows are JSON: `{"Type": "<.NET class>", "TypeValue": {...}}`.
- Contacts come from `...CalDavAccountConfiguration` records (one type serves CalDAV and CardDAV). Useful fields: `AccountName` (a display name, **not** the login), `Host` (a full URL such as `https://mail.example.com/webdav/`), `DefaultContactsPath`.
- Google accounts are `GdataContactsAccountConfiguration`. Other records (`Imap`, `Smtp`, `Jabber`, `IceWarpChat`) also carry hosts.
- **The same records hold credentials (`Credentials.Secret` and others). Never read, print, log or copy them.** `AccountReader` selects only `AccountName` and `Host`.

## Folder cache

Each account has `<mail account GUID>\<protocol record GUID>\folders.dat`, plus `contact_index.dat` and `contact_data.dat`. `Local Folders\` has no account record.

- `folders.dat`: tables `Folders(id, name, path, folderFlags, parentFolderId, ...)` and `FolderAttributes(parentId, name, value)`.
- A contact folder is a `Folders` row with an attribute `type = 4` (calendars 8, tasks 16, mail 2, notes 32).
- Folders with flag bit `0x200` (RecipientHistory, UnsafeContentWhitelist) are skipped. This comes from one PC and still needs checking elsewhere.
- Attribute `displayName` gives the human name. Google label folders have opaque names such as `contactGroups%2F...`; `displayName` fixes that, and `categoryName` marks them.
- Folder names repeat across accounts, so the UI always shows account, server and path.
- The directory GUID of a `folders.dat` is the key into `accounts.dat`, which gives the account name and `Host`.

## Counts: `contact_index.dat`

- `ContactItems(id, folder, type, ...)`: type 0 is a contact, type 1 a distribution list (no emails or phones).
- `NonUploadedAndMarkedForDeleteContactItems(id)`: items to exclude from counts.
- `ContactCategoryNames(categoryId, id, categoryName)`: label membership.
- The IceWarp Outlook Sync folder ships an old System.Data.SQLite that cannot read these files (FTS5, WITHOUT ROWID). Use `Microsoft.Data.Sqlite`.
- Counts reflect the client's last sync, not the live server.

## Gmail labels

Labels are tags on contacts, not folders. A label's folder row holds no `ContactItems`; its members are the contacts carrying that `categoryName`. `ReadCategoryCounts` counts distinct contacts per label, which matches Gmail's own numbers. A contact can carry several labels, so label folders overlap, and "My Contacts" duplicates the root "Contacts" folder. Some labels (for example "starred") have no folder row and are not listed.

## Local Folders

`%APPDATA%\IceWarp Desktop Client\Local Folders\` has the same set of files as an account but no account record: its contacts live only on this PC. The user's own folder is `Contacts` (`folders.dat` row, `type = 4`); `RecipientHistory` (flag `0x200`) is the client's list of addresses it has mailed and is skipped.

- **The data is in `contact_index.dat`, not `contact_data.dat`.** `contact_data.dat` holds, per contact, a stub vCard (`BEGIN`, `VERSION`, `PRODID`, `END`, about 79 bytes) and the avatar image; the real fields are relational tables.
- `ContactItems` (`type` 0 contact, 1 distribution list; `folder` is `Folders.id`): `firstName`, `middleName`, `lastName`, `suffix`, `prefix`, `displayName`, `nickName`, `organization`, `title`, `department`, `comment` (notes), `componentUid` (a stable id; `uniqueId` is empty). `birthday` and `anniversary` are `0` when unset.
- Child tables, joined on `parentId`: `ContactEmails`, `ContactTelephones`, `ContactAddresses` (street, city, county, zip, country), `ContactHomepages`, `ContactIM`. The type is in `purpose` with a numeric suffix (`WORK_1`, `CELL_2`, `FAXWORK_1`, `HOME_1`); free-text purposes such as `Personal_1` also occur.
- Labels are rows of `ContactCategoryNames`. A Local Folders `Contacts` that was filled from a Google account also carries Google's own labels (`My Contacts`, `starred`, `Most Contacted`, `Unfiled`); ContactManage leaves those out.
- `contact_data.dat` and `contact_index.dat` have FTS5 search tables (`NameIndex_*`, `CommentIndex_*`) that the client keeps in step with the rows. Anything that wrote the rows without them would leave the client's search wrong.

`LocalContactReader` reads a copy of `contact_index.dat` (see below) and maps this to a `Contact`; homepages, the nickname and instant-messaging entries are carried as raw `Extra` lines.

### Writing back (Local Folders only)

`LocalContactWriter` is the only code that writes to IceWarp's files. It exists because Local Folders have no server copy, and it is built to be hard to misuse:

- **One transaction over both files** (`contact_index.dat` with `contact_data.dat` attached). Any failed check rolls everything back. SQLite's own locking lets it run while IceWarp Desktop Client is open; it waits up to 15 seconds for a lock, and says so if it cannot get it.
- **Known layouts only.** `PRAGMA user_version` of `contact_index.dat` must be 17 (the version it was written against) and every table it touches must exist; otherwise it refuses before backing anything up.
- **Backup first.** `Backups\LocalFolders-files-<time>\` holds `contact_index.dat` and `contact_data.dat` made with SQLite's online backup (a plain copy of a live database can be torn). To restore by hand: exit IceWarp, delete the two files and their `-wal` and `-shm` from `Local Folders\`, copy the two backups there. The usual vCard backup (`Backups\<time>\Local Folders - Contacts.vcf`) is made too and is what **Restore from backup** uses.
- **Same rules as the other stores.** A contact left alone is not touched; a merged contact is written over one of its own original rows (keeping its id, picture and card); others are added; only contacts nobody claims are removed; an empty list is refused. A contact read back from a backup with the UID of a row that is still there updates that row; a deleted one comes back as a new row with its old UID.
- **Per row it does what IceWarp does.** Child rows (emails, phones, addresses) are changed only where they differ, keeping the original `purpose` (`WORK_1`, `CELL_2`, free text) and primary flag (`1` for the first value, `100` for the rest). New values get `<TYPE>_<n>` purposes. Homepages and instant-messaging entries are only added, never deleted. Labels are replaced except IceWarp's built-in ones (`My Contacts` ...), which stay. Empty text columns are `''`, not NULL, as IceWarp writes them. `created` and `updated` are .NET ticks (UTC).
- **Search tables.** The email, address and IM search tables are kept right by IceWarp's own triggers, so ordinary SQL is enough. `NameIndex` and `CommentIndex` in `contact_data.dat` have no triggers and are refreshed by the writer. Before committing it checks that every contact row has exactly one name row and one comment row, and that the three full-text indexes pass `integrity-check`; it rolls back otherwise.
- **What an added contact lacks:** a picture. It gets the stub card IceWarp keeps for every contact but no avatar and no `avatarHash`; whether the client then generates one has not been tried live yet (the live run only merged and removed contacts). Details with no column (birthdays, Google fields ...) are left out and named in the result message.
- **What the open client shows.** Confirmed live (one Replace on the dev PC, IceWarp Desktop Client open throughout): the client showed the new contacts straight away, with no restart. Other client versions may differ, so the messages say "normally shows the change straight away; if not, exit and start it again". Do not edit contacts in the client while ContactManage is saving. This differs from CardDAV accounts (Gmail, AOL, IceWarp servers), whose cached copy only refreshes after the client syncs or restarts.

## Reading the database safely

The client keeps these files open in WAL mode. `SqliteCopy` copies the `.dat` and its `-wal` and `-shm` files to a temp folder, opens the copy, and deletes it afterwards. The originals are never opened or modified.

## CardDAV server behaviour (verified against an IceWarp server)

| Check | Result |
| --- | --- |
| Login | HTTP Basic over https. The login is the account's email address, not the `AccountName`. |
| Discovery | `PROPFIND` on the host URL returns `current-user-principal`, then `addressbook-home-set`. |
| Capabilities | DAV header includes `addressbook` and `extended-mkcol`. Allowed: PROPFIND, REPORT, PUT, DELETE, MKCOL, COPY, MOVE. |
| Create address book | Extended MKCOL works for a normal user. |
| Bulk read | An `addressbook-query` REPORT lists the contacts but returns only empty stubs. The real data comes from `addressbook-multiget` in batches of 50. |
| Overwrite and delete | PUT replaces (204), DELETE removes (204). |
| Format | vCard 3.0, `text/x-vcard`. |
| Fidelity | Names with accents, title, phone types, addresses, URL, categories survive. Reformatting that keeps the meaning: `;` escaped in notes, `BDAY` in compact form, trailing `;` on `ORG`. **Email type labels are dropped**: the first email becomes `INTERNET,PREF`, the others `INTERNET`. |
| Folder layout | The top-level `Contacts` book is directly in the home. Shared and delegated books are nested (`Shared/<name>`, `Support Inbox/<name>`). Mapping from the cache path is `path` minus the leading account number, relative to the home. Only the top-level book is confirmed live. |

IceWarp Desktop Client keeps its own cached copy of the folders it syncs (including AOL and Google ones). After ContactManage changes a server, the client shows the new contacts and counts only after its next sync, and a Refresh inside the client does not always force it; exiting and starting the client again does.

Since version 1.0.1, Replace writes a merged contact over one of its own originals (a PUT to the original's address, same UID) instead of under a new address. This is what AOL needs (see `AOL.md`) and is covered by the fake-server tests; it has not yet been run against a live IceWarp server.

AOL (`carddav.aol.com`) is a separate server with its own behaviour, documented in `AOL.md`. Google is not CardDAV in IceWarp (it uses its own protocol); ContactManage reads Gmail through Google's People API instead (`GMAIL.md`). Other IceWarp versions have not been checked.
