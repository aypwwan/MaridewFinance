# Maridew Finance

A wealth-management dashboard available on **Windows (desktop app)**, in the
**browser (web edition)**, and on **Android (APK)** — all three share the same
dashboard UI and the same zero-knowledge cloud sync. All monetary values
display in **Kenyan Shillings (KSh / KES)**.

## Use it in your browser (no install)

**[Launch the web edition](https://aypwwan.github.io/MaridewFinance/)** — the exact same dashboard,
running entirely client-side. Create an account and it works from any device: data is encrypted
in your browser (AES-GCM, PBKDF2-derived key) before it's synced, so the server stores only
ciphertext it cannot read. Use **Settings → Export** for a portable JSON backup.

## Install on Android (APK)

1. Download `MaridewFinance-<version>.apk` from
   **[Releases](https://github.com/aypwwan/MaridewFinance/releases/latest)**.
2. Open it on your phone (Android 7.0+); allow *Install unknown apps* for your
   browser/file manager when prompted (normal for apps outside the Play Store).
3. That's it — the full dashboard runs on-device with the same accounts and
   encrypted cloud sync as the web edition, so your data appears on both.

The app is a thin native wrapper: the shared dashboard is bundled inside the
APK and served from a private `https://` origin (AndroidX WebViewAssetLoader),
with storage in the WebView's IndexedDB on the device. Every release is signed
with the same stable key, so updates install in place without losing data.

**Background sync:** a quiet foreground service keeps the app synced even when
it is closed — in both directions: entries added on another device arrive
automatically (about every 5 minutes) and raise a "New entries" notification,
while anything added on the phone while it was closed is pushed to the cloud on
the same timer. Idle ticks don't touch the cloud (the push is skipped unless
local data actually differs from the server's copy), so other devices aren't
spammed with updates. Successful uploads just refresh the "last synced" time in
the persistent notification; it restarts after reboot. You can stop the service
any time from Android's *Settings → Apps → Maridew Finance → Stop*.

## Download & install (end users)

No build tools needed:

1. Download the latest installer from
   **[Releases](https://github.com/aypwwan/MaridewFinance/releases/latest)** —
   `MaridewFinanceSetup-<version>.exe`.
2. Run it. It installs into Program Files, creates a Start-menu shortcut, and
   bootstraps the .NET 8 / WebView2 runtimes if they are missing.
3. That's it. The app **updates itself**: it checks the release feed, verifies
   the installer's SHA-256, installs silently after a UAC prompt, and
   relaunches. You can also check manually in *Settings → Software Updates*.

> **SmartScreen note:** the installer is signed with Maridew's own certificate
> authority, so machines that have not trusted that CA yet may show an
> "unknown publisher" warning. To trust it, run
> `installer\signing\trust-ca.ps1` from an elevated prompt, or click
> *More info → Run anyway*.

## What this is

- A **WPF (.NET 8) desktop application** that opens directly in Visual Studio.
- The dashboard UI (dashboard, transactions, budgets, loans, savings goals,
  reports) is the same HTML/CSS/JS front-end from the original mockup, now
  hosted inside the app via the **Microsoft Edge WebView2** control — so it
  looks and behaves identically, but runs as a native `.exe` you can double
  click, no browser required.
- All currency labels, formatting, and chart axes were converted from `$` to
  `KSh` (e.g. `KSh 142,650.00`), and number formatting uses the `en-KE` locale.

## Requirements

1. **Visual Studio 2022** (17.8 or later) with the **.NET desktop development**
   workload installed.
2. **.NET 8 SDK** (Visual Studio installer can add this automatically).
3. **WebView2 Runtime** — pre-installed on Windows 10/11 by default. If it's
   missing, Visual Studio/NuGet will still build fine; the runtime is only
   needed to *run* the app, and Windows will prompt you to install it from
   https://developer.microsoft.com/microsoft-edge/webview2/ if absent.

## How to open and run

1. Unzip this project folder anywhere on your machine.
2. Double-click **`MaridewFinance.sln`** — this opens the solution in
   Visual Studio.
3. Visual Studio will automatically restore the NuGet package
   (`Microsoft.Web.WebView2`) on first load. If it doesn't, right-click the
   solution in Solution Explorer → **Restore NuGet Packages**.
4. Press **F5** (or the green ▶ "Start" button) to build and run.
5. The Maridew Finance window opens and loads the dashboard.

## Project structure

```
MaridewFinance/
├── MaridewFinance.sln
├── README.md
├── .gitignore
├── installer/                       # Windows installer (Inno Setup)
│   ├── build.ps1                    # One-command publish + build + sign
│   ├── setup.iss                    # Inno Setup script (shortcuts, uninstall)
│   ├── MicrosoftEdgeWebview2Setup.exe  # WebView2 runtime bootstrapper (bundled)
│   ├── signing/                     # Code-signing tooling
│   │   ├── generate-selfsigned-cert.ps1  # Local CA + signing cert (internal use)
│   │   └── trust-ca.ps1                   # Trust the local CA on a PC
│   └── dist/                        # Output: MaridewFinanceSetup-<version>.exe
├── MaridewFinance.Android/          # Android APK wrapper around the same wwwroot
│   ├── MaridewFinance.Android.csproj
│   ├── MainActivity.cs              # WebView + WebViewAssetLoader host
│   ├── AndroidManifest.xml
│   ├── maridew.keystore             # stable signing key (also used by CI)
│   └── Resources/                   # launcher icons + theme
└── MaridewFinance.App/
    ├── MaridewFinance.App.csproj     # WPF project, .NET 8, WebView2 reference
    ├── app.manifest                  # High-DPI / asInvoker manifest
    ├── App.xaml / App.xaml.cs
    ├── MainWindow.xaml               # Hosts the WebView2 control
    ├── MainWindow.xaml.cs            # Loads wwwroot/index.html into WebView2
    └── wwwroot/
        └── index.html                # The dashboard UI (KES currency)
```

## Installing on Windows (installer)

A classic Windows `setup.exe` installer is included so the app installs with
shortcuts and uninstalls cleanly — no Visual Studio needed on the target PC.

**Build it** (requires the .NET SDK and Inno Setup 6, https://jrsoftware.org/isdl.php):

```
powershell -ExecutionPolicy Bypass -File installer\build.ps1
```

This publishes the app (Release, win-x64, self-contained .NET runtime) and
produces `installer\dist\MaridewFinanceSetup-<version>.exe`.

**What the installer does**

- Installs to `C:\Program Files\Maridew Finance` (per-machine, admin).
- Creates a **Start-menu shortcut** (and optional desktop shortcut) and an
  entry in **Settings → Apps** for uninstalling.
- Bundles the **WebView2 runtime** bootstrapper and installs it silently if
the dashboard runtime is missing (needs internet once).
- Checks for the **.NET 8 Desktop Runtime**; if absent, offers to open the
  official download page during install.
- Offers to launch the app when setup finishes.

**Uninstalling** is clean: **Settings → Apps → Maridew Finance → Uninstall**
removes the program files and shortcuts. Your data (the SQLite database and
backups in `%LOCALAPPDATA%\MaridewFinance`) is **kept** — uninstall never
deletes financial data.

## Code signing (removing the SmartScreen warning)

Windows shows an "unknown publisher" SmartScreen warning for any app that is
not signed by a **publicly trusted** certificate. The signing pipeline is
already wired into `installer\build.ps1` (signs both `MaridewFinance.exe` and
the setup `.exe`, with a DigiCert timestamp so signatures stay valid after
the certificate expires).

**For the general public — buy a code-signing certificate** from a CA such as
DigiCert, Sectigo or SSL.com (an OV cert, plus a few weeks of downloads for
reputation, fully removes the warning; an EV cert gets reputation instantly).
Then either:

```
set MARIDEW_SIGN_PFX=C:\path\to\your.pfx
set MARIDEW_SIGN_PASSWORD=your-password
powershell -ExecutionPolicy Bypass -File installer\build.ps1
```

or drop the `.pfx` and a password file at
`installer\signing\private\maridew-finance.pfx` / `maridew-finance.pfx.txt`.
Export the `.pfx` (with its chain) from the CA portal. If your certificate
lives on a hardware token or cloud HSM, sign with signtool's `/certs` options
or the CA's cloud-signing service instead and adjust `build.ps1` accordingly
— the script currently takes a `.pfx` file.

**For internal rollout (free)** — sign with a locally generated CA and trust
that CA on each PC:

```
powershell -ExecutionPolicy Bypass -File installer\signing\generate-selfsigned-cert.ps1
# on every machine that should trust the builds (admin rights needed):
powershell -ExecutionPolicy Bypass -File installer\signing\trust-ca.ps1
powershell -ExecutionPolicy Bypass -File installer\build.ps1
```

After trusting the CA, Windows treats the signed app and installer as
trusted on that PC — no SmartScreen warning. Keys are generated under
`installer\signing\private\` (gitignored — never commit them). There is no
way to remove SmartScreen for strangers without a publicly trusted
certificate.

## Auto-update

The app checks a JSON **update feed** for newer versions (on startup and via
**Settings → Software Updates**), downloads the signed installer, verifies
its SHA-256 checksum, and runs it silently — Windows asks for permission via
UAC, the installer upgrades in place, and the app relaunches. Your data
(`%LOCALAPPDATA%\MaridewFinance`) is never touched by an update.

**Host the feed** anywhere that serves HTTPS (GitHub Releases, a static
host, a CDN). It must be a JSON document like this:

```json
{
  "version": "1.0.1",
  "url": "https://example.com/downloads/MaridewFinanceSetup-1.0.1.exe",
  "sha256": "<hex sha256 of that installer>",
  "notes": "What's new in this version"
}
```

Compute the checksum with:
`powershell -Command "(Get-FileHash MaridewFinanceSetup-1.0.1.exe -Algorithm SHA256).Hash"`

**Where the feed URL comes from:** the default is the `DefaultFeedUrl`
constant in `MaridewFinance.App\UpdateService.cs`. To point an install at a
different feed (e.g. your own domain) without rebuilding, drop a file named
`update-feed-url.txt` containing just the URL into the app data folder
(`%LOCALAPPDATA%\MaridewFinance\`).

Notes:
- The installed version comes from the `<Version>` in the csproj — keep
  `UpdateService.CurrentVersion` in sync with it (both are `1.0.0` today).
- Only newer dotted versions install; equal or older feeds are ignored.
- The feed is fetched and the installer downloaded by PowerShell
  (`Invoke-WebRequest`), which ships with every supported Windows version.
- The installer itself is the same Inno Setup artifact produced by
  `installer\build.ps1` — build it, host it, point the feed at it.

## Notes

- The dashboard's charts (Chart.js), icons (Lucide), styling (Tailwind), and
  the "Plus Jakarta Sans" font are bundled locally under
  `wwwroot/vendor/`, so the app renders fully offline — no internet needed.
- **Accounts:** the app opens with a sign-in / create-account window. Accounts
  are stored locally in the same SQLite database — passwords are hashed with
  PBKDF2 (SHA-256, 100k iterations, per-user salt), never stored in plain
  text. Every person's transactions, budgets, loans and savings goals are
  private to their account on this machine. Sign out from **Settings &
  Backups → Account**, or just close the window, to return to the login
  screen. No data leaves the machine and there is no server.
- All data (transactions, budgets, loans, savings goals) is stored in a local
  SQLite database at `%LOCALAPPDATA%\MaridewFinance\maridew.db` and survives
  app restarts. Copying that file is a full backup (note: a database file
  contains *all* accounts, so treat exports as sensitive).
- **Automatic backups:** on each launch (and on date rollover during long
  sessions) the app snapshots the database with SQLite's `VACUUM INTO` into
  `%LOCALAPPDATA%\MaridewFinance\backups\maridew-YYYY-MM-DD.db` — one per
  day, with only the newest 7 kept. Restore from the **Settings & Backups**
  tab in the app, or by copying a backup over `maridew.db` while the app is
  closed.
- **Move data between machines:** use **Settings & Backups → Move Data** to
  export the whole database to a `.db` file and import it on another machine.
  Imports are validated (must be a SQLite database containing the Maridew
  Finance tables) and a safety snapshot of the current data is taken first.
- To change the app name/window title, edit the `Title` property in
  `MainWindow.xaml` and the `<title>` tag in `wwwroot/index.html`.
