# ContactManage

Windows desktop app that merges several contact folders from a mail client into one de-duplicated list and writes it back. Supported today: **IceWarp Desktop Client** (with its AOL account) and **Gmail**. Stack: **C# / .NET 9 / WPF**, xUnit tests.

Read this file first. Deeper detail: `README.md` (overview), `SCOPE.md` (requirements and decisions), `docs/ARCHITECTURE.md`, `docs/ICEWARP.md` (how IceWarp stores things), `docs/GMAIL.md`, `docs/AOL.md`, `docs/QUICKSTART.md` (shortest install path), `docs/ONBOARDING.md` (giving the app to an outside user), `docs/INSTALLATION.md` (build, run, deploy), `docs/SECURITY.md` (handling rules).

## The workflow the app implements

1. Ask where the contacts are (an online account such as Gmail, a mail program on this PC, or the demo; the first screen is skipped when there is nothing to choose or the user chose to remember). 2. List all contact folders (with account, server name, contact count). 3. User ticks one folder (to clean up its own duplicates) or several (to merge them). 4. Read and merge them. 5. Show duplicates and field conflicts for the user to resolve. 6. Write back: replace the selected folders, create a new folder in an existing account, or save a vCard file.

The main window has a **My contacts / Demo (sample data)** switch. Demo mode uses made-up contacts and an in-memory store, runs the identical flow, and never touches a real account. `ContactManage.App.exe --demo` starts in demo mode.

## Decisions already made by the user (do not re-litigate)

- Must deploy to other PCs, not just the dev PC. Colleagues first, public release later (so design for it).
- v1 supports IceWarp Desktop Client (with its AOL account) and Gmail. Outlook, Thunderbird, eM Client, Hotmail adapters are phase 2. Local Folders (IceWarp's own store) are readable and their `Contacts` folder can be replaced in place (backup first; see Hard rules and `docs/ICEWARP.md`).
- **Online accounts without a mail program** (decided 2026-10-30, option D): some Gmail users only use the web. The app has a first screen, "Where are your contacts?" (Online account / A mail program on this PC / Try the demo, a "remember my choice" box, and **Switch source** in the main window). The screen is skipped when no supported mail program is found (online accounts only) or the user chose to remember. Rollout stays controlled: the Google app remains in Testing status and the administrator adds each Gmail address as a test user before that person tries it (so no Google verification is needed now); see `docs/GMAIL.md`, "Gmail without a mail program". Choice is stored in `%LOCALAPPDATA%\ContactManage\settings.json` (`AppSettings`, `StartPlan` in Core). Later online providers, in this order: Microsoft (Outlook.com, Hotmail, 365) through the Graph API, a generic CardDAV account (iCloud, Fastmail, Nextcloud) with an app password, then the Thunderbird and Outlook adapters.
- **CardDAV is the primary integration**; vCard/CSV is the fallback (the user reversed an earlier vCard-first plan).
- The server hostname is never typed by the user: it is mined from the account that owns the selected folders and shown in the folder list.
- Source: https://github.com/Scomage/ContactManage-public (`main`).
- **Gmail** (decided after the IceWarp trial; details in `docs/GMAIL.md`): People API with OAuth (desktop app, system browser, PKCE, loopback), personal Gmail first, for the user and a few colleagues, Testing status at first. Merge duplicates in "My Contacts" into one survivor that carries the labels of all members. Google writes are sequential, not parallel. Built so far: sign-in (Add Google account), reading, label-aware merge, vCard-file output. Writing back: Replace My Contacts or a single label (see docs/GMAIL.md); first tested on a throwaway account.
- **AOL** (details in `docs/AOL.md`): CardDAV at carddav.aol.com with an AOL **app password**; OAuth would need Yahoo's commercial access approval, so it is not used. AOL has one address book and no MKCOL, so "new folder" is not offered for it (detected from the server's `Allow` header); an `addressbook-query` without a filter gets HTTP 400 and the client falls back to a PROPFIND listing. Read, a one-contact write test and (after the name+email fix, see docs/AOL.md) Replace worked on a real account.
- UI details requested: contact count per folder, buttons ordered Refresh, Next, Exit (left to right), Gmail group names instead of ids, Gmail labels listed as folders with Gmail's counts, demo/production selector.

