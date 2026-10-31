# Gmail contacts: design and OAuth plan

Status: sign-in, reading, label-aware merging and write-back (My Contacts or a single label) are built and confirmed on a throwaway copy of 516 real contacts and on one label of the real account (see "Writing back").

## Goal

Clean up a Gmail account's main contact list ("My Contacts"): find duplicates, merge each set into one contact that carries **all the labels of every member**, so the existing labels stay accurate. Same review flow as the IceWarp folders (duplicate sets, conflicts, Keep separate, partial finish, backup first).

## Decisions

| Decision | Choice |
| --- | --- |
| Integration | **Google People API** (REST + OAuth 2.0). Google's CardDAV is OAuth-only and does not expose labels, so it cannot do this job. |
| Accounts | Personal Gmail (@gmail.com) first. Workspace accounts would also work (an Internal OAuth app would skip verification there). |
| Audience | The user and a few colleagues first; public later. |
| OAuth app type | External, **Testing** status at first: up to 100 named test users, refresh tokens expire after 7 days (re-sign-in weekly). Moving to Production later removes the expiry; unverified production apps show a warning screen until Google verifies the app (weeks; start the paperwork before a public launch). |
| Scope | `https://www.googleapis.com/auth/contacts` (read and write personal contacts). Not yet confirmed whether Google classes it sensitive or restricted; check the scope reference before verification. |
| Merge rule | People API has no merge. The surviving contact is updated in place with the union of fields and labels; the other members are deleted. |

## OAuth connection (desktop app)

1. **Installed-app flow with PKCE and a loopback redirect.** The app starts a listener on `http://127.0.0.1:<random port>/`, opens the system browser at Google's consent page, and receives the authorization code on that address. The user types the Google password only on Google's page; the app never sees it. Use the system browser, never an embedded browser (Google blocks those).
2. **Tokens.** Access token (about an hour) lives in memory. The refresh token is stored in Windows Credential Manager through the existing `ICredentialVault`, entry `ContactManage:google|<email>`, saved only after the first successful API call. Same "Forget saved passwords" button clears it. A rejected refresh token (revoked, or the 7-day expiry in Testing) means "sign in again" with the browser.
3. **Client ID.** Not a user secret, but not committed to the repo either: the app reads `%LOCALAPPDATA%\ContactManage\google-client.json` (the file Google lets you download). A public release can embed its own ID. Never log tokens, codes or the client secret.
4. **Consent screen text** says what the app does: reads and changes your contacts, nothing else. No other scopes (no Gmail mail access).

### One-time Google Cloud setup (done by the account owner, not by the app)

1. Open https://console.cloud.google.com, create a project named `ContactManage`.
2. **APIs & Services, Library:** enable **People API**.
3. **Google Auth platform (OAuth consent screen):** user type **External**; app name `ContactManage`; support and developer email; add the scope `.../auth/contacts`; add every Gmail address that will sign in under **Test users**.
4. **Credentials, Create credentials, OAuth client ID:** application type **Desktop app**; download the JSON.
5. Save that file as `%LOCALAPPDATA%\ContactManage\google-client.json` on each PC that runs the app. Do not paste it into chat or commit it.

## Gmail without a mail program

Some people only use Gmail in the browser and have no mail program installed. They can still clean up their contacts: the app asks for no mail program when the **Online account** source is used (it is the only choice offered when no supported mail program is found, and a choice on the first screen otherwise).

The rollout is controlled: the Google app stays in **Testing** status and the administrator adds each person before they try it. Testing status allows up to 100 test users, and each person has to sign in again every 7 days.

**Administrator, once per person**

1. Google Cloud console, project `ContactManage`, **Google Auth platform, Audience** (the old name is "OAuth consent screen"): under **Test users** add the person's Gmail address. Without this, Google refuses the sign-in ("access blocked").
2. Give them the file `google-client.json` (never by chat or e-mail body that is archived widely; a direct file transfer is better) and the ContactManage zip.

**The person, once**

