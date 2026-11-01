# Architecture

Four layers, each depending only on the one below.

```
App (WPF)  ->  Adapters (IceWarp, CardDAV)  ->  Core (model, merge, vCard)
```

## Projects

| Project | Target | Responsibility |
| --- | --- | --- |
| `ContactManage.Core` | net9.0 | Contact model, normalization, duplicate matching, merge groups and conflicts, vCard read/write, store interfaces. No client or UI code. |
| `ContactManage.Adapters` | net9.0 (Windows-only attribute) | IceWarp folder discovery from its local cache, CardDAV client, CardDAV contact store. |
| `ContactManage.App` | net9.0-windows, WPF | Folder list, review window, sign-in and destination dialogs, demo data, workflow. |
| `ContactManage.Tests` | net9.0 | xUnit tests including an in-memory CardDAV server. |
| `tools/CardDavSpike` | net9.0-windows | Console probe for trying a real server (read-only by default; fake-contact write tests; an additive restore from a backup). |
| `tools/GoogleProbe` | net9.0-windows | Read-only Gmail probe: browser sign-in, prints label names and counts. |

Packages: `Microsoft.Data.Sqlite` (reading IceWarp caches), `libphonenumber-csharp` (E.164 phone normalization).

## Core

- `Contact`: id, display/given/family name, company, job title, notes; lists of emails, phones, addresses. Fidelity extras: name prefix/middle/suffix, department, `Uid`, `RemoteId` (the server href), `Labels` (raw vCard parameters per value, for example `;TYPE=WORK`), `Extra` (raw vCard lines the model does not understand).
- `Normalizer`: email (trim, lower-case), phone (E.164, default region US), name (case, accent and punctuation insensitive), loose text key, name compatibility.
- `MergeEngine`: union-find over shared normalized emails and phones, then name-only links. Produces a `MergeResult` of untouched contacts and `MergeGroup`s.
- `MergeGroup`: members, match reasons, `NeedsReview`, `Decision` (Pending, Merge, KeepSeparate), `Conflicts`. `BuildMerged()` creates the merged contact.
- `NotDuplicates`: the saved list of pairs the user said are different people. Contacts are keyed by account and vCard UID (or server address). `Apply` settles matching groups before review, `Record` updates and saves after a successful write, `Load` tolerates a missing or unreadable file.
- `FieldConflict`: options plus `Choose`, `KeepBoth`, `Custom`, `Reset`.
- `MergeResult.BuildFinalList()`: throws until every group is decided and every conflict resolved, so nothing is decided silently.
- `MergeResult.BuildPartialList()` and `Summary`: for a user who stops early. Finished decisions are applied; undecided groups and merges with unresolved conflicts pass through untouched. The review window confirms before saving a partial result.
- `VCard`: parse and write vCard 3.0 (reads 4.0). Modeled fields map to `Contact`; everything else is kept as raw lines in `Extra`, so a round trip drops nothing.
- `IContactSource` (detect client, list folders) and `IContactStore` (read contacts, replace a folder's contents, create a folder). `VCardFiles` handles `.vcf` files and timestamped backups.
- `ContactFolder`: id (`<accountKey>:<path>`), name, path, account, server name, kind (CardDav, GmailApi, GmailLabel, Google, Local), writable flag, counts, display path, server URL. `IsReadable` and `CanWrite` are true for CardDAV, Gmail (API) and Gmail label folders. `FolderList.SelectionProblem` refuses a selection that holds more than one row of the same Google account.

## Sources and the first screen

A *source* (`IContactSource`) says what is on this PC: `IceWarpContactSource` reads the mail program's folder cache, `OnlineContactSource` lists nothing (online accounts add their own rows after sign-in), and `DemoContactSource` supplies made-up folders. Which one is used is a `SourceMode` (Online, MailProgram, Demo). On start, `StartPlan.Decide` (Core, unit-tested) chooses between showing the "Where are your contacts?" screen and going straight to a mode: no supported mail program means online accounts; a remembered `StartPreference` (kept in `settings.json` by `AppSettings`) skips the screen; `--demo` forces the demo and `--start-screen` forces the screen. `MainViewModel.SetMode` switches source, and **Switch source** reopens the screen later. Writing and reading by folder kind is unchanged: `CompositeContactStore` sends each folder to the CardDAV, Gmail or Local Folders store, so an online-only user gets the same review and save flow. New online providers (Microsoft, generic CardDAV) will be added as further stores and rows in the start screen's online tile.

## Adapters

### IceWarp (read-only local cache; Local Folders can also be written)

`IceWarpContactSource` detects the client (registry, profile folder) and lists contact folders by combining `accounts.dat` (account name and host only), each account's `folders.dat` (folder tree) and `contact_index.dat` (counts). Details in `ICEWARP.md`. All SQLite access goes through `SqliteCopy`, which works on a temporary copy.

### CardDAV

- `CardDavClient`: PROPFIND (principal, home, address books, count), REPORT `addressbook-query` (bulk read), PUT with `If-None-Match: *`, DELETE, extended MKCOL. HTTP Basic over https only. Redirects are followed manually and only within the same host. A 401 or 403 raises `CardDavAuthException`.
- `CardDavContactStore`: one session per account. Asks for credentials through a callback (with an error message and up to three attempts). Maps an IceWarp folder to a server address book: the cache path without its leading account number, relative to the home; otherwise a unique display-name match. `ReplaceContactsAsync` (1) writes the contacts that are new, (2) lists the folder again; a new contact that is missing was folded by the server into an existing one (AOL does that for an existing name and email), so each is matched to its counterpart (same name, shared email), which is kept and counted as updated, and if one cannot be matched it stops with "Nothing was deleted", (3) writes each merged contact over one of its own originals (`Contact.Sources`, keeping that original's UID), (4) deletes the remaining removed ones, leaves untouched contacts alone, refuses an empty list, and re-counts afterwards. It lists folders with a REPORT and, if the server refuses that (AOL: HTTP 400 without a filter), with a PROPFIND. `IContactStore.CanCreateFolder` is false for an account whose server does not list MKCOL in its `Allow` header (AOL), and the save screen then greys out "new folder". `CreateFolderAsync` makes a new address book (a numbered name if the name is taken) and fills it.

## App

- `MainWindow` + `MainViewModel`: folder list with checkboxes (disabled for unreadable folders), mode switch (real or demo), status line, Refresh / Next / Exit.
- Next runs the workflow in `MainWindow.xaml.cs`: read each selected folder, merge, open `ReviewWindow`, then `DestinationWindow`, confirm, back up, write, report.
- `ReviewWindow` + `ReviewViewModel`: list of duplicate sets, member table, Merge or Keep separate, conflict boxes, filter for items needing attention. Finish is enabled only when `MergeResult.IsComplete`.
- `CredentialWindow` + `SessionCredentials`: asks for user name and password per account, with an opt-in "Remember" checkbox. A saved login is reused silently (`SavedLogins` in Core decides when to reuse, save after a successful sign-in, or forget after a rejection; `WindowsCredentialVault` in Adapters stores it in Windows Credential Manager through `ICredentialVault`). Otherwise only the user name is remembered, in memory.
- `DemoData`, `DemoContactSource`, `DemoContactStore`: three sample folders and 13 contacts with an in-memory store that behaves like the real one.

## Data flow

```
folders.dat / contact_index.dat / accounts.dat        (local cache, read via temp copies)
        |  IceWarpContactSource.ListFolders
        v
 ContactFolder list  -- user ticks folders -->  IContactStore.ReadContactsAsync (CardDAV REPORT)
        v
 List<Contact>  -> MergeEngine.Merge -> MergeResult -> ReviewWindow (decisions, conflicts)
        v
 BuildFinalList -> DestinationWindow -> backup (.vcf) -> IContactStore.ReplaceContactsAsync / CreateFolderAsync
```

## Merge rules in short

- Same normalized email or phone: same person (transitive), merged automatically when names are compatible.
- Same multi-word name only, or incompatible names in a group: "possible", waits for the user.
- Identical single-value fields merge. Different ones become conflicts for the user.
- Emails, phones, addresses are unioned; labels follow their values; duplicate extras are dropped; single-value extras (BDAY, PHOTO, ...) keep the first.

## Testing

**Coverage report.** `dotnet test tests/ContactManage.Tests` collects coverage (coverlet) and, when the tests pass, builds an HTML report with ReportGenerator into `coverage-report/index.html` (open it in a browser). ReportGenerator is a local dotnet tool pinned in `.config/dotnet-tools.json` and restored automatically. The hook lives in `tests/ContactManage.Tests/CoverageReport.targets`; `-p:CoverageReport=false` turns it off. MSBuild skips the hook when a test fails, so run `powershell -File scripts/coverage-report.ps1` to build the report after a failing run. `coverage-history/` keeps earlier runs so the report shows the trend. The Visual Studio Test Explorer does not run the hook. The UI tests do not collect coverage.

GitHub Actions (`.github/workflows/ci.yml`) restores, builds in Release and runs the tests with coverage on `windows-latest` (excluding the UI category) for every push to `main` and every pull request, and uploads the test results (`test-results` artifact) and the HTML coverage report (`coverage-report` artifact), and adds the coverage summary to the run page. After a failing test it builds the report with the script. The projects are Windows-only, so it does not run on Linux.

`dotnet test` runs 246 unit tests: normalizer, merge engine, vCard codec, folder cache reader (with generated SQLite files), folder display helpers, and the CardDAV store against `FakeDavServer`, which implements enough of WebDAV/CardDAV (PROPFIND, REPORT, PUT, DELETE, MKCOL, Basic auth) to cover read, replace ordering, failed writes, name collisions, wrong passwords and plain-http refusal. The WPF windows are covered by `tests/ContactManage.UiTests`: FlaUI starts the built app with `--demo` and drives it through UI Automation patterns (invoke, toggle, select), never the mouse or keyboard, so it is safe to run while using the PC. Controls are found by `AutomationProperties.AutomationId` (set in the XAML), so give new controls an id before testing them. The five tests cover the folder list, a one-folder cleanup, resolving every set across three folders into a new folder, removing an obsolete set, and finishing early. A separate `ui-tests` CI job runs them on a hosted Windows runner (not yet confirmed there). `dotnet test --filter "Category!=UI"` skips them.

## Extending to another mail client

Implement `IContactSource` (find the client and list its folders). If its contacts are reachable over CardDAV, the existing `CardDavContactStore` can be reused as long as each `ContactFolder` carries `ServerUrl`, `Path` and an account key; otherwise implement `IContactStore` for it.