## Repository layout

```
ContactManage.sln
src/ContactManage.Core       model, normalizer, merge engine, vCard codec, IContactStore (no client code, platform neutral)
src/ContactManage.Adapters   IceWarp reader (Windows only) and the CardDAV client/store
src/ContactManage.App        WPF UI: folder list, review window, credential and destination dialogs, demo data
tests/ContactManage.Tests    xUnit unit tests (246), includes an in-memory fake CardDAV server
tests/ContactManage.UiTests  FlaUI end-to-end tests that drive the real WPF windows in demo mode, the first screen and the phone region box (10, category "UI")
tools/GoogleProbe            read-only Gmail probe: browser sign-in, prints label names and counts only (needs %LOCALAPPDATA%\ContactManage\google-client.json)
tools/CardDavSpike           console probe used to test a real server (read-only probe; --write-test uses a scratch address book)
docs/                        architecture, IceWarp internals, installation, security
```

## Commands

```
dotnet build                      # whole solution; keep it at 0 warnings, 0 errors
dotnet test                       # all tests must pass
dotnet test tests/ContactManage.Tests   # also collects coverage and writes coverage-report/index.html (git-ignored) when the tests pass
powershell -File scripts/coverage-report.ps1   # rebuild the report by hand, for example after a failing run
dotnet test --filter "Category!=UI"   # unit tests only (the UI tests open real windows for about 15 seconds)
dotnet test tests/ContactManage.UiTests   # UI tests only; never touches the mouse or keyboard
dotnet run --project src/ContactManage.App [-- --demo]
```

Python is not installed on the dev PC. For throwaway experiments use a small dotnet console project in the scratchpad and delete it afterwards.

## Hard rules (security and safety)

- **Never read, print, log or copy credentials.** IceWarp `accounts.dat` records contain `Credentials.Secret` and similar fields. Only `AccountName` and `Host` may be read (`AccountReader` does exactly that).
- **Never open IceWarp databases in place to read them.** Copy the `.dat` plus `-wal` and `-shm` to a temp folder and read the copy (`SqliteCopy` does this and deletes the copy). **Never write to those files, with one exception the user asked for (2026-10-30): `LocalContactWriter`, used only for IceWarp's Local Folders** (`Local Folders\contact_index.dat` and `contact_data.dat`). It runs while IceWarp may be open, in one transaction, after `Preflight` (known schema version) and an online-backup copy of both files, and rolls back if its consistency checks fail. Nothing else may write to any IceWarp file; `accounts.dat` and the account folders stay read-only. Any new live run of this writer on the user's real profile needs their explicit "run it"; develop and test on copies and fixtures only.
- **Never handle the user's passwords.** For anything that needs a login (CardDAV tests), launch the tool in its own console window for the user to type into; do not ask for the password in chat and do not pass it on a command line. The `!` prefix in Claude Code has no interactive keyboard, so password prompts crash there. Use `Start-Process powershell -ArgumentList '-NoExit','-Command',...` and have the tool tee its output to a log file under the scratchpad (the tool's `--log <path>` option does this; the password is never echoed so it is never logged). Read the log afterwards.
- The app sends passwords only over https, only to the server they were entered for (cross-host redirects are refused), and keeps them in memory only for the session, unless the user ticks the opt-in "Remember this password on this PC" box (then Windows Credential Manager, saved only after the server accepted the login, deleted when rejected; "Forget saved passwords" clears all). Claude must still never see or handle passwords, saved or not.
- Write operations are destructive: backups first, writes before deletes, refuse to empty a folder, verify counts afterwards. Test real writes only against a scratch address book or a new folder first.
- Do not commit or push unless the user asks. Commit messages end with:
  `Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>`. Git identity is Scomage / support@scomage.com.
- Delete temp copies, screenshots and scratch files you create. A screenshot of the full screen can capture private mail; delete it right after use.

## Conventions

