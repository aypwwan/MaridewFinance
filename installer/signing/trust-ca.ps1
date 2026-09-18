# ============================================================================
#  trust-ca.ps1 - trust the Maridew Finance local CA on this PC
# ----------------------------------------------------------------------------
#  Installs the local CA (created by generate-selfsigned-cert.ps1) into the
#  Windows root trust store, so files signed by it are trusted here and the
#  SmartScreen warning disappears for this machine.
#
#  Needs administrator rights (writes to the system trust store).
#
#  Usage:
#    powershell -ExecutionPolicy Bypass -File installer\signing\trust-ca.ps1
# ============================================================================

$ErrorActionPreference = 'Stop'

$caFile = Join-Path $PSScriptRoot 'private\ca.crt'
if (-not (Test-Path $caFile)) {
    throw "CA certificate not found at $caFile. Run generate-selfsigned-cert.ps1 first."
}

Write-Host "==> Installing Maridew Finance local CA into the Windows root trust store" -ForegroundColor Cyan
certutil -addstore -f "Root" $caFile
if ($LASTEXITCODE -ne 0) {
    throw "certutil failed. This script needs administrator rights - run it from an elevated prompt."
}

Write-Host ""
Write-Host "Done. Windows now trusts files signed by the Maridew Finance local CA." -ForegroundColor Green
Write-Host "Rebuild the installer (installer\build.ps1) and the SmartScreen warning will no longer appear on this PC."
Write-Host ""
Write-Host "Manual alternative: double-click private\ca.crt -> Certificate -> Install ->"
Write-Host "'Trusted Root Certification Authorities', and tick 'Code Signing' as a trusted purpose."