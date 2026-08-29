<#
    Authenticode-sign a built PowerDial binary.

        .\scripts\sign.ps1 "$env:USERPROFILE\Desktop\PowerDial.exe"

    Picks the first code-signing certificate in the current user's personal store, or use
    -Thumbprint to name one. Uses Set-AuthenticodeSignature rather than signtool.exe so no
    Windows SDK install is needed - it is the same Authenticode API underneath.

    WHAT A SIGNATURE DOES AND DOES NOT DO
    A signature proves the file has not been altered since it was signed, and names a
    publisher. Whether Windows trusts that publisher is a separate question:

      self-signed (what is set up now) - nobody else's machine trusts it, so SmartScreen
          still warns exactly as it did unsigned. It gives tamper-evidence and a stable
          identity, and it proves this pipeline works. It does not smooth the install.

      CA-issued (DigiCert, Sectigo, SSL.com and others) - trusted everywhere out of the
          box. An OV certificate still accumulates SmartScreen reputation over the first
          downloads; an EV certificate is trusted immediately. Since June 2023 the private
          key must live on a hardware token or in a cloud HSM, so signing needs that
          plugged in or configured. Nothing else here changes: point -Thumbprint at it.

    Timestamping is not optional. Without it the signature dies the day the certificate
    expires; with it, it stays valid for binaries signed while the certificate was live.
#>
param(
    [Parameter(Mandatory = $true)][string]$Path,
    [string]$Thumbprint,
    [string]$TimestampServer = "http://timestamp.digicert.com"
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path $Path)) { throw "No such file: $Path" }

if ($Thumbprint) {
    $cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Thumbprint -eq $Thumbprint }
    if (-not $cert) { throw "No certificate with thumbprint $Thumbprint in CurrentUser\My" }
} else {
    $cert = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert | Select-Object -First 1
    if (-not $cert) { throw "No code-signing certificate in CurrentUser\My. See the notes at the top of this script." }
}

Write-Output "signing : $Path"
Write-Output "with    : $($cert.Subject)  ($($cert.Thumbprint))"

$r = Set-AuthenticodeSignature -FilePath $Path -Certificate $cert -TimestampServer $TimestampServer -HashAlgorithm SHA256

Write-Output "status  : $($r.Status)"
if ($r.Status -ne "Valid" -and $r.Status -ne "UnknownError") { throw "Signing failed: $($r.StatusMessage)" }
if ($r.Status -eq "UnknownError") {
    # Expected for a self-signed certificate: the file IS signed, but this machine does not
    # trust the issuer, so verification reports the chain as untrusted rather than the
    # signature as bad. Say so plainly instead of pretending it passed.
    Write-Output "note    : signed, but the issuer is not trusted on this machine - expected while self-signed."
}