- Match the surrounding code: comment density (comments explain why, not what), naming, idiom. Records for data, small sealed classes, file-scoped namespaces, nullable enabled, implicit usings.
- Adapters and the tests project carry `[assembly: SupportedOSPlatform("windows")]` (see `AssemblyInfo.cs`); the CardDAV client itself is platform neutral.
- Every behaviour change in Core or Adapters gets a unit test. CardDAV code is tested against `tests/ContactManage.Tests/FakeDavServer.cs`.
- `.gitignore` has an exception `!src/ContactManage.App/` because the `*.app` rule hid the folder. Line endings: `.gitattributes` is `* text=auto`; git prints harmless LF-to-CRLF warnings.

## Tooling quirks seen in this environment

- The Bash tool fails with "unexpected EOF" on long heredocs that contain quotes or apostrophes, and nothing runs. Use the Write/Edit tools to create files; use Bash for short commands.
- The PowerShell tool is Windows PowerShell 5.1: no `&&`, no `2>&1` on native commands.
- The working directory sometimes changes after `cd` in a command; prefer absolute paths.

## Refresh and the folder list

The folder list starts from IceWarp's local cache (`folders.dat`), which only changes when IceWarp syncs. **Refresh** reloads that cache and then asks the servers (`IContactStore.RefreshFoldersAsync`): folders and counts are updated from each account's address-book home, and folders the cache does not know yet are added (`FolderList.Splice`). Only accounts already signed in to this session or with a remembered login are asked, so Refresh never opens a sign-in box for an unused account; unreachable servers are reported in a message, not thrown. The list also refreshes automatically after a successful save, and ticked folders stay ticked.

## Performance rules (from the code review)

- Listing a folder (`ListHrefsAsync`, one cheap request) is separate from reading it (`ReadAllAsync`, listing plus multiget). Replace only needs the listing; do not download vCards just to learn their addresses.
- Requests that touch many contacts run `CardDavClient.Parallelism` (4) at a time: multiget batches, PUTs, DELETEs and per-folder counts. The first failure stops the rest, and deletes still happen only after every write succeeded. `FakeDavServer` serializes requests with a lock because of this.
- IceWarp's databases are read off the UI thread (`MainViewModel.LoadAsync`) and each `contact_index.dat` is copied once per load (`FolderCacheReader.ReadAllCounts`).
- `MergeEngine` groups links by root once; a 4,500-contact test guards against going quadratic again.

## How merging works (Core)

- `MergeEngine.Merge` links contacts that share a normalized email or phone (transitively, union-find). Multi-word name-only matches are "possible" duplicates, and groups whose members have incompatible names (for example a shared family phone) are flagged `NeedsReview`; both start as `Pending` and are never merged without the user.
- `MergeGroup.Conflicts` lists single-value fields (display/given/family name, company, title, notes) where members disagree. The user picks one value, keeps both, leaves the field blank, or types one. `BuildFinalList` throws until every group is decided and every conflict resolved. For a user who stops early, `BuildPartialList` applies every finished decision and passes undecided sets (and merges with unresolved conflicts) through untouched; `Summary` counts merged, kept separate and left undecided. The review window's Finish asks for confirmation when anything is undecided.
- A third decision, **Remove** ("Remove all of these (obsolete)"), drops every member of a set from the result. It counts as decided, needs no conflict resolution, and is reported in `MergeSummary` (`Removed` sets, `RemovedContacts`). Only Replace deletes anything on a server: the removed contacts are deleted from the folders being replaced, and the confirmation says so (members living in folders not replaced stay put). A new folder or a vCard file simply leaves them out. Replace still refuses to empty a folder, so removing every contact fails safely. Removing individual contacts that are not part of a duplicate set is not built.
- Multi-value fields (emails, phones, addresses) are unioned. Labels (WORK, CELL ...), name parts (prefix, middle, suffix), department, and raw extra vCard lines (BDAY, URL, CATEGORIES, X- fields) are carried through so a write-back loses nothing the server kept.
- `NotDuplicates` remembers sets the user marked "Keep separate" in `%LOCALAPPDATA%\ContactManage\not-duplicates.json` (pairs of `account|UID` identifiers only, no names or emails). `Apply(result)` runs before the review window and settles any set whose members are all known pairs (flagged `Remembered`, shown as "Keep separate (remembered from before)"); `Record(result)` runs only after a successful save. Switching a remembered set to Merge forgets it. A set that gained a new member is asked about again, and merged contacts (no UID yet) can't be remembered. Demo mode keeps its list in memory only.
- The phone region is the country a number without a country code is read as when numbers are compared. It is US until the user picks another in the **Phone region** box of the main window (saved as `PhoneRegion` in `settings.json`; `PhoneRegions`, `AppSettings`, `Normalizer`). Stored numbers are never changed. Name compatibility is simple word overlap, so "Bob" and "Robert" count as different.

