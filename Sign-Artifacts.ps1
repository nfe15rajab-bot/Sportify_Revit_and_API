<#
.SYNOPSIS
    Authenticode-signs the files Sportify ships (the add-in DLL, the API, the installer), and says plainly what happened.

.DESCRIPTION
    Dot-source it (". .\Sign-Artifacts.ps1") and call Sign-SportifyFiles, or run it directly:

        .\Sign-Artifacts.ps1 -Files dist\Sportify_Revit.exe -CertificateThumbprint 0123...   # a certificate in CurrentUser\My or LocalMachine\My
        .\Sign-Artifacts.ps1 -Files dist\payload\SportfyRevit.dll -PfxFile my.pfx -PfxPassword (Read-Host -AsSecureString)

    BuildDistribution.ps1 calls it when it is given a certificate. There is no certificate in this repository and there cannot be: a certificate is bought from a certificate
    authority (an "OV" or "EV" code-signing certificate) or issued by the organisation. Without one the build is UNSIGNED: Windows SmartScreen warns when the installer is run,
    and Revit asks the person to confirm the add-in ("Load Once / Always Load") because its publisher is unknown. A self-signed certificate signs (and proves the files were not
    changed after signing) but is not trusted anywhere except where it was installed as a trusted publisher, so it is for trying this script, not for shipping.

    Every file is signed with SHA-256 and, when a timestamp server is reachable, timestamped (so the signature stays valid after the certificate expires). The result of each
    signature is checked afterwards; a file that is not signed, or whose signature does not match its contents, is an error.
#>
[CmdletBinding()]
param(
    [string[]]$Files,
    [string]$CertificateThumbprint,
    [string]$PfxFile,
    [securestring]$PfxPassword,
    [string]$TimestampUrl = "http://timestamp.digicert.com"
)

function Get-SportifySigningCertificate {
    param([string]$Thumbprint, [string]$Pfx, [securestring]$Password)
    if ($Pfx) {
        if (-not (Test-Path $Pfx)) { throw "The certificate file was not found: $Pfx" }
        $cert = if ($Password) { [System.Security.Cryptography.X509Certificates.X509Certificate2]::new((Resolve-Path $Pfx).Path, $Password) } else { [System.Security.Cryptography.X509Certificates.X509Certificate2]::new((Resolve-Path $Pfx).Path) }
    }
    elseif ($Thumbprint) {
        $clean = ($Thumbprint -replace "\s", "").ToUpperInvariant()
        $cert = Get-ChildItem Cert:\CurrentUser\My, Cert:\LocalMachine\My -ErrorAction SilentlyContinue | Where-Object { $_.Thumbprint -eq $clean } | Select-Object -First 1
        if (-not $cert) { throw "No certificate with thumbprint $clean in CurrentUser\My or LocalMachine\My." }
    }
    else { throw "Give a certificate: -CertificateThumbprint, or -PfxFile (with -PfxPassword)." }
    if (-not $cert.HasPrivateKey) { throw "The certificate ($($cert.Subject)) has no private key here: it cannot sign." }
    if ($cert.NotAfter -lt (Get-Date)) { throw "The certificate ($($cert.Subject)) expired on $($cert.NotAfter.ToString('yyyy-MM-dd'))." }
    $usage = $cert.EnhancedKeyUsageList | Where-Object { $_.ObjectId -eq "1.3.6.1.5.5.7.3.3" }
    if (-not $usage) { throw "The certificate ($($cert.Subject)) is not a code-signing certificate (no Code Signing key usage)." }
    return $cert
}

<# Signs each file and verifies the result. Returns one object per file: Path, Status, Signer, Timestamped. Throws when a file could not be signed or does not verify. #>
function Sign-SportifyFiles {
    param(
        [Parameter(Mandatory)][string[]]$Files,
        [string]$CertificateThumbprint, [string]$PfxFile, [securestring]$PfxPassword,
        [string]$TimestampUrl = "http://timestamp.digicert.com"
    )
    $cert = Get-SportifySigningCertificate -Thumbprint $CertificateThumbprint -Pfx $PfxFile -Password $PfxPassword
    Write-Host "Signing with: $($cert.Subject)  (valid until $($cert.NotAfter.ToString('yyyy-MM-dd')))" -ForegroundColor Cyan
    $results = @()
    foreach ($file in $Files) {
        if (-not (Test-Path $file)) { throw "Cannot sign a file that is not there: $file" }
        $signed = $null
        $stamped = $false
        if ($TimestampUrl) {
            try { $signed = Set-AuthenticodeSignature -FilePath $file -Certificate $cert -HashAlgorithm SHA256 -TimestampServer $TimestampUrl -ErrorAction Stop; $stamped = $true }
            catch { Write-Warning "No timestamp for $file ($($_.Exception.Message)): signing without one, so the signature ends when the certificate does." }
        }
        if (-not $signed) { $signed = Set-AuthenticodeSignature -FilePath $file -Certificate $cert -HashAlgorithm SHA256 -ErrorAction Stop }

        $check = Get-AuthenticodeSignature -FilePath $file
        if (-not $check.SignerCertificate -or $check.SignerCertificate.Thumbprint -ne $cert.Thumbprint) { throw "$file is not signed by the given certificate after signing (status $($check.Status))." }
        if ($check.Status -in @("HashMismatch", "NotSigned", "Incompatible")) { throw "$file did not verify after signing: $($check.Status) $($check.StatusMessage)" }
        if ($check.Status -ne "Valid") { Write-Warning "$file is signed, but Windows does not trust the signer on this machine ($($check.Status)): expected for a self-signed or private-CA certificate." }
        $results += [pscustomobject]@{ Path = $file; Status = [string]$check.Status; Signer = $check.SignerCertificate.Subject; Timestamped = ($null -ne $check.TimeStamperCertificate) }
        Write-Host ("  signed  {0}   {1}{2}" -f $file, $check.Status, $(if ($check.TimeStamperCertificate) { ", timestamped" } else { ", NOT timestamped" }))
    }
    return $results
}

if ($Files) {
    Sign-SportifyFiles -Files $Files -CertificateThumbprint $CertificateThumbprint -PfxFile $PfxFile -PfxPassword $PfxPassword -TimestampUrl $TimestampUrl | Format-Table -AutoSize
}
