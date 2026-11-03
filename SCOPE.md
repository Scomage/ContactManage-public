# ContactManage – Project Scope

Date: 2026-10-22, updated 2026-11-03 · Status: v1.0.8 released (IceWarp, Gmail, AOL, Local Folders and Microsoft accounts working; Microsoft read and new folder confirmed on a real Outlook.com account; see CLAUDE.md for current status and next steps)

## 1. Purpose

ContactManage is an installable Windows application that merges several contact folders into one de-duplicated list and writes that list back into the user's mail client.

Goals:

- Combine contacts scattered across accounts, folders and local stores.
- Remove duplicates safely; the user decides every real conflict.
- Replace existing contact folders, or create new ones, without losing data.
- Install and run on any Windows PC, not only the development machine.

## 2. Decisions so far

| Decision | Choice |
| --- | --- |
| v1 mail client | **IceWarp Desktop Client** (the default mail client on the dev PC), reading its IceWarp and AOL CardDAV accounts. **Gmail** is added through Google's People API (`docs/GMAIL.md`), **Microsoft accounts** through Microsoft Graph (`docs/MICROSOFT.md`); AOL uses an app password (`docs/AOL.md`) |
| Language / UI | **C# on .NET 9, WPF** (MVVM) |
| Distribution | Self-contained win-x64 build with an installer; deployable to other PCs |
| Audience | Colleagues first; public release is a future goal (design for it now) |
| Server hostname | Never typed by the user: mined from the account that owns each contact folder |
| Integration order | **CardDAV first** (primary path). vCard/CSV is the fallback for servers where CardDAV is unavailable, and is tested after CardDAV |

## 3. Deployment requirements

- Signed installer (MSI or MSIX) plus an optional portable zip. Self-contained: no .NET runtime to install.
- Per-user install; no administrator rights where possible.
- First run auto-detects the installed mail client. Nothing is hard-coded to the dev machine.
- Fully local: contacts are processed on the PC; no telemetry without opt-in.
- Plain-text log file and a "copy diagnostics" button for support.
- Version check with a manual download link in v1; auto-update later.

## 4. Scope

| In scope (v1) | Out of scope (v1) |
| --- | --- |
| Detect installed mail clients; IceWarp (with its AOL account), Gmail and Microsoft accounts supported | Continuous background sync |
| List contact folders with counts | Calendar, email, tasks |
| Merge selected folders into one list | macOS, Linux, mobile |
| Match duplicates by name, email, phone | Server or multi-user hosting |
| Field-level conflict resolution | Fuzzy/AI matching beyond name, email, phone |
| Replace folders or create new ones | Contact photos beyond pass-through (stretch) |
| Backup, change log, restore from backup | Clients other than IceWarp, AOL, Gmail and Microsoft, except vCard fallback |
| vCard/CSV import and export (universal fallback) | Outlook, Thunderbird, eM Client adapters (phase 2) |

## 5. Functional requirements (six-step wizard)

1. **Identify the mail client.** Detect installed clients (registry, install paths, profiles). If several are present, the user picks one. Unsupported clients are listed with the reason.
2. **Identify contact folders.** Enumerate every contact folder across the client's accounts and local stores, with item counts, account names and the **server name** (hostname read from the owning account's settings; local folders show "Local").
3. **Select folders.** Checkbox list; each row shows folder name, account, server name and item count. At least two must be ticked to continue. Folders from different servers can be merged; the server of each is kept for the write-back step.
4. **Merge.** Read the selected folders into one in-memory combined list. Source folders are not modified at this stage.
5. **De-duplicate and resolve conflicts.**
   - Contacts are duplicates if they share a normalized email address, a normalized phone number (E.164), or the same normalized name plus one other matching field.
   - Matches are grouped transitively (A~B and B~C gives one group).
   - Identical fields merge automatically. Differing single-value fields (job title, address) go to a conflict screen: pick one, keep both, or edit.
   - Multi-value fields (extra emails, phones) are unioned.
   - Name-only matches are flagged "possible" and need confirmation.
6. **Write back.** Choose existing folders to replace, or create a new folder in an existing account or a local store. A summary of adds, updates and deletes is shown before anything is written.

## 6. IceWarp integration

IceWarp Desktop Client is installed at `C:\Program Files (x86)\IceWarp Desktop Client\MailClient.exe`. It is a .NET application with an embedded Chromium (CEF) UI. No public contact API or obvious local contact database was found in the install folder.

Decision: **CardDAV is the primary integration; vCard/CSV is the fallback.**

1. **CardDAV (primary).** Direct read/write against the server named in the account record. Lists address books, creates and replaces contacts, and automates the whole workflow. The app signs in with the user's own account (see credentials below) and reports clearly if a server has CardDAV off.
2. **vCard/CSV (fallback, tested after CardDAV).** The app exports the selected folders to vCard/CSV, merges them, and produces a file the user imports into IceWarp. Needs no credentials and no server settings. Viability depends on whether the client can export a folder, import into a chosen or new folder, and replace a folder without leaving duplicates (e.g. delete then import).
3. **Local client cache:** rejected. Writing an undocumented store risks corruption, and the client would not see changes made behind its back.

