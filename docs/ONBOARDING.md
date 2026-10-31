# Giving the app to an outside user (Gmail, no mail program)

One page for the administrator. Total time per person: about 10 minutes. The same steps also work for a colleague who has a mail program; they just pick **Online account** on the first screen.

What the person needs: a Windows 10 or 11 PC (64-bit), a Gmail address, the app zip, and the file `google-client.json`. They do not need IceWarp, a mail program, .NET or administrator rights.

## Administrator checklist

1. **Add their Gmail address as a test user** (while the Google app is in Testing status).
   - https://console.cloud.google.com, project `ContactManage`, **Google Auth platform, Audience, Test users, Add users**, type the address, Save.
   - Limit: 100 test users. Without this step Google shows "access blocked" at sign-in.
2. **Send the app.** Give them the link to the newest zip, which never changes: https://github.com/Scomage/ContactManage-public/releases/latest/download/ContactManage-win-x64.zip (the same file is also on the Releases page as `ContactManage-<version>-win-x64.zip`), or a copy you built with `scripts\package.ps1 -Version <x.y.z>`.
3. **Send `google-client.json` by direct file transfer** (not a group chat, not pasted into a message). It holds the Google client secret, which must never be committed, posted or forwarded. Keep your own copy at `%LOCALAPPDATA%\ContactManage\google-client.json`.
4. **Send the message below**, then add a reminder for 7 days: Google makes every test user sign in again weekly.

## Message to the person

> **ContactManage: set-up (about 10 minutes)**
>
> 1. Download and unzip the app (link above, file `ContactManage-win-x64.zip`) and double-click **Install.cmd**. Windows may say "Windows protected your PC": click **More info**, then **Run anyway** (the app is not code-signed yet).
> 2. Open File Explorer, paste `%LOCALAPPDATA%\ContactManage` in the address bar and press Enter (create the folder if Windows says it is missing). Copy the file `google-client.json` I sent you into it.
> 3. Start **ContactManage** from the Start menu. On the first screen choose **Online account**, then press **Add Google account** and sign in with your Gmail address in the browser.
> 4. Google will warn that the app is not verified. Click **Advanced**, then **Go to ContactManage (unsafe)**, then allow access to your contacts. This is expected: the app is in a private test.
> 5. Tick **My Contacts** (or one label), press **Next**, review the duplicates and finish. A backup of your contacts is saved first; **Restore from backup** puts it back.
>
> Sign-in lasts 7 days. After that press **Add Google account** again. Nothing leaves your PC except the calls to Google. Tell me the exact message text if anything goes wrong.

## When something fails

| Symptom | Cause and fix |
| --- | --- |
| "Access blocked" or "app has not completed verification" at sign-in | The address is not on the Test users list. Add it (step 1) and retry. |
| App says the Google client file is missing | `google-client.json` is not in `%LOCALAPPDATA%\ContactManage`, or has the wrong name. |
| "Please sign in again" after a few days | The weekly expiry. Press **Add Google account**. |
| SmartScreen blocks the installer | **More info**, then **Run anyway**. |
| First screen does not appear | The choice was remembered. Use **Switch source** in the main window, or delete `%LOCALAPPDATA%\ContactManage\settings.json`. |

## Taking someone off

Remove the address from **Test users** in the Google console. Google should then refuse new sign-ins for it (not tested). They can also remove the app at https://myaccount.google.com/permissions and uninstall it from Settings, Apps. Their backups and logs stay in `%LOCALAPPDATA%\ContactManage` on their PC until they delete them.

See also `GMAIL.md` ("Gmail without a mail program") for the reasons behind these steps, and `QUICKSTART.md` for the general install.