## What was verified on a real server (IceWarp, mail server on the dev network)

- Basic auth over https works. The address-book home is found through `current-user-principal` then `addressbook-home-set`.
- Server allows MKCOL (extended MKCOL creates a new address book), PUT, DELETE, MOVE, COPY, REPORT. A new address book for a normal user works.
- Bulk read: an `addressbook-query` REPORT (with or without `allprop`) returns only empty stub vCards (found in the first real trial: the app wrote 29 blank contacts). The query lists the hrefs; `addressbook-multiget` (batches of 50) returns the real data. `FakeDavServer` mimics this, and the client throws if fewer contacts come back than were listed.
- Round trip fidelity: names, accents, title, phones with types, addresses, URL, categories survive. The server rewrites some values without changing meaning (`;` escaped in notes, `BDAY` becomes `19800229`, trailing `;` on `ORG`). **It drops email type labels** (WORK/HOME become `INTERNET,PREF` on the first and `INTERNET` on the rest). The app warns about it before writing back (`LabelLoss`: counts the emails that have a Work or Home type and adds a note to the confirmation for CardDAV targets other than AOL, which keeps the type).
- A new address book gets a unique address (`Name-3f9a2c`), never one a deleted folder used. In the first real trial a folder recreated at a reused address (`Mergedcontacts`) showed 0 contacts in IceWarp Desktop Client although the server held 27 (webmail showed them after a re-login); a folder at a fresh address showed up correctly. Probable cause is client-side sync state kept per address (not proven).
- Shared and other-user address books sit inside other collections (`Shared/<name>`, `Support Inbox/<name>`), not at the top level of the home. The store maps IceWarp's cache path (`/0/Shared/Team` becomes `Shared/Team`) and falls back to matching the display name. Only the top-level `Contacts` mapping has been confirmed live.

## Current status

Released: **v1.0.7** (tag on `main`; v1.0.7 added the email-type warning, the AOL restore fix and the phone region box; v1.0.4 added the first screen and online-only mode, v1.0.5 a fixed-name download; GitHub Actions builds `ContactManage-<version>-win-x64.zip` and attaches it to the release; per-user installer, unsigned). Done and committed: scope, IceWarp folder reader and Local Folders reader, merge engine, review/conflict window, finish-early with partial results, remembered "not duplicates" list, single-folder cleanup, vCard codec, CardDAV client and store (read, replace, create folder), Gmail through the People API (sign-in, read, label rows, Replace of My Contacts or one label), AOL over CardDAV with an app password (read, Replace), save screens, change log, Restore from backup, credential prompt, demo/real mode switch, 246 unit tests, 10 FlaUI UI tests, GitHub Actions CI and release workflow.

**Confirmed on real accounts:** IceWarp Local Folders (read, then Replace in place with merges and removals, IWDC open; see Next steps); IceWarp trial (read, new folder, replace on a scratch folder); Gmail (516-contact copy on a throwaway account, then one label of the real account); AOL (read, a one-contact write test, then Replace with merged duplicates after the name+email fix). **Not yet run on a live IceWarp server:** the 1.0.1 in-place write of merged contacts (fake-server tests only), and IceWarp "write into an existing folder" and shared folders.

**AOL lessons (details in `docs/AOL.md`):** AOL identifies a contact by name and email, so a PUT with an existing name and email updates that contact instead of creating one. The first Replace therefore lost the merged contacts (54 became 46). Replace now writes a merged contact over one of its own originals and checks new entries exist before it deletes anything. When new entries are folded into contacts the book already had (a **Restore from backup** into the same AOL book), Replace matches each folded contact to the existing one by name and a shared email, keeps those (the server already updated them), reports them as updated with a note, and deletes the rest; a folded contact with no match still stops it with "Nothing was deleted". Fake-server tested only, not yet run on a real AOL account; `tools/CardDavSpike -- --restore-missing <backup.vcf>` remains as a manual route.

