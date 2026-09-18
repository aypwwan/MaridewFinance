# ============================================================================
#  build.ps1 - one-command Windows installer build for Maridew Finance
# ----------------------------------------------------------------------------
#  1. Publishes the app (Release, win-x64, self-contained .NET runtime)
#  2. Signs the app .exe (if a signing certificate is configured)
#  3. Compiles the Inno Setup installer into installer\dist\
#  4. Signs the installer .exe (same certificate)
#
#  Requirements: .NET 8 SDK (dotnet on PATH) and Inno Setup 6
#  (https://jrsoftware.org/isdl.php or `winget install JRSoftware.InnoSetup`).
#
#  Code signing - build.ps1 looks for a certificate in this order:
#    1. $env:MARIDEW_SIGN_PFX  (path to a .pfx) + $env:MARIDEW_SIGN_PASSWORD
#    2. installer\signing\private\maridew-finance.pfx (+ .pfx.txt password),
#       created by installer\signing\generate-selfsigned-cert.ps1
#    With no certificate found, the build still succeeds but unsigned.
#    Signing needs signtool (Windows SDK) - auto-detected.
#
#  Usage:
#    powershell -ExecutionPolicy Bypass -File installer\build.ps1
#    powershell -ExecutionPolicy Bypass -File installer\build.ps1 -Version 1.1.0
# ============================================================================

param(
    [string]$Version = "1.0.0",
    [string]$Configuration = "Release"
)

$ErrorActionPreference = 'Stop'

$repoRoot    = Split-Path -Parent $PSScriptRoot            # MaridewFinance\
$appDir      = Join-Path $repoRoot 'MaridewFinance.App'
$publishDir  = Join-Path $repoRoot 'publish-installer'
$distDir     = Join-Path $PSScriptRoot 'dist'

# --- Locate Inno Setup 6 ------------------------------------------------
$iscc = @(
    Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'
    Join-Path $env:ProgramFiles       'Inno Setup 6\ISCC.exe'
    'ISCC.exe'
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $iscc) {
    throw "Inno Setup 6 was not found. Install it from https://jrsoftware.org/isdl.php (or: winget install JRSoftware.InnoSetup) and run this script again."
}

# --- Locate signtool (Windows SDK) --------------------------------------
# Prefer the x64 build of the newest installed SDK (arm64/x86 builds won't run).
$signtool = ''
$found = @()
foreach ($sdkBin in @("C:\Program Files (x86)\Windows Kits\10\bin", "C:\Program Files\Windows Kits\10\bin")) {
    $found += Get-ChildItem $sdkBin -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue
}
$found = $found | Where-Object { $_.FullName -match '\\x64\\' } | Sort-Object FullName -Descending
if ($found) { $signtool = $found[0].FullName }

# --- Resolve signing certificate -----------------------------------------
$signPfx  = $env:MARIDEW_SIGN_PFX
$signPass = $env:MARIDEW_SIGN_PASSWORD
if (-not $signPfx) {
    $defaultPfx = Join-Path $PSScriptRoot 'signing\private\maridew-finance.pfx'
    if (Test-Path $defaultPfx) {
        $signPfx = $defaultPfx
        $pwFile = Join-Path $PSScriptRoot 'signing\private\maridew-finance.pfx.txt'
        if (-not $signPass -and (Test-Path $pwFile)) { $signPass = (Get-Content $pwFile -Raw).Trim() }
    }
}

function Sign-File([string]$File) {
    if (-not $signPfx) { return }
    if (-not $signtool) {
        Write-Host "  (warn: signtool not found - $File left unsigned)" -ForegroundColor Yellow
        return
    }
    Write-Host "  Signing: $File"
    $args = @('sign', '/f', $signPfx)
    if ($signPass) { $args += @('/p', $signPass) }
    $args += @('/fd', 'sha256', '/tr', 'http://timestamp.digicert.com', '/td', 'sha256', '/v', $File)
    & $signtool @args
    if ($LASTEXITCODE -ne 0) {
        # Timestamp server unreachable - retry without a timestamp (signature
        # then expires with the certificate instead of being long-lived).
        Write-Host "  (warn: timestamping failed - signing without a timestamp)" -ForegroundColor Yellow
        $args = @('sign', '/f', $signPfx)
        if ($signPass) { $args += @('/p', $signPass) }
        $args += @('/fd', 'sha256', $File)
        & $signtool @args
        if ($LASTEXITCODE -ne 0) { throw "signtool failed for $File" }
    }
}

# --- 1/3: Publish the app ----------------------------------------------
Write-Host "==> Publishing app ($Configuration, win-x64, self-contained)" -ForegroundColor Cyan
if (Test-Path $publishDir) {
    # A plain Remove-Item can fail with Access Denied when a directory ACL
    # denies child deletion even though nothing holds the files. Rename the
    # folder aside instead (rename needs no child Delete), then remove it
    # best-effort so a stale copy never lingers.
    $stale = "$publishDir.old-$([guid]::NewGuid().ToString('N').Substring(0, 8))"
    Move-Item $publishDir $stale -Force
    try { Remove-Item $stale -Recurse -Force } catch { Write-Host "Note: could not delete '$stale' (locked or ACL-restricted); it can be removed manually." -ForegroundColor Yellow }
}
dotnet publish $appDir -c $Configuration -r win-x64 --self-contained -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }

# --- 2/3: Sign the app executable (before it is bundled) ---------------
Write-Host "==> Signing app executable" -ForegroundColor Cyan
Sign-File (Join-Path $publishDir 'MaridewFinance.exe')

# --- 3/3: Compile + sign the installer ----------------------------------
Write-Host "==> Compiling installer with Inno Setup" -ForegroundColor Cyan
New-Item -ItemType Directory -Force -Path $distDir | Out-Null
& $iscc "/DAppVersion=$Version" "/DPublishDir=$publishDir" (Join-Path $PSScriptRoot 'setup.iss')
if ($LASTEXITCODE -ne 0) { throw "ISCC.exe failed with exit code $LASTEXITCODE." }

$exe = Join-Path $distDir "MaridewFinanceSetup-$Version.exe"
Write-Host "==> Signing installer" -ForegroundColor Cyan
Sign-File $exe

$mb = [math]::Round((Get-Item $exe).Length / 1MB, 1)
Write-Host ""
Write-Host ("Installer ready: {0}  ({1} MB)" -f $exe, $mb) -ForegroundColor Green
if ($signPfx) {
    Write-Host "Signed with: $signPfx" -ForegroundColor Green
} else {
    Write-Host "NOT SIGNED - configure a certificate to remove SmartScreen warnings (see README)." -ForegroundColor Yellow
}
Write-Host "Share that file, or run it locally to install Maridew Finance."