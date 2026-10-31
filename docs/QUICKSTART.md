# ContactManage: install and first run

ContactManage merges your contact folders into one de-duplicated list and writes it back. This page is the shortest path to a working install. Download address: **https://contactmanage.scomage.com**

## What you need

| | |
| --- | --- |
| PC | Windows 10 or 11, 64-bit. Nothing else to install: the app is self-contained. |
| Contacts | **Gmail**, or a mail program already on this PC (IceWarp Desktop Client, with its IceWarp, AOL and Local Folders accounts). A mail program is not needed for Gmail. |
| Network | https access to the contact server. |
| Gmail only | The file `google-client.json` from whoever gave you the app, and your Gmail address added as a test user by them (see step 7 below). |

For IceWarp the server address is read from the mail program's own account list, so you never type it. You do need your normal login for each account the first time (user name is usually the full email address).

## Install

1. Download **ContactManage-win-x64.zip** from https://contactmanage.scomage.com. The file name has no version in it; the same link always gives the newest release. (The same zip is on the project's Releases page: https://github.com/Scomage/ContactManage-public/releases/latest, where a copy also carries the version in its name.)
2. Unzip it anywhere, then double-click **Install.cmd**. It installs for the current user only (no administrator rights) to `%LOCALAPPDATA%\Programs\ContactManage`, adds a Start menu shortcut and an entry in Settings > Apps.
3. Start **ContactManage** from the Start menu. Installing a newer zip over an old one updates it; your data in `%LOCALAPPDATA%\ContactManage` is kept.

The program is not code-signed yet, so Windows SmartScreen shows "Windows protected your PC". Choose **More info**, then **Run anyway**.

## First run, step by step

0. **First screen, "Where are your contacts?"** (shown when a supported mail program is installed; without one the app goes straight to online accounts). Choose **Online account** (Gmail, no mail program needed), **A mail program on this PC**, or **Try the demo**. Tick *Remember my choice* to skip the screen next time; **Switch source** (top left of the main window) brings it back and lets you untick it. The choice is kept in `%LOCALAPPDATA%\ContactManage\settings.json`. For Gmail-only use, see "Gmail without a mail program" in `GMAIL.md`.
1. With a mail program chosen, the app finds IceWarp and lists every contact folder with its account, server and contact count. Local Folders (contacts that exist only on this PC) are listed too; you can tick and merge them and write the result back into the same folder. They have no server copy, so the app warns you and saves a backup first. IceWarp normally shows the change straight away; if it still shows the old contacts, exit and start it again. IceWarp's cached copy of a Google account is not shown; use **Add Google account** instead (see below).
2. The **My contacts / Demo** switch at the top right: use Demo first to see the flow with made-up data.
3. Tick one folder (clean up its own duplicates) or several (merge them) and press **Next**.
4. Sign in to each account when asked. Tick **Remember this password on this PC** to store it in Windows Credential Manager (encrypted for your Windows login, this PC only). It is saved only after the server accepts it and is removed automatically if the server rejects it later. **Forget saved passwords** (top right) removes every entry the app made.
5. Review the duplicate sets. Pick Merge, Keep separate or **Remove all of these (obsolete)**; resolve any field conflicts; **Finish** is always available and leaves undecided sets untouched.
6. Choose where the result goes:
   - **Create a new folder** in an account (safest first try; not offered for Gmail or AOL, which cannot make extra folders). If the name is already taken, you are asked whether to replace that folder or pick another name.
   - **Replace the selected folders**, or **write into any existing folder**. A backup is saved first.
   - **Save to a vCard file.**

   Under the choices, **Also save a change log** writes a text file telling what happened to each contact; **Open log folder** (bottom left of the main window) shows the logs in File Explorer. **Restore from backup** (top right) puts a saved folder back.