**Credentials (open design point).** Passwords in `accounts.dat` are protected by the client and must not be read. CardDAV therefore needs the user to enter their password once per server, stored in Windows Credential Manager. Hostname and account name are pre-filled from the client, so only the password is typed.

**Hostname discovery (confirmed on the dev PC).** The user never enters a server name. The app reads it from the account that owns each selected folder, and shows it in the folder selection list (step 3).

- Account settings live in `%APPDATA%\IceWarp Desktop Client\accounts.dat`, a SQLite database in WAL mode (so the `-wal` and `-shm` files must be copied with it, or opened read-only while the client runs).
- Tables are key/value only: `Accounts`, `AccountsJson`, `CloudStorageProviders`, `OnlineMeetingProviders`. `AccountsJson` holds one JSON record per row: `{"Type": "<.NET class>", "TypeValue": {...}}`. Property names such as `AccountName` and `Host` exist inside the JSON, not as table columns.
- Contacts are served by the `MailClient.Protocols.CalDav.CalDavAccountConfiguration` records (the client uses one record type for CalDAV and CardDAV). Relevant fields: `AccountName`, `Host` (a full URL, e.g. `https://webmail.<domain>/webdav/`), `DefaultContactsPath`, `UnsupportedContactFields`.
- Other record types (`ImapAccountConfiguration.Host`, `SmtpAccountConfiguration.Host`, `JabberAccountConfiguration.Host`) also carry hostnames and can serve as a fallback. The folder data for each account sits in `%APPDATA%\IceWarp Desktop Client\<account id>\` (`contact_data.dat`, `folders.dat`, etc.), keyed by the same GUID.
- `Credentials.Secret` and similar fields exist in these records. The app must never read, log or copy them.
- The format is internal and undocumented, so it may change between client versions: read-only access, version-tolerant parsing, and a one-time prompt as the fallback if parsing fails.

**Finding: CardDAV is already in use.** The client itself syncs contacts over WebDAV/CardDAV, and the dev PC has several such accounts on different servers (a company IceWarp server, AOL, and others). So CardDAV is available on at least some servers, and one merge may span folders from different servers. This does not change the decision to test vCard/CSV first, but it lowers the risk of the CardDAV fallback.

**Contact folder discovery (confirmed on the dev PC).** Each account has a cached folder tree at `%APPDATA%\IceWarp Desktop Client\<mail account id>\<protocol record id>\folders.dat` (SQLite, WAL mode, read-only access).

- Tables: `Folders` (`id`, `name`, `path`, `folderFlags`, `parentFolderId`, ...) and `FolderAttributes` (`parentId`, `name`, `value`).
- A contact folder is a `Folders` row with a `FolderAttributes` row `name='type'`, `value=4`. CardDAV address books also carry `customFolderType = contactcollection`. Calendars are type 8, tasks 16, mail 2, notes 32.
- The parent directory GUID is the protocol record in `accounts.dat`, which gives the account name and server `Host`. Folder `path` (e.g. `/0/Shared/Team Contacts`) is relative to that host.
- Not every type-4 row is a user address book. Internal folders such as `RecipientHistory` and `UnsafeContentWhitelist` (folder flags 520) are excluded, and archive/hidden folders (flags such as 37120, 8320) need review before being shown.
- Folder names repeat across accounts (several are just "Contacts"), so the list must always show account, server and path.
- Account types found on the dev PC: IceWarp (CalDAV/CardDAV), AOL (CardDAV), Google (`GdataContactsAccountConfiguration`, contact groups as folders, not CardDAV) and `Local Folders` (no server, not synced). v1 reads IceWarp, AOL, Local Folders and (through the People API) Gmail; writing covers CardDAV accounts, Gmail and (in place, with a backup) Local Folders; IceWarp's cached copy of a Google account is not read.
- The cache can be stale. At write time the app lists address books live over CardDAV (PROPFIND) and uses the cache only to pre-populate the selection list quickly.

**Dev PC folder list the app would show:**

| Account | Server | Contact folders |
| --- | --- | --- |
| Work account (IceWarp) | webmail.example.com | Contacts |
| Main account (IceWarp) | mail.example.com | Contacts, Archive/Contacts, Shared/Team Contacts, Support Inbox/Contacts |
| Second account (IceWarp) | mail.example.com | Contacts |
| Colleague account (IceWarp) | mail.example.com | Contacts |
| Home AOL | carddav.aol.com | Contacts |
| Home Gmail | Google | Contacts, plus 6 contact groups |
| Local Folders | Local | Contacts |

Spike goals (first task):

- Find where the client stores account settings and contacts, so the server name for each folder can be mined.
- Test CardDAV first on a scratch address book: authentication, address book list (PROPFIND), read all contacts, create a new address book (MKCOL), write, replace and undo; check field fidelity (multiple phones/emails, addresses, notes, groups) and the `UnsupportedContactFields` the client records per account.
- Then test the vCard/CSV round trip as the fallback: export a folder, import into an existing and a new folder, replace a folder.
- Record which IceWarp versions and account types were tested.

The adapter interface keeps other clients pluggable:

```csharp
interface IContactSource {
    ClientInfo Detect();
    IReadOnlyList<ContactFolder> ListFolders();
    IReadOnlyList<Contact> ReadContacts(ContactFolder folder);
    ContactFolder CreateFolder(string name, Account? account);
    void ReplaceContacts(ContactFolder folder, IReadOnlyList<Contact> contacts);
}
```

## 7. Data safety

Replacing a folder is destructive, so no write happens without a backup and explicit confirmation.

- **Automatic backup:** every affected folder is exported to a timestamped vCard/CSV file in a user-visible backup folder before any write.
- **Dry run:** a preview lists contacts to add, update and remove per destination folder.
- **Confirmation:** the user must confirm the named folders to be replaced.
- **Undo:** "restore from backup" reverses a write.
- **Verify:** after writing, re-read the folder and compare counts; report mismatches.
- **Fidelity:** unmapped fields (custom properties, photos, categories) are preserved or reported as lost, never silently dropped.
- **Server-side risk:** with CardDAV, writes sync to the server and other devices immediately, so backup and preview are mandatory, not optional.

## 8. Architecture

Four layers, each depending only on the one below:

1. **UI (WPF, MVVM):** wizard, conflict screen, preview, log view.
2. **Core library:** contact model, normalization (names, emails, E.164 phones), matching and merge engine, conflict list. No client code, so it is fully unit-testable.
3. **Client adapters:** IceWarp (CardDAV) and vCard/CSV in v1; Outlook, Thunderbird, Graph later.
4. **Infrastructure:** backup/restore, logging, settings, credential storage (Windows Credential Manager), installer.

Solution layout (see docs/ARCHITECTURE.md):

```
ContactManage.sln
  src/ContactManage.App        (WPF)
  src/ContactManage.Core       (model, matching, merge, vCard)
  src/ContactManage.Adapters   (IceWarp reader, CardDAV client and store)
  tests/ContactManage.Tests    (xUnit, includes a fake CardDAV server)
  tools/CardDavSpike           (server probe and scratch write test)
  docs/                        (architecture, IceWarp internals, installation, security)
  installer/                   (install scripts: scripts/installer, scripts/package.ps1)
