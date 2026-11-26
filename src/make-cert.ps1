param(
    [string]$Subject = "CN=PortableBuilder",
    [string]$OutPfx = "portablebuilder-test.pfx",
    [string]$Password = ""
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrEmpty($Password)) {
    $Password = [Guid]::NewGuid().ToString("N").Substring(0, 20)
    Write-Host ("[i] Generated certificate password: {0}" -f $Password)
}

$cert = New-SelfSignedCertificate `
    -Type CodeSigningCert `
    -Subject $Subject `
    -CertStoreLocation "Cert:\CurrentUser\My" `
    -KeyExportPolicy Exportable `
    -NotAfter (Get-Date).AddYears(3)

$secure = ConvertTo-SecureString -String $Password -Force -AsPlainText
Export-PfxCertificate -Cert $cert -FilePath $OutPfx -Password $secure | Out-Null

Write-Host ("[OK] Self-signed certificate exported to {0} (password: {1})" -f $OutPfx, $Password)
Write-Host "[i] Thumbprint: $($cert.Thumbprint)"
Write-Host ""
Write-Host "NOTE: a self-signed certificate does NOT remove SmartScreen warnings on"
Write-Host "other PCs. For distribution, buy an OV/EV code-signing certificate and run:"
Write-Host "  sign.cmd your-cert.pfx your-password"
