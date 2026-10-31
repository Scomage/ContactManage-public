# Installation and deployment

## Requirements

- Windows 10 or later (x64).
- To build: .NET 9 SDK. To run a published self-contained build: nothing extra.
- IceWarp Desktop Client installed and signed in to at least one contact account (or use Demo mode). Its accounts include IceWarp servers and AOL; a Gmail account is added inside ContactManage with **Add Google account**.
- Network access to your contact server. The app talks to it over https only.
- AOL needs an **app password** (AOL account security settings), not the normal AOL password. See `AOL.md`.

## Build and run from source

```
git clone https://github.com/Scomage/ContactManage-public.git
cd ContactManage-public
dotnet build
dotnet test
dotnet run --project src/ContactManage.App                 # real contacts
dotnet run --project src/ContactManage.App -- --demo       # sample data
```

The app is the project `src/ContactManage.App` (WPF, `net9.0-windows`).

## First run

1. The app detects IceWarp and lists contact folders. If IceWarp was never started, open it once and add an account so a profile exists.
2. Choose **My contacts** (default) or **Demo (sample data)** at the top right. Demo never reads or changes anything real.
3. Tick one folder (to clean up its own duplicates) or several (to merge them). Greyed-out folders can't be read yet. Press Next.
4. You are asked to sign in to each account involved. The user name defaults to the account name if it looks like an email address; otherwise type your login (usually your email address). The password is kept in memory only, unless you tick "Remember this password on this PC"; then Windows Credential Manager keeps it, encrypted for your Windows login, and the next run signs in automatically. "Forget saved passwords" on the main window removes them.
5. Review duplicates, press Finish, choose a destination, confirm.

Recommended first trial on real data: merge into a **new folder** and look at the result in IceWarp before using Replace. AOL and Gmail have no separate folders to create (AOL has one address book, Gmail one list plus labels), so there the first trial is a vCard file, or Replace with the backup on. The save screen greys out "new folder" for a server that cannot create one.

## Where files go

| What | Where |
| --- | --- |
| Backups made before Replace | `%LOCALAPPDATA%\ContactManage\Backups\<yyyyMMdd-HHmmss>\<account> - <folder>.vcf` |
| Start-screen choice (online account, mail program, ask) | `%LOCALAPPDATA%\ContactManage\settings.json`. Holds no contacts or credentials. |
| Remembered "not duplicates" pairs | `%LOCALAPPDATA%\ContactManage\not-duplicates.json` (identifiers only). Delete the file to start over. |
| IceWarp profile (read only, except Local Folders' contact databases when you replace that folder) | `%APPDATA%\IceWarp Desktop Client\` |
| Temporary database copies | Windows temp folder, deleted right after reading |

To restore a folder from a backup use **Restore from backup** in the app (top right). It saves the folder's present contents first, so a restore can be undone.

## Package and install on other PCs

```
powershell -File scripts\package.ps1 -Version 1.0.1
```

This writes `dist\ContactManage-1.0.1-win-x64.zip` (about 62 MB): the self-contained app (no .NET install needed on the target PC) plus `Install.cmd`, `install.ps1` and `uninstall.ps1`. On the target PC, unzip and double-click **Install.cmd**. The install is per user (no administrator rights): files go to `%LOCALAPPDATA%\Programs\ContactManage`, with a Start menu shortcut and an entry in Settings > Apps. Running it again over an old install updates it. User data in `%LOCALAPPDATA%\ContactManage` (backups, logs, settings) is never touched by install, update or uninstall.

**From GitHub:** pushing a tag such as `v1.0.1` runs `.github/workflows/release.yml`, which tests, builds the zip and attaches it to a GitHub release. The release has the zip twice: with the version in its name and as `ContactManage-win-x64.zip`, so `.../releases/latest/download/ContactManage-win-x64.zip` is a link that never changes. The zip is then on the Releases page.

Gmail sign-in needs the Google client settings file `%LOCALAPPDATA%\ContactManage\google-client.json` on each PC. It holds the OAuth client secret and is not part of the package or the repository; hand it to colleagues separately (see `GMAIL.md`).

Unsigned executables trigger a SmartScreen warning ("More info", then "Run anyway"); code signing is planned before wider release. A proper MSI/MSIX installer is not planned for now.

## Trying a server with the probe tool

`tools/CardDavSpike` checks a CardDAV server without using the app. It needs a real console window because it hides the password as you type, so start it in its own window:

```
dotnet run --project tools/CardDavSpike                       # read-only: capabilities, address books, counts
dotnet run --project tools/CardDavSpike -- --write-test       # creates, fills and deletes a scratch address book
dotnet run --project tools/CardDavSpike -- --log out.log      # also copy the output to a file
dotnet run --project tools/CardDavSpike -- --single-contact-test   # for servers that cannot create an address book (AOL): adds, updates and deletes one fake contact
dotnet run --project tools/CardDavSpike -- --key-test              # how the server decides two vCards are the same contact (four fake contacts)
dotnet run --project tools/CardDavSpike -- --restore-missing <backup.vcf>   # adds back what a backup holds and the book lacks; never deletes; asks for YES
```

The write test only touches an address book named `ContactManage-test-<random>` that it creates and deletes. The single-contact and key tests touch only fake entries named "ContactManage Test" / "CM Key Test" and delete them. The restore tool prints counts only, never names or addresses. None of them prints contact data.

## Troubleshooting

| Symptom | Likely cause |
| --- | --- |
| "Mail client: IceWarp Desktop Client (No profile found...)" | IceWarp was never run or has no account. Start it and add an account. |
| Folder list is empty or shows an error | The profile is locked or unreadable. Close and reopen IceWarp, press Refresh. |
| Sign-in keeps failing | Use your full login (usually the email address). For AOL use the full AOL address and an **app password**, typed without spaces. Gmail does not use a typed password: it signs in through your browser. |
| "Could not find the folder ..." | The folder's cache path does not match the server. Press Refresh in IceWarp so it syncs, then try again. Shared folders are not confirmed to work yet. |
| Counts differ from the server | The list shows IceWarp's last-synced counts. They are updated when IceWarp syncs. |
| Changes not visible in IceWarp | IceWarp shows server changes after its next sync. |
| IceWarp Desktop Client still shows the old count for a Gmail or AOL folder after a save | It keeps its own cached copy and does not re-read on Refresh. Exit and start IceWarp Desktop Client again. ContactManage's own list and the provider's website already show the new counts. |
| "Nothing was deleted" after a save | The server answered a new contact as an update of one it already holds (AOL does this for a write with the same name and email), so ContactManage stopped before deleting anything. Your backup is in the Backups folder; tell the maintainer the message. |
