# Microsoft accounts (Outlook.com, Hotmail, Microsoft 365)

Status: **read, folder list and "create a new folder" confirmed on a real Outlook.com account (2026-11-03); Replace (update in place and delete) and restore are tested against a fake Microsoft Graph only.** Before a real run you need the one-time app registration below. Every new kind of live write follows the usual rule: the user says "run it", on a throwaway account or a scratch folder first.

## Decisions

| Decision | Choice |
| --- | --- |
| Integration | **Microsoft Graph v1.0** (REST + OAuth 2.0). Outlook.com has no usable CardDAV. |
| Accounts | Personal Microsoft accounts (Outlook.com, Hotmail, Live) and work or school accounts (Microsoft 365). Tenant `common`. |
| Folders | Graph contacts live in real **contact folders**, so a Microsoft account behaves like a CardDAV account: every folder can be ticked, merged, replaced, and "new folder" is offered. (Gmail is different: one list plus labels.) |
| Scope | `Contacts.ReadWrite` plus `openid email offline_access`. Nothing that reads mail or the calendar. |
| App type | **Public client** (desktop app): sign-in in the system browser with PKCE and a `http://localhost:<port>/` redirect; **no client secret**. |
| Tokens | The refresh token is kept in Windows Credential Manager (`ContactManage:microsoft|<email>`). Microsoft replaces it every time it is used, and `MicrosoftAccounts` saves the new one each time. "Forget saved passwords" clears it. |
| Merge rule | Same as Gmail: a merged contact updates one of its own originals in place (it keeps its photo and identity), the other originals are deleted, last. |

## One-time setup (the administrator, not the app)

1. Sign in to https://entra.microsoft.com (the Microsoft Entra admin center) with an account that may register apps. A free Azure or Entra tenant is enough; a personal Microsoft account has no tenant, so it first needs a free Azure account (a phone number and a card are asked for identity; app registrations cost nothing). The registration can belong to a company account: the people who sign in to ContactManage need no tenant of their own.
2. **Identity, Applications, App registrations, New registration.** Name `ContactManage`. Supported account types: **Accounts in any organizational directory and personal Microsoft accounts**. Redirect URI: platform **Mobile and desktop applications**, value `http://localhost`.
3. **Authentication (Preview):** the redirect URI shows under **Redirect URI configuration**; on the **Settings** tab set **Allow public client flows** to **Yes** and save. Without it sign-in fails with an error about a client secret.
4. **API permissions, Add a permission, Microsoft Graph, Delegated:** `Contacts.ReadWrite`, `offline_access`, `openid`, `email` (the default `User.Read` can stay). No admin consent is needed for a personal account.
5. **Overview:** copy the **Application (client) ID**. Create `microsoft-client.json`:

   ```json
   { "client_id": "00000000-0000-0000-0000-000000000000" }
   ```

   (An optional `"tenant"` can be `common` (default), `organizations`, `consumers` or a tenant id.)
6. Save that file as `%LOCALAPPDATA%\ContactManage\microsoft-client.json` on each PC that runs the app. It holds no secret, but it is not committed to the repository.