```

Libraries (candidates): libphonenumber-csharp for phones, a vCard parser/writer, xUnit for tests.

## 9. Milestones

1. **Spike:** IceWarp contact access (section 6) on a test profile.
2. **Core engine:** model, normalization, matching, merge and conflict rules, with unit tests.
3. **IceWarp adapter + wizard UI:** end-to-end flow including backup and undo.
4. **vCard/CSV adapter:** universal fallback.
5. **Installer and pilot:** per-user install package built (`scripts/package.ps1`, GitHub release workflow; v1.0.0 and v1.0.1 tagged). Still open: code-signed installer; test on 2-3 other PCs.
6. **Phase 2:** Outlook, Thunderbird, Outlook.com (Graph), per demand.

## 10. Risks

- IceWarp may not expose CardDAV (server admin setting), forcing a vCard/CSV-only workflow.
- Writes via CardDAV propagate to the server immediately; a bad merge affects all synced devices.
- Over-aggressive matching could merge different people; mitigated by "possible" flags and preview.
- Unsigned installers trigger SmartScreen warnings; a code-signing certificate is needed.
- Credentials: if CardDAV is used, the app needs the user's IceWarp login; store securely (Windows Credential Manager) and never log it.
- Hostname mining depends on an undocumented settings location, which may differ between IceWarp versions; the one-time prompt is the fallback.
- Public release raises the bar for signing, support, privacy and compatibility across many IceWarp server setups.

## 11. Open questions

- [x] Where does IceWarp Desktop Client store account settings? Answered: `accounts.dat` (see section 6).
- [x] How are contact folders listed per account? Answered: `folders.dat` cache, `type=4` (see section 6); confirmed live over CardDAV at write time.
- [x] Does the IceWarp server allow creating a new address book (MKCOL) for a normal user? Answered: yes, extended MKCOL works (see docs/ICEWARP.md). Email type labels are dropped on save.
- [x] Can the server ever be reached with a password the user types? Answered: IceWarp yes; AOL needs an app password (Yahoo offers OAuth for AOL only through a commercial access application, see docs/AOL.md); Gmail uses Google OAuth in the browser (docs/GMAIL.md).
- [x] Local Folders (IceWarp's own store) can be read: `LocalContactReader` maps the relational tables of `contact_index.dat` (see `docs/ICEWARP.md`).
- [x] Local Folders can be written back in place (`LocalContactWriter`, see `docs/ICEWARP.md`): backup of both databases and a vCard backup first, one transaction, runs while IceWarp is open. Tested on fixtures with IceWarp's real schema and on a scratch copy of the real files, then run live on the dev PC (514 became 502, integrity checks clean, IceWarp Desktop Client showed the change without a restart).
- [ ] Does the vCard/CSV fallback preserve enough fields, and can a folder be replaced cleanly?
- [ ] Are the Shared and Archive contact folders meant to be merge sources or write targets?
- [ ] Public release: licensing, code-signing certificate, support model and privacy statement (not needed for the colleague pilot).