1. Install ContactManage (see `QUICKSTART.md`, option A).
2. Save `google-client.json` as `%LOCALAPPDATA%\ContactManage\google-client.json` (paste that path into File Explorer's address bar; create the `ContactManage` folder if it is missing).
3. Start ContactManage, choose **Online account**, press **Add Google account** and sign in in the browser. Google warns that the app is not verified: choose *Advanced*, then *Go to ContactManage (unsafe)*. This is expected while the app is in Testing status.
4. Tick **My Contacts** (the whole list) or one label, press **Next**, review duplicates, and save. Saving back to Gmail with **Replace** keeps a backup first; **Restore from backup** puts it back.

An account is named by its email address (there is no mail program to take a name from). Every sign-in lasts 7 days; after that the app says to sign in again with **Add Google account**.

## People API facts that shape the code

- Contacts and labels: labels are **contact groups**; a contact's labels are its `memberships`. A contact appears in "My Contacts" through membership of the `myContacts` system group, which merged contacts must keep.
- Updates need the contact's current `etag` and a field mask (`updatePersonFields`); a stale etag is rejected, so re-read and retry.
- **Writes for one user must be sent one after another, not in parallel** (Google's guidance). The CardDAV code's four-at-a-time writes must not be reused here. Batch calls (create, update, delete) are the efficient route, sent sequentially.
- Writes can take minutes to show in sync responses, so read-after-write verification must not rely on sync tokens; re-list instead.
- Quotas per request (documented by Google): single create/update costs 1 critical write and 1 daily contact write; batch create/update 6 critical writes and 200 daily contact writes; batch delete 10 write requests. Exact daily limits to be confirmed before a large first run.
- Deleted-contact recovery was not confirmed, so a vCard backup is always written first (as Replace does).

## Data model changes

- New `Contact.Groups` (set of label names) next to the existing `Contact.Labels`, which today means *vCard parameters* (WORK, CELL). Rename suggestion: keep `Labels` for parameters and call the Gmail ones `Groups` in code, "labels" on screen.
- `ContactMerger` unions `Groups` across members (no conflict dialog needed: labels only accumulate).
- Values the model does not understand (birthdays, URLs, events, relations, custom fields, nicknames) must not be lost when the other members are deleted: union the multi-valued ones by value, carry the raw JSON of the rest on the survivor.
- The survivor is chosen as the member with the most fields filled (ties: oldest); its `etag` and unmodeled data stay.

## Build order

1. **Read-only probe** (tool, own console window): sign in through the browser, list contacts and labels, print counts and label names only. Proves the OAuth flow and the client ID setup.
2. `GoogleAuth` + `GooglePeopleStore : IContactStore` (read), Gmail account shown in the folder list as "My Contacts" (one folder per account); fake People API server for tests.
3. Merge with label union and review window (labels listed per member).
4. Writer: backup, update survivors, delete losers (sequential batches), verify by re-listing, report. First run only against a scratch label or a throwaway Gmail account.
5. App: "Add Google account" button, sign-in/sign-out, token removal under "Forget saved passwords".

## Not in scope yet

Reading IceWarp's cached Google folders (that cache lives in `contact_data.dat`, see `ICEWARP.md`); the People API replaces it. "Other contacts" (auto-saved addresses) and the Workspace directory.

## How a Gmail account shows in the folder list

- One row per signed-in account: **My Contacts**, with the account's total, selectable. Under it, one indented row per label with its count and its own checkbox (tick a label to work on just that label's contacts; tick My Contacts for everything; never both, see below).
- The account is shown under the name its IceWarp account has ("Home Gmail") and **Google** as the server. When you add an account the app names it automatically if exactly one IceWarp Google account has no name yet, asks you to choose if several, and falls back to the email address. Names are kept in `%LOCALAPPDATA%\ContactManage\google-accounts.json` (names and emails only, no tokens); "Forget saved passwords" clears it.
- IceWarp's cached copy of a Google account (its labels as greyed folders) is hidden: it duplicated the same data and could not be read.
- Totals and labels come straight from Google at startup (for saved sign-ins) and on every Refresh. A sign-in that Google no longer accepts shows a message asking you to use Add Google account again.
- The arrow in front of **My Contacts** folds the label rows away and unfolds them again (open by default; the state is remembered for the session).
- Before a Google account is signed in, the list shows a placeholder row for each Google account IceWarp knows: its name (for example "Home Gmail"), server **Google**, no count, no checkbox, and "Not signed in. Use Add Google account (top right)." in the Path column. Signing in replaces the placeholder.

## Writing back (built; confirmed on a throwaway account and on one label of a real account)

**Replace** on an account's My Contacts list, or on a single label: a label row can be ticked, then only that label's contacts are read, merged and written (other contacts are never updated or deleted; merged contacts keep every label). "New folder" is not offered for Gmail (one list plus labels). Order: back up to a vCard file, create missing labels, create contacts that have no original in the list, update each merged set's first original in place (`updateContact`, version tag from the read, so a contact edited in Gmail since stops the run), add labels with `members:modify` (stars and other labels untouched), delete the remaining originals last (`batchDeleteContacts`), then re-list to verify the count. Requests are sequential; 429/503 retry with backoff up to six times. Fields from the merge that have no Gmail place (PHOTO, a second name) are left out and reported. Read-only members Google adds to items (metadata, formattedType) are stripped before sending.

Confirmed on a throwaway account holding a complete copy of a real 516-contact list: merge and Replace of the whole of My Contacts worked. Confirmed on the real account: Replace of one small label (23 contacts after the merge) worked. Still unconfirmed: the write quota on lists much larger than 516 and whether deleted contacts can be restored; the backup vCard is the safety net.

Only one row of a Google account can be ticked at a time (My Contacts or one label), because a contact can be in several of them and would be read and replaced twice. Next stays disabled and the status line says why.

**IceWarp Desktop Client does not pick up the change on Refresh.** It keeps its own cached copy of a Gmail account's labels and counts, so after a write it still shows the old numbers until the client is exited and started again. ContactManage's own list (read from Google after the save) and Google Contacts on the web show the new counts straight away.

Also confirmed on the throwaway account: the optional change log read correctly, and Restore from backup returned the folder to the right contact count.
