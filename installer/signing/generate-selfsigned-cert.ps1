# ============================================================================
#  generate-selfsigned-cert.ps1 - local CA + code-signing certificate
# ----------------------------------------------------------------------------
#  Creates a private CA and a code-signing certificate for Maridew Finance,
#  then exports a .pfx that signtool can use to sign the app and installer.
#
#  What this is for:
#    * Internal rollouts - install the CA on each PC (trust-ca.ps1) and
#      Windows treats signed files as trusted: no SmartScreen warning.
#    * Testing the signing pipeline before you buy a public certificate.
#
#  It does NOT remove SmartScreen for the general public - for that you need
#  a publicly trusted code-signing certificate from a CA (see README).
#
#  Usage:
#    powershell -ExecutionPolicy Bypass -File installer\signing\generate-selfsigned-cert.ps1
#    powershell ... -File installer\signing\generate-selfsigned-cert.ps1 -Password "change-me"
# ============================================================================

param(
    [string]$Password = "maridew-local-signing"
)

$ErrorActionPreference = 'Stop'

$openssl = (Get-Command openssl -ErrorAction SilentlyContinue).Source
if (-not $openssl) {
    throw "openssl was not found on PATH (it ships with Git for Windows)."
}

$outDir = Join-Path $PSScriptRoot 'private'
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

$caKey  = Join-Path $outDir 'ca.key'
$caCrt  = Join-Path $outDir 'ca.crt'
$csKey  = Join-Path $outDir 'codesign.key'
$csCsr  = Join-Path $outDir 'codesign.csr'
$csCrt  = Join-Path $outDir 'codesign.crt'
$pfx    = Join-Path $outDir 'maridew-finance.pfx'
$pwFile = Join-Path $outDir 'maridew-finance.pfx.txt'

Write-Host "==> Creating local CA (Maridew Finance Local CA)" -ForegroundColor Cyan
& $openssl req -x509 -newkey rsa:3072 -sha256 -days 3650 -nodes `
    -keyout $caKey -out $caCrt `
    -subj "/C=KE/O=Maridew/CN=Maridew Finance Local CA" `
    -addext "basicConstraints=critical,CA:TRUE" `
    -addext "keyUsage=critical,keyCertSign,cRLSign" `
    -addext "subjectKeyIdentifier=hash"
if ($LASTEXITCODE -ne 0) { throw "openssl failed creating the CA." }

Write-Host "==> Creating code-signing certificate (Maridew Finance)" -ForegroundColor Cyan
& $openssl req -newkey rsa:3072 -sha256 -nodes `
    -keyout $csKey -out $csCsr `
    -subj "/C=KE/O=Maridew/CN=Maridew Finance" `
    -addext "keyUsage=critical,digitalSignature" `
    -addext "extendedKeyUsage=critical,codeSigning"
if ($LASTEXITCODE -ne 0) { throw "openssl failed creating the code-signing key." }

$extFile = Join-Path $outDir 'codesign.ext'
Set-Content -Path $extFile -Value @"
keyUsage=critical,digitalSignature
basicConstraints=critical,CA:FALSE
extendedKeyUsage=critical,codeSigning
"@ -NoNewline

& $openssl x509 -req -sha256 -days 3650 `
    -in $csCsr -CA $caCrt -CAkey $caKey -CAcreateserial `
    -out $csCrt -extfile $extFile
if ($LASTEXITCODE -ne 0) { throw "openssl failed issuing the code-signing certificate." }

Write-Host "==> Exporting PKCS#12 (.pfx) for signtool" -ForegroundColor Cyan
& $openssl pkcs12 -export `
    -inkey $csKey -in $csCrt -certfile $caCrt `
    -out $pfx -passout "pass:$Password" `
    -name "Maridew Finance"
if ($LASTEXITCODE -ne 0) { throw "openssl failed exporting the .pfx." }

# Remember the password for build.ps1 (file is gitignored).
Set-Content -Path $pwFile -Value $Password -NoNewline

Write-Host ""
Write-Host "Done." -ForegroundColor Green
Write-Host "  Certificate : $csCrt"
Write-Host "  Signing key : $pfx  (password stored in $pwFile)"
Write-Host ""
Write-Host "Next steps:"
Write-Host "  1. On each PC that should trust these builds, run:"
Write-Host "       powershell -ExecutionPolicy Bypass -File installer\signing\trust-ca.ps1"
Write-Host "     (or install private\ca.crt as a Trusted Root with 'Code Signing' trust)"
Write-Host "  2. Rebuild the installer:"
Write-Host "       powershell -ExecutionPolicy Bypass -File installer\build.ps1"