**Work or school accounts:** an organisation may stop its users from consenting to an app whose publisher is not verified, or may need an administrator to approve the app once. Personal Microsoft accounts are not affected. (Believed from Microsoft's documentation, not confirmed here.)

## The person using it

1. Install ContactManage (`QUICKSTART.md`), save `microsoft-client.json` as above.
2. Start the app, choose **Online account** (or use it beside a mail program), press **Add Microsoft account** and sign in in the browser. Accept the request to read and change the contacts.
3. The account's folders appear with their counts, under the account's email address. Tick one to clean up its duplicates, or several to merge them, then save with Replace, a new folder, or a vCard file.

## What an Outlook contact can hold

An Outlook contact has fixed places, not open lists: **3 email addresses, 2 business phones, 2 home phones, 1 mobile, and one home, one business and one other address.** A merge can gather more than that. `MicrosoftSlots` decides where each value goes (by its type, then any free place) and counts what fits nowhere; the confirmation says how many values would be left out, and they stay in the vCard backup. Nothing is dropped without that note.

| App contact | Outlook place |
| --- | --- |
| first, middle, last name, prefix, suffix | `givenName`, `middleName`, `surname`, `title`, `generation` |
| company, job title, department | `companyName`, `jobTitle`, `department` |
| notes | `personalNotes` |
| email | `emailAddresses` (no type: Outlook keeps none) |
| phone WORK or unlabelled / HOME / CELL | `businessPhones` / `homePhones` / `mobilePhone` |
| address HOME / WORK / other | `homeAddress` / `businessAddress` / `otherAddress` |
| labels (Groups) | `categories` |
| nickname, birthday, spouse, children, assistant, manager, office, profession, IM addresses, web page, file-as, phonetic names | carried as `X-MS-<PROPERTY>` lines and written back |
| vCard `BDAY`, `URL` from another source | `birthday`, `businessHomePage` (one web page only) |

Photos are not read; an updated survivor keeps its own photo. A new contact has none.

## Facts that shape the code (from Microsoft's documentation)

- `GET /me/contactFolders` lists the folders and, by the documentation, the default `Contacts`. A brand-new Outlook.com account showed no folders at all (2026-11-03), so the store also asks for the built-in folder by its well-known name (`me/contactFolders/contacts`) and adds it when the list lacks it. Contacts of a folder: `/me/contactFolders/{id}/contacts`, paged with `@odata.nextLink`. A count comes from `$count=true`, with a listing as the fallback.
- Writes go through `POST /$batch`, 20 operations per request: create (`POST .../contacts`), update (`PATCH /me/contacts/{id}`, only the named properties change, so an update names every managed property, empty ones as `null` or `[]`) and delete. Requests go out one at a time; a throttled operation (429, 503) is sent again after the `Retry-After` it asked for.
- Outlook may rebuild the display name when other name parts change, so it is always sent.
- Paging links are only followed on `graph.microsoft.com`; the token never goes anywhere else.
- Code: `src/ContactManage.Adapters/Microsoft/` (namespace `ContactManage.Adapters.MicrosoftGraph`, because a namespace called `Microsoft` would hide the real `Microsoft.*` ones), `MicrosoftSlots` in Core. Sign-in code shared with Google is in `OAuthLoopback`.

## Confirmed on a real account (2026-11-03)

Tried on a throwaway personal Outlook.com account holding 23 fake contacts:

- Sign-in in the browser (app registration as described above), the contacts permission request, saved sign-in, folder list with a count, reading all contacts, and **create a new folder** with the merged result (23 contacts merged to 14, as the merge engine predicted). Reading was also checked on a licensed account with about 900 contacts (read only).
- **A brand-new account showed no folders.** The sign-in worked and Graph answered without an error, but the folder list left out the built-in Contacts folder. The store now also asks for it by its well-known name (`me/contactFolders/contacts`) and adds it when the list lacks it; after that the folder showed with the right count.

Things that caught the test out, worth knowing before the next one:

- **A second Outlook.com address made inside an existing account is an alias, not a new account.** Signing in with it opens the main account (the test showed the main account's 909 contacts). A separate test needs a separate Microsoft account (https://signup.live.com, "Get a new email address", in a private browser window).
- **Outlook.com's own vCard import is unreliable.** It put each email into the Notes field ("Contact Imported: Email (map to Primary email)...") instead of the email field, so nothing matched. A CSV import with Outlook's column names (First Name, Last Name, E-mail Address, E-mail 2 Address, Mobile Phone, Home Phone, Business Phone, Company, Job Title) mapped correctly. Anyone who imported vCards into Outlook.com may have contacts with emails only in Notes.
- A work or school (Entra) account with **no Exchange Online licence has no mailbox**, so Graph has no contacts to give it. Use a licensed account.

## Not confirmed on a real account yet

- **Replace** on a real account: updating a survivor in place (PATCH, including clearing a field with `null`), the deletes, and the count check afterwards. Restore from backup.
- Work or school accounts: whether the unverified-publisher notice in the Entra portal ("End users cannot grant consent to newly registered multitenant apps without verified publishers") stops users from signing in. Personal accounts are not affected.

- That a deleted contact goes to Deleted Items and can be recovered (believed; the backup is the safety net either way).
- That PATCH with `null` clears the string properties as expected, and that Outlook does not rewrite display names unexpectedly.
- The order of the folders Graph returns, and whether the other built-in folders of a work account (for example "Skype for Business Contacts") should be hidden.
- Child folders (folders inside folders) are not listed; only the top level is.
- The photo and `extensions` of a contact are neither read nor written.
- Known limits found in review, not changed: Outlook's per-address display name on an email is not kept when a contact is updated (only the address is written); an X-MS-* line that cannot be read is dropped without being counted in the \left out\ note; a Microsoft access token that Microsoft rejects before its recorded expiry is not refreshed until it expires, so the user is asked to sign in again; Refresh while offline can leave an account's rows out of the list until the next successful refresh.
- Safety rules added in review: a gateway error (502, 503, 504) is retried only for reads, never for a $batch write that may already have been carried out; the sign-in port ignores any request that does not carry this sign-in's state.
