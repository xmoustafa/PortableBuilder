param(
    [string]$Pfx,
    [string]$Password,
    [string]$Timestamp = "http://timestamp.digicert.com"
)

$ErrorActionPreference = "Stop"

if (-not $Pfx -or -not $Password) {
    Write-Host "Usage: sign.cmd <certificate.pfx> <password> [timestamp-url]"
    Write-Host "  Or:  powershell -File sign.ps1 -Pfx cert.pfx -Password pass"
    exit 1
}
if (-not (Test-Path $Pfx)) {
    Write-Host ("[X] PFX not found: {0}" -f $Pfx)
    exit 1
}

$signtool = Get-ChildItem "C:\Program Files (x86)\Windows Kits\10\bin" -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -match '\\x64\\' } | Select-Object -First 1
if (-not $signtool) {
    $cmd = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($cmd) { $signtool = Get-Item $cmd.Source }
}
if (-not $signtool) {
    Write-Host "[X] signtool.exe not found. Install the Windows SDK."
    exit 1
}

$root = Split-Path $PSScriptRoot -Parent
$targets = @(
    (Join-Path $root 'PortableBuilder.exe')
)

foreach ($target in $targets) {
    if (-not (Test-Path $target)) { continue }
    & $signtool.FullName sign /fd SHA256 /f $Pfx /p $Password /tr $Timestamp /td SHA256 $target
    if ($LASTEXITCODE -ne 0) {
        Write-Host ("[X] Signing failed: {0}" -f $target)
        exit 1
    }
    Write-Host ("[OK] Signed {0}" -f (Split-Path $target -Leaf))
}
