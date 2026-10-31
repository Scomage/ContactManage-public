# Security and data-safety rules

These apply to the app and to anyone (or any AI assistant) working on the code.

## Credentials

- IceWarp stores account secrets in `accounts.dat`. They are not needed and must never be read, printed, logged, copied or sent anywhere. The reader selects only `AccountName` and `Host`.
- The app asks the user for a user name and password per account. The password is held in memory for the session, sent only in the HTTP `Authorization` header, and never written to disk (except as below), logs or the command line.
- **Remember is opt-in.** The sign-in box has an unticked "Remember this password on this PC" checkbox. When ticked, the login is saved in Windows Credential Manager (`WindowsCredentialVault`, entries named `ContactManage:<server host>|<account>`, persistence "local machine"), which encrypts it for the signed-in Windows user; it is not roamed to other PCs. It is saved only after the server accepted it (`SavedLogins`), is reused silently next time, and is deleted as soon as the server rejects it. The entry is tied to the server host, so it is never offered to another server. The "Forget saved passwords" button on the main window removes every `ContactManage:` entry. The app never reads Credential Manager entries it did not create.
- Passwords are sent only over https and only to the server they were entered for. Plain `http://` addresses are refused. Redirects to a different host are refused, so credentials cannot be forwarded elsewhere.
- Login failures are reported without echoing what was typed. After three failed attempts the operation stops.
- AOL needs an app password, handled like any other typed password (memory only, or Credential Manager if "Remember" is ticked). Gmail uses Google's browser sign-in; the app holds only a refresh token, in Credential Manager, and never the Google password.
- Tools that need a login (the probe tool) read the password themselves from a console window. A password must never be typed into a chat, passed as an argument or stored in a script.

## The mail client's files

- The IceWarp databases are opened only as temporary copies (`SqliteCopy`: the `.dat` plus `-wal` and `-shm`), and the copy is deleted afterwards. The originals are never opened for writing.
- The app does not write into IceWarp's cache for server accounts: changes go to the server over CardDAV and show up after IceWarp's next sync. The one exception is **Local Folders** (contacts that exist only on the PC): replacing that folder changes `contact_index.dat` and `contact_data.dat` in place, in one transaction, only for a database layout the app knows, after an online backup of both files, and with the vCard backup that Restore from backup uses. `accounts.dat` and the account folders are never written.

## Contact data

- Contacts are processed on the PC. The only network traffic is to the user's own contact server. There is no telemetry.
- Before Replace, a backup of every affected folder is saved to `%LOCALAPPDATA%\ContactManage\Backups`. Backups contain full contact details, so protect that folder like the contacts themselves.
- The remembered "not duplicates" file (`%LOCALAPPDATA%\ContactManage\not-duplicates.json`) holds only account and contact identifiers, never names, emails or phone numbers. A corrupt file is renamed, not overwritten.
- Log files and test output must not contain contact values. The probe tool prints only vCard property names and counts; the write tests use fake contacts; `--restore-missing` prints counts only. The change logs (`%LOCALAPPDATA%\ContactManage\Logs`) and backups hold real contact details and stay on the PC.

## Write safety

- Nothing is written without an explicit confirmation naming the folders.
- New contacts are written first and old ones deleted last, so a failure part-way loses nothing. On CardDAV the new entries are then looked up in a second listing, and if any is missing nothing is deleted (a server such as AOL can answer a new entry with an update of an existing contact). A merged contact is written over one of its own originals, so it cannot be deleted along with them.
- A folder is never emptied: an empty merged list is refused.
- Gmail: only one row (My Contacts or a label) of a Google account can be processed at a time, so no contact is read or replaced twice.
- Counts are re-read afterwards and a mismatch is reported as a warning.
- New files use `If-None-Match: *` so an existing contact is never overwritten by accident.
- Test real writes on a scratch address book or a new folder first.

## Repository

- Do not commit logs, backups, `.vcf` exports of real contacts, or any file produced from `accounts.dat`.
- Delete screenshots that may show mail or contacts as soon as they have been used.

## Reporting a problem

Report security concerns directly to the maintainer (support@scomage.com).
