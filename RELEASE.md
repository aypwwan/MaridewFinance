# Release process

Everything a release needs to know, in one place. The pipeline is
`.github/workflows/release.yml`; it runs on a version tag
(`git push origin v1.2.3`) or a manual dispatch with the version as input.

## 1. Version markers (ALL must match the tag)

The release workflow refuses to run when any of these disagree with the tag:

| Where | What |
|---|---|
| `MaridewFinance.App/MaridewFinance.App.csproj` | `<Version>1.2.3</Version>` |
| `MaridewFinance.App/UpdateService.cs` | `public const string CurrentVersion = "1.2.3";` |
| `MaridewFinance.App/wwwroot/web-bridge.js` | `var WEB_VERSION = '1.2.3';` |
| `MaridewFinance.Android/MaridewFinance.Android.csproj` | `<ApplicationVersionName>1.2.3</ApplicationVersionName>` |

The Android `versionCode` is **derived from the tag**, never hand-edited:
`major*10000 + minor*100 + patch` (so `1.2.3` → `10203`). It must move
monotonically for installed apps to accept the update.

## 2. Steps

```
# update the four markers above, then:
git commit -am "Bump to 1.2.3"
git tag v1.2.3
git push origin main v1.2.3
```

The workflow builds and publishes:

- `MaridewFinanceSetup-1.2.3.exe` — Inno Setup installer (optionally
  Authenticode-signed when the `MARIDEW_SIGN_PFX_B64` / `MARIDEW_SIGN_PASSWORD`
  secrets exist)
- `MaridewFinance-1.2.3.apk` — signed Android build
- `latest.json` — the auto-update feed (version, installer URL, SHA-256,
  notes) attached to the GitHub Release

The desktop app polls `latest.json` under
`https://github.com/aypwwan/MaridewFinance/releases/latest/download/latest.json`,
verifies the installer's SHA-256 against the feed **and refuses to install
when the feed's hash is missing** — always build the feed with
`installer/publish-update.ps1`, which computes it.

## 3. Android signing

The repo commits `MaridewFinance.Android/maridew.keystore` deliberately: its
only job is that installs upgrade in place instead of forcing uninstall
(data loss). The passwords are accordingly not treated as secrets, but builds
can override everything via environment variables:

| Variable | Overrides |
|---|---|
| `MARIDEW_ANDROID_KEYSTORE` | keystore path (use a private keystore) |
| `MARIDEW_ANDROID_KEYPASS` | key password |
| `MARIDEW_ANDROID_STOREPASS` | store password |

If you ever switch to a new keystore, installed users must uninstall first —
avoid it. Keep a backup of the keystore outside the repo.

## 4. Sync worker

The worker at `sync-worker/` (deployed at
`https://maridew-sync.maridew.workers.dev`) is independent of releases.
Deploys are automated: the `Deploy sync worker` workflow (`.github/workflows/deploy-worker.yml`)
runs whenever `sync-worker/**` changes on `main` (or via *Run workflow*),
parse-checks the worker, deploys with wrangler, and verifies `/health` reports
the version parsed from `worker.js`. It requires two repository secrets:
`CLOUDFLARE_API_TOKEN` (the **Edit Cloudflare Workers** API-token template) and
`CLOUDFLARE_ACCOUNT_ID`. The protocol version lives in the worker's `/health`
(`v: 4`).

## 5. Pre-release checklist

- [ ] All four version markers updated
- [ ] `versionCode` formula sanity-check for the new tag
- [ ] Release notes written into the tag/commit message context
- [ ] Emulator smoke test: fresh install → sign-in → add entry → background sync tick
- [ ] Web edition deployed (automatic on push to `main`)
