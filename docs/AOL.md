# AOL contacts

Status: reading works in the app; a single-contact write test (add, update, delete) worked on a real account. The first Replace lost contacts (see below); after the fix, Replace on the real account (with merged duplicates) worked.

## How AOL is reached

AOL contacts are read and written over **CardDAV** at `https://carddav.aol.com/.well-known/carddav`, the same client the app uses for IceWarp. The user name is the full AOL address. The password is an **app password** (AOL account security, "generate app password"), not the normal AOL password. The app asks for it in its own sign-in box; "Remember this password on this PC" stores it in Windows Credential Manager as for any CardDAV account.

**OAuth is not an option today.** Yahoo, which runs AOL, offers OAuth for CardDAV, IMAP and CalDAV on AOL and Yahoo accounts only to applicants approved through its commercial access form (contact: mail-api@yahooinc.com); those scopes are not self-serve in the developer console. That is worth pursuing before a public release, not for colleagues' use now.

## What AOL's server does (checked on a real account)

| Topic | Finding |
| --- | --- |
| Address books | Exactly one, `Contacts`, at `/dav/<address>/Contacts/` inside the home `/dav/<address>/`. |
| Allowed methods | `REPORT, GET, DELETE, PROPFIND, OPTIONS, PUT`. **No MKCOL, MOVE or COPY**, although the `DAV` header mentions `extended-mkcol`. Creating an address book answers 404. |
| Listing | An `addressbook-query` without a `<filter>` answers **HTTP 400**. The client then lists the folder with a PROPFIND (depth 1), which works. `CardDavClient.ListHrefsAsync` does this fallback for HTTP 400, 403, 405 and 501. |
| Reading | `addressbook-multiget` returns the real vCards; a 54-contact folder read through the app with no errors. |
| Writing | PUT of a new contact: 201. PUT over the same address: 204. DELETE: 204. The book returned to its exact old contents. |
| Fidelity | Name, company, phone, note came back unchanged. The email type `WORK` came back as `INTERNET,WORK` (kept, with a type added; IceWarp drops it). AOL's own contacts were simple: a name and one email. |

### What went wrong in the first Replace (54 contacts became 46) and why

AOL recognises a contact by its **name and email**. A PUT to a new address whose vCard has the name and email of an existing contact is answered with 204 and **updates that existing contact**; the new address does not exist afterwards (GET gives 404). The same UID or the same email with another name stays separate, and a phone added to such a PUT lands on the existing contact. The first version of Replace wrote a merged contact under a new address and then deleted every old member, which deleted the merged contact too (54 minus 8 members = 46). Restoring from a backup repeated the effect, because the backup's contacts matched the ones already there and the delete step then removed them (the counts went 54, 46, 50, 0).

The fix (in `CardDavContactStore.ReplaceContactsAsync`): a merged contact is written over the address of one of its own originals (keeping that original's UID) and only the other originals are deleted; contacts that are written as new entries are checked in a second listing before any delete, and if one is missing the Replace stops with "Nothing was deleted". Tests: `FakeDavServer.FoldSameNameAndEmail` imitates AOL.

Recovery in that case: `tools/CardDavSpike --restore-missing <backup.vcf>` compares a backup with the book by name and email and adds only what is missing (counts printed, YES required, never deletes). It brought the account back to its 50 distinct contacts.

AOL can hold two contacts with the same name and email: four copies made in IceWarp Desktop Client ("copy to folder") appeared as separate entries. A PUT from ContactManage cannot create such a copy.

## What the app does about it

- **New folder is not offered** for an account whose server does not list MKCOL in its `Allow` header (`CardDavClient.AllowsNewAddressBooksAsync`, `IContactStore.CanCreateFolder`). A server that does not say stays as before. If a create is refused anyway (403, 404 or 501) the message says the server probably allows only one address book.
- The ways to save an AOL result are **Replace** the `Contacts` book (backup first, change log optional) or **save a vCard file**.
- Merge, review, Keep separate, Remove and the backup work as for IceWarp.
- **Restore from backup** on AOL: a backup's contacts are written as new entries, and AOL answers those whose name and email it already has as updates. The restore therefore adds what is missing and refreshes what matches, then stops with "Nothing was deleted" instead of deleting. For a clean additive restore run `tools/CardDavSpike -- --restore-missing <backup.vcf>` (it asks for YES and prints counts only).

## Not yet done

- Make the in-app restore finish without the "Nothing was deleted" stop on AOL.
- Write limits on large books (the test book had 54 contacts).
- OAuth through Yahoo's commercial access programme.

## Testing tool

`tools/CardDavSpike` has these AOL-safe modes (run them in your own PowerShell window; the password is typed there and never logged):

```
dotnet run --project tools/CardDavSpike -- --log <file>                       # read-only: methods, address books, counts, property names
dotnet run --project tools/CardDavSpike -- --single-contact-test --log <file> # adds one fake contact to the first book, reads, updates and deletes it
dotnet run --project tools/CardDavSpike -- --identity-test --log <file>       # four fake contacts: what the server treats as the same contact (UID, email)
dotnet run --project tools/CardDavSpike -- --key-test --log <file>            # four fake contacts: same name and email, with or without a phone, another email
dotnet run --project tools/CardDavSpike -- --restore-missing <backup.vcf>     # adds the backup's missing contacts only; asks for YES; counts only
```

The older `--write-test` creates a scratch address book and so cannot work on AOL.
