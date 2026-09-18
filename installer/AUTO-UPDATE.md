# Auto-Update Setup (GitHub Releases)

Maridew Finance can update itself: the app checks a small JSON feed, downloads
the signed installer, verifies its SHA-256 checksum, installs it silently, and
relaunches. This guide wires that feed to a GitHub repository you own.

## How it works

```
Installed app                 GitHub Releases (your repo)
-------------                 ---------------------------
fetch latest.json  ────────►  https://github.com/<owner>/<repo>/releases/latest/download/latest.json
download installer ────────►  <asset URL from the feed>
verify SHA-256            ──  matches "sha256" in the feed
install silently (/VERYSILENT) and relaunch
```

The feed URL is baked into the app (one constant in `UpdateService.cs`), so
every install checks your repository automatically - no per-machine setup.

## One-time setup (about 3 minutes)

1. **Create the repo.** On github.com create a repository, e.g.
   `your-name/MaridewFinance` (public or private - a fine-grained PAT works
   for private repos, but the release download URLs require the repo to be
   **public** unless every user has a token. Use a public repo.)

2. **Put the repo name in the app.** In
   `MaridewFinance.App/UpdateService.cs`, set:

   ```csharp
   private static readonly string GitHubRepo = "your-name/MaridewFinance";
   ```

   (It is `static readonly` rather than `const` on purpose: an empty `const`
   would let the compiler fold the feed-url check away and emit an
   unreachable-code warning. Leave it as `""` or use the `YOUR-USERNAME`
   placeholder and installs will simply report "No update feed is
   configured" - nothing breaks.)

3. **Create a personal access token** (classic, `repo` scope, or fine-grained
   with Contents read/write for this one repo) and keep it for publishing:
   github.com → Settings → Developer settings → Personal access tokens.

4. **Rebuild the installer** so the constant ships:

   ```powershell
   powershell -File installer\build.ps1
   ```

## Publishing an update (every release)

```powershell
# Build 1.0.1 (bump <Version> in MaridewFinance.App.csproj first,
# and UpdateService.CurrentVersion must match it):
powershell -File installer\build.ps1 -Version 1.0.1

# Publish it:
$env:GITHUB_TOKEN = "<your token>"
powershell -File installer\publish-update.ps1 -Repo "your-name/MaridewFinance" `
    -Version 1.0.1 -Notes "Transfer fees, faster reports."
```

The script creates the `v1.0.1` release (idempotent - re-running replaces the
assets), uploads the signed installer, and generates `latest.json` with the
correct version, download URL, and SHA-256. Installed copies pick it up on
their next check - from the UI, or on app start.

## Testing before shipping

Dry run (no network - prints the feed document and hash it would publish):

```powershell
powershell -File installer\publish-update.ps1 -Repo "your-name/MaridewFinance" -Version 1.0.1 -DryRun
```

To test the full flow on this machine without touching the baked-in constant,
point one install at any feed with the override file:

```powershell
"https://github.com/your-name/MaridewFinance/releases/latest/download/latest.json" |
    Set-Content "$env:LOCALAPPDATA\MaridewFinance\update-feed-url.txt"
```

Delete that file to fall back to the baked-in constant.

## Feed schema (what the app reads)

```json
{
  "version": "1.0.1",
  "url": "https://github.com/your-name/MaridewFinance/releases/download/v1.0.1/MaridewFinanceSetup-1.0.1.exe",
  "sha256": "<hex digest of the installer>",
  "notes": "What's new"
}
```

Notes must not contain double quotes (the app's dependency-free JSON reader
stops at the first one) - `publish-update.ps1` sanitizes this automatically.