## Next steps (in rough order)

**Online-only mode was tested by the maintainer (2026-10-31)**, released in v1.0.4. Not yet confirmed with someone who has no mail program installed: use `docs/ONBOARDING.md` to add the first outside tester.

1. **Local Folders: first live run of the write-back.** Built and tested (`LocalContactWriter`, `LocalFoldersContactStore.ReplaceContactsAsync`; 12 fixture tests with IceWarp's real tables, triggers and search tables; a dry run on a scratch copy of the real 514-contact files: untouched = no change, restore-style round trip kept every row, merge/remove/add counts right, both databases pass `integrity_check`). **Confirmed live (2026-10-30, dev PC, IceWarp Desktop Client open throughout):** one Replace merged and removed duplicates, 514 became 502 with 14 rows updated; the user saw the result in IWDC straight away, **no restart needed**; a check on a copy afterwards found both databases `integrity_check` ok, no orphan rows, name/comment search rows matching the contacts, all three full-text indexes healthy. The user-facing messages therefore say IWDC "normally shows the change straight away; if not, exit and start it again". Still unproven live: adding a brand-new contact (it gets a stub card but no avatar; whether IWDC makes one is unknown) and a Restore from backup on this folder. Backups: vCard backup in `Backups\<time>\Local Folders - Contacts.vcf` (Restore from backup works) plus an online-backup copy of both databases in `Backups\LocalFolders-files-<time>\` (restore by hand with IceWarp closed: delete the two files and their `-wal`/`-shm`, copy the two backups in). The save screen also still offers a new folder in an IceWarp account or a vCard file.
2. One small live run on IceWarp of the 1.0.1 in-place Replace, and the other IceWarp accounts and shared folders.
3. Done in v1.0.7: the email-type warning, the AOL restore that finishes (try it once on a real AOL account, with a backup of that book) and the phone region box.
4. Settings still open: remembered user name.
6. Code signing, README screenshots, update check.
7. Gmail: Google verification (so sign-in stops expiring weekly), write quota on very large lists, whether API-deleted contacts can be recovered.
8. AOL OAuth through Yahoo's commercial access programme, only if a public release needs it.
9. Public-release work: scrub `SCOPE.md`, license, signing certificate, support and privacy statement.

## Open questions

- Whether Yahoo would grant AOL OAuth to an in-house tool (commercial access form; not needed while app passwords work).
- Whether Shared/Archive folders should be merge sources or write targets (currently any CardDAV folder can be either).
- vCard/CSV fidelity as a fallback for servers without CardDAV.
- Dev PC facts used in docs (counts, accounts) are a snapshot and will drift.

## Restore from backup

**Restore from backup** (main window, real mode only) lists every file under `%LOCALAPPDATA%\ContactManage\Backups\<yyyyMMdd-HHmmss>\<Account - Folder>.vcf` with its contact count (`BackupCatalog`). The file name is matched to a current folder (`BackupCatalog.FindTarget`); with no match the user must choose, never a guess. Restore and the change log were confirmed on a throwaway Gmail account (counts correct). Restoring first backs up the folder's present contents (so a restore can be undone), then runs the normal Replace with the backup's contacts, which are written as new entries (new ids; Gmail loses photos and stars). vCard `CATEGORIES` is parsed into `Contact.Groups`, so Gmail labels come back.

## Change log (optional)

The save screen has a checkbox, "Also save a change log showing what happened to each contact" (on by default, remembered for the session, disabled in demo mode). When ticked, `ChangeLog.Build` writes `%LOCALAPPDATA%\ContactManage\Logs\changes-<timestamp>.txt` after a successful save: header (folders read, destination, summary), then sections MERGED (originals, the merged result, how each differing field was settled), KEPT SEPARATE, REMOVED, LEFT UNDECIDED and RETAINED, with the folder of each contact and whether the originals were really changed (only replaced folders are). The log holds names, emails and phones: it stays on the user's PC and is never committed.
