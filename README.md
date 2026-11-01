# ContactManage

A Windows app that combines contact folders from your mail client into one clean list.

You choose the folders, ContactManage finds the duplicates, you settle anything it is unsure about, and then it saves the result: replace the folders, create a new one, or export a vCard file.

**No mail program needed for Gmail:** if you only use Gmail in the browser, start the app, choose **Online account** and sign in with your Google account (see `docs/GMAIL.md`, "Gmail without a mail program"). Where a mail program is installed, a first screen asks which you want and can remember the answer.

Supported today: **IceWarp Desktop Client** (its CardDAV accounts, including **AOL** with an app password; see `docs/AOL.md`) and **Gmail** (sign in with Google; see `docs/GMAIL.md`). Outlook, Thunderbird and eM Client are planned.

## What it does

1. **Asks where your contacts are** (online account, a mail program on this PC, or the demo; skipped when there is nothing to choose), then lists every contact folder with its account, server and contact count.
2. **You tick the folders** to merge, or a single folder to remove the duplicates inside it.
3. **Reads and merges them.** Contacts that share an email address or phone number are treated as the same person. Contacts with only the same name are shown as "possible" duplicates for you to decide.
4. **You review**: merge or keep separate, and for fields where copies disagree (company, title, notes...) pick one value, keep both, or type your own.
   You don't have to finish: press Finish at any time and everything you have decided is saved, while undecided sets are left exactly as they were for another day.
   When you mark a set "Keep separate", ContactManage remembers it, so those contacts are not offered as duplicates again on later runs. Change a remembered set to Merge to forget it.
5. **You save**: replace the selected folders (a backup of each is saved first), create a new folder in an account, or save a `.vcf` file. An optional change log lists what happened to every contact, and **Restore from backup** puts a folder back as it was.

Try it without touching real contacts: choose **Try the demo** on the first screen, **Demo (sample data)** at the top right of the window, or start the app with `--demo`. **Switch source** (top left) brings the first screen back.

## Safety

- Nothing is written until you confirm, and replacing a folder saves a backup first (`%LOCALAPPDATA%\ContactManage\Backups`).
- New contacts are written before old ones are deleted, and CardDAV checks they really arrived first (if a server folded one into a contact it already had, as AOL does for the same name and email, that contact is kept and counted as updated; if the folded contact cannot be matched, the save stops with "Nothing was deleted"). A merged contact is written over one of its own originals. A folder is never emptied, and counts are checked afterwards.
- Passwords are asked for each session, sent only over https to their own server, and never logged. Saving one on your PC (Windows Credential Manager) is opt-in. Gmail uses Google's own sign-in in your browser; the app never sees your Google password.
- Contacts are processed on your PC. Nothing is sent anywhere except to your own contact server.

See `docs/SECURITY.md` for the full list of rules.

## Known limits

- Gmail: tick My Contacts **or** a single label, one row per Google account (Next stays disabled otherwise). Google contact photos and starred status of merged-away duplicates are not carried over. While the Google app is in Testing status, the Google sign-in expires every 7 days.
- AOL: needs an app password; it has a single address book, so "new folder" is not offered (Replace with backup, or a vCard file). Tested on a real account: read, Replace with merged duplicates, restore.
- IceWarp Desktop Client keeps its own copy of Gmail and AOL folders and only shows new counts after you exit and start it again.
- IceWarp Local Folders (contacts kept on this PC only, no server copy) can be read, merged and written back in place. Before writing, the app saves a vCard backup and a copy of IceWarp's contact database files, and warns you that nothing on a server can bring the contacts back. IceWarp Desktop Client may stay open and normally shows the change straight away (if not, exit and start it again).
- IceWarp's cached copy of a Google account can't be read; use Add Google account for Gmail.
- IceWarp servers drop the work/home label on email addresses when saving.
- CardDAV: the top-level Contacts folder is confirmed to work on IceWarp and AOL; shared folders on IceWarp are not yet verified.

## Install

Unzip `ContactManage-<version>-win-x64.zip` (from the Releases page) and double-click `Install.cmd`. Per-user, no administrator rights, no .NET needed. See `docs/QUICKSTART.md`.

## Build and run

```
dotnet build
dotnet test
dotnet run --project src/ContactManage.App
```

Requires the .NET 9 SDK and Windows 10 or later. More in `docs/INSTALLATION.md`.

## Documentation

| File | Contents |
| --- | --- |
| `CLAUDE.md` | Working notes for contributors and Claude Code: decisions, rules, status, next steps |
| `SCOPE.md` | Requirements, decisions, risks and milestones |
| `docs/ARCHITECTURE.md` | Projects, key types, data flow, merge rules |
| `docs/GMAIL.md` | How Gmail is read and written (Google People API), sign-in, limits |
| `docs/AOL.md` | AOL contacts over CardDAV with an app password: what the server allows, test results |
| `docs/ICEWARP.md` | How IceWarp stores accounts and folders, and how the CardDAV server behaves |
| `docs/QUICKSTART.md` | One-page install and setup for technical users |
| `docs/ONBOARDING.md` | Checklist and message for giving the app and Gmail access to an outside user |
| `docs/INSTALLATION.md` | Building, running, publishing and deploying to other PCs |
| `docs/SECURITY.md` | Credential handling and data-safety rules |

## Repository

Source: https://github.com/Scomage/ContactManage-public.