7. **Add Google account** (top right) signs in to Gmail through your browser. The account then shows as **My Contacts** with its total, named like its IceWarp account (for example "Home Gmail", server "Google"), with each label as an indented subfolder with its count. Tick My Contacts for the whole list, or a single label to work on just that label's contacts. Merged Gmail contacts keep the labels of all duplicates and can be written back with Replace (a backup is saved first). Sign-in needs `%LOCALAPPDATA%\ContactManage\google-client.json` (see `GMAIL.md`). Tick only **one row per Google account** (My Contacts or one label): a contact can be in several, so Next stays disabled with an explanation if you tick two. After a save, restart IceWarp Desktop Client to see the new Gmail counts there; ContactManage and Google Contacts show them straight away.
8. **Refresh** re-reads IceWarp's list and then asks the servers (only for accounts you are signed in to) so new folders and counts show without waiting for IceWarp to sync.
9. **AOL** accounts appear in the list with server `carddav.aol.com`. Sign in with your **full AOL address** and an **app password** (AOL account security settings, "generate app password"); the normal password is rejected. AOL has a single address book (Contacts) and does not allow creating more, so "Create a new folder" is greyed out for it: use **Replace** (backup on) or a vCard file. AOL answers a write with the name and email of an existing contact by updating that contact, so ContactManage writes a merged contact over one of its originals instead of next to them; see `AOL.md`.

Recommended first real run: merge two small folders into a **new folder** and check it in IceWarp or webmail before using Replace.

## Where things are stored

| What | Where |
| --- | --- |
| Saved passwords (only if you tick Remember) | Windows Credential Manager, entries named `ContactManage:<server>\|<account>` |
| Backups before Replace | `%LOCALAPPDATA%\ContactManage\Backups\<timestamp>\*.vcf` |
| Change logs (optional) | `%LOCALAPPDATA%\ContactManage\Logs\changes-<timestamp>.txt` |
| Google sign-in settings | `%LOCALAPPDATA%\ContactManage\google-client.json` (you supply it); sign-in tokens are in Windows Credential Manager |
| Remembered "not duplicates" | `%LOCALAPPDATA%\ContactManage\not-duplicates.json` (account and ID pairs only, no names or emails) |
| Start-screen choice | `%LOCALAPPDATA%\ContactManage\settings.json` (delete it to see the first screen again) |
| IceWarp profile (read only; only Local Folders' two contact databases are ever written, after a backup) | `%APPDATA%\IceWarp Desktop Client\` |
| Temporary database copies | `%TEMP%\ContactManage\`, deleted right after reading |

## Uninstall and clean up

1. Settings > Apps > ContactManage > Uninstall (or run `uninstall.ps1` in the install folder). If you copied the app by hand instead, delete its folder.
2. Remove saved passwords: use **Forget saved passwords** in the app first, or run `cmdkey /list` and delete entries starting with `ContactManage:` (`cmdkey /delete:<target>`).
3. Delete `%LOCALAPPDATA%\ContactManage\` (keep `Backups` if you may need to restore).

## Security notes

- Passwords go only over https, only to the server they were entered for; redirects to another host are refused.
- The app never reads or copies IceWarp's stored credentials. The only IceWarp files it ever writes are the two contact databases of Local Folders, and only when you replace that folder (a backup of both is saved first).
- It talks to the server over CardDAV with Basic authentication, so the server must be https.
- Nothing is sent to any third party; there is no telemetry and no AI component in the app.

## Troubleshooting

| Symptom | What to do |
| --- | --- |
| "No profile found" | Start IceWarp once and add an account, then press Refresh. |
| Sign-in keeps failing | Use the full login (usually the email address). AOL needs the full AOL address and an app password, typed without spaces (a user name like "Home AOL" is only IceWarp's label). Gmail signs in through the browser instead. |
| "Nothing was deleted" after Next/Save | The server treated a new contact as one it already holds (AOL: same name and email), so the save stopped before deleting anything. Your backup is in the Backups folder; send the maintainer the message. |
| IceWarp Desktop Client shows the old count after a Gmail or AOL save | It caches its own copy; exit and start it again. |
| "Server rejected the user name or password" vs "failed (HTTP 403)" | 401 is a wrong login; 403 means the server refused that action (permissions). |
| New folder shows empty in IceWarp Desktop Client but webmail shows contacts | The client has not synced it yet; sign out and in, or wait for its next sync. Each new folder gets a fresh address so this should not persist. |
| A folder is missing from the list | Press Refresh. Only top-level address books are discovered from the server; a nested shared folder needs IceWarp to have synced it. |
| Window shows "Reading the mail client's folders..." for long | IceWarp's database copy is large or on a slow disk; it runs in the background and finishes by itself. |
| Server drops WORK/HOME on emails | Known IceWarp behavior after a write; phone and address labels are kept. |

Report problems with the exact message text. Messages never contain contact data or passwords.
