# Publishes a Maridew Finance release to GitHub Releases, making auto-updates
# work for every installed copy of the app.
#
# What it does:
#   1. Takes the signed installer from installer\dist (or -SetupExe).
#   2. Computes its SHA-256 checksum.
#   3. Generates latest.json in the exact schema the app's UpdateService reads:
#        { "version", "url", "sha256", "notes" }
#   4. Creates the GitHub Release (tag v<version>) and uploads the installer
#      and latest.json as release assets.
#
# Afterwards, every install that has this repo configured in UpdateService
# sees the new version via the stable URL:
#   https://github.com/<owner>/<repo>/releases/latest/download/latest.json
#
# Usage:
#   $env:GITHUB_TOKEN = "<a PAT with repo Contents: read/write>"
#   powershell -File publish-update.ps1 -Repo "your-name/MaridewFinance" -Version "1.0.1" `
#       -Notes "Fixes and improvements."
#
#   Dry run (no network, prints the feed + hash):
#   powershell -File publish-update.ps1 -Repo "your-name/MaridewFinance" -Version "1.0.1" -DryRun
#
param(
    [Parameter(Mandatory = $true)] [string]$Repo,        # owner/name
    [Parameter(Mandatory = $true)] [string]$Version,     # e.g. 1.0.1
    [string]$SetupExe = "",                              # defaults to dist\MaridewFinanceSetup-<Version>.exe
    [string]$ApkPath = "",                               # optional Android APK to attach to the same release
    [string]$Notes = "",
    [string]$Token = $env:GITHUB_TOKEN,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'

if ($SetupExe -eq "") {
    $here = Split-Path -Parent $MyInvocation.MyCommand.Path
    $SetupExe = Join-Path $here "dist\MaridewFinanceSetup-$Version.exe"
}
if (-not (Test-Path $SetupExe)) {
    throw "Installer not found: $SetupExe - run installer\build.ps1 -Version $Version first."
}

# --- 1. Checksum ------------------------------------------------------------
$sha256 = (Get-FileHash -Path $SetupExe -Algorithm SHA256).Hash.ToLower()
$size = (Get-Item $SetupExe).Length

# --- 2. Feed document (schema used by UpdateService.JsonField) ---------------
# The app reads this with a deliberately tiny JSON scanner that stops at the
# first raw quote and does not decode backslash escapes, so the document is
# built directly as clean ASCII: quotes and line breaks in notes are removed
# rather than escaped.
$Notes = (($Notes -replace '"', "'") -replace '[\r\n\t\\]', ' ').Trim()
$feedUrl = "https://github.com/$Repo/releases/download/v$Version/$(Split-Path -Leaf $SetupExe)"
$json = '{"version": "' + $Version.Trim() + '", "url": "' + $feedUrl + '", "sha256": "' + $sha256 + '", "notes": "' + $Notes + '"}'

Write-Host "Installer : $SetupExe  ($([math]::Round($size/1MB,1)) MB)"
Write-Host "SHA-256   : $sha256"
Write-Host "Feed      :"
Write-Host $json

if ($DryRun) {
    Write-Host "`nDry run - nothing was uploaded." -ForegroundColor Cyan
    exit 0
}

if ($Token -eq "") {
    throw "No GitHub token. Set `$env:GITHUB_TOKEN (a PAT with Contents read/write for this repo)."
}

$api = "https://api.github.com"
$headers = @{
    Authorization = "Bearer $Token"
    Accept        = "application/vnd.github+json"
    "X-GitHub-Api-Version" = "2022-11-28"
    "User-Agent"  = "maridew-finance-release"
}

# --- 3. Create (or reuse) the release ---------------------------------------
$tag = "v$Version"
$tagUrl = "$api/repos/$Repo/releases/tags/$tag"
try {
    $release = Invoke-RestMethod -Method Get -Uri $tagUrl -Headers $headers
    Write-Host "Release $tag already exists - reusing it (id $($release.id))."
} catch {
    $body = @{
        tag_name   = $tag
        name       = "Maridew Finance $Version"
        body       = $Notes
        draft      = $false
        prerelease = $false
    } | ConvertTo-Json
    # (target_commitish intentionally omitted: GitHub rejects a JSON null for
    # it, and omitting it makes the tag point at the default branch.)
    $release = Invoke-RestMethod -Method Post -Uri "$api/repos/$Repo/releases" -Headers $headers -Body $body -ContentType "application/json"
    Write-Host "Created release $tag (id $($release.id))."
}

# --- 4. Upload assets (delete + re-upload for idempotency) ------------------
# Uploads go through curl.exe, not Invoke-RestMethod: GitHub's upload endpoint
# rejects PowerShell's raw-body request style with "Multipart form data
# required", and curl with --data-binary (a real Content-Length) is what the
# endpoint accepts. curl.exe is preinstalled on Windows 10/11 and CI runners;
# spell it with .exe - bare `curl` in PowerShell aliases Invoke-WebRequest.
$assets = @(@{ Path = $SetupExe; Name = (Split-Path -Leaf $SetupExe) },
            @{ Path = $null;    Name = "latest.json"; Content = $json })

if ($ApkPath -ne "") {
    if (-not (Test-Path $ApkPath)) {
        throw "ApkPath given but file not found: $ApkPath"
    }
    $apkName = "MaridewFinance-$Version.apk"
    $assets += @{ Path = $ApkPath; Name = $apkName }
    Write-Host "APK       : $ApkPath (uploaded as $apkName)"
}

$uploadBase = ($release.upload_url -split '\{')[0]   # strip the {?name,label} URI template

foreach ($a in $assets) {
    # Remove an existing asset with the same name.
    foreach ($existing in @($release.assets)) {
        if ($existing.name -eq $a.Name) {
            Invoke-RestMethod -Method Delete -Uri "$api/repos/$Repo/releases/assets/$($existing.id)" -Headers $headers | Out-Null
            Write-Host "Replaced existing asset: $($a.Name)"
        }
    }

    $tmpBody = $null
    if ($null -ne $a.Content) {
        $tmpBody = Join-Path ([IO.Path]::GetTempPath()) ("asset-" + [guid]::NewGuid().ToString('N') + ".json")
        [IO.File]::WriteAllBytes($tmpBody, [System.Text.Encoding]::UTF8.GetBytes($a.Content))
        $bodyPath = $tmpBody
    } else {
        $bodyPath = $a.Path
    }

    $name = [uri]::EscapeDataString($a.Name)
    $tmpOut = Join-Path ([IO.Path]::GetTempPath()) ("asset-resp-" + [guid]::NewGuid().ToString('N') + ".json")
    & curl.exe -sS --fail-with-body `
        -H "Authorization: Bearer $Token" -H "User-Agent: maridew-finance-release" `
        -H "Content-Type: application/octet-stream" `
        --data-binary "@$bodyPath" -o "$tmpOut" `
        "$uploadBase`?name=$name"
    if ($tmpBody) { Remove-Item $tmpBody -Force -ErrorAction SilentlyContinue }
    if ($LASTEXITCODE -ne 0) { throw "curl upload failed for $($a.Name) (exit $LASTEXITCODE)" }

    $uploaded = Get-Content $tmpOut -Raw | ConvertFrom-Json
    Remove-Item $tmpOut -Force -ErrorAction SilentlyContinue
    if ($uploaded.state -ne "uploaded") { throw "upload of $($a.Name) did not complete: $($uploaded | ConvertTo-Json -Compress)" }
    Write-Host ("Uploaded   : {0}  ({1} bytes)" -f $uploaded.name, $uploaded.size)
}

Write-Host ""
Write-Host "Done. Installs configured with repo '$Repo' now see version $Version." -ForegroundColor Green
Write-Host "Feed URL (baked into the app): https://github.com/$Repo/releases/latest/download/latest.json"
