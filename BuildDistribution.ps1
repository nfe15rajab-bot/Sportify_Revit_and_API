<#
.SYNOPSIS
    Assembles the one thing Ali/Moamen/Sukriti actually need: a folder
    they can unzip and run. Produces dist\Sportify_Revit.exe (the
    installer) sitting next to dist\payload\ (the add-in DLL + its
    WebView2 dependencies + the bundled web app + a self-contained copy
    of Sportify.Api) — Sportify.Installer\Program.cs already knows how
    to take that payload folder and turn it into a working Revit add-in,
    this script's only job is building the three pieces and putting
    them where that installer expects to find them.

.PARAMETER CertificateThumbprint
    A code-signing certificate in CurrentUser\My or LocalMachine\My. With it (or -PfxFile) the add-in DLL, Sportify.Api.exe and Sportify_Revit.exe are signed and the
    signatures checked (Sign-Artifacts.ps1). Without one the build is UNSIGNED and says so at the end: SmartScreen warns on the installer and Revit asks whether to load
    an add-in from an unknown publisher. The certificate has to be bought or issued; there is none in this repository.

.PARAMETER PfxFile
    A .pfx file with the certificate and its private key; the password is asked for (or passed as -PfxPassword, a SecureString).

.PARAMETER TimestampUrl
    The RFC 3161 timestamp server used when signing (default DigiCert's). A signature made without one stops being valid when the certificate expires.

.PARAMETER RequireSigning
    Fail instead of producing an unsigned build when no certificate was given.

.NOTES
    Needs, on the machine running this script: the .NET SDK, real
    internet access (NuGet — WebView2 and the self-contained runtime
    packs aren't anything you can vendor by hand), and Revit 2025
    installed at the default location (SportfyRevit.csproj's HintPath
    points at C:\Program Files\Autodesk\Revit 2025\RevitAPI(UI).dll —
    those are compile-only references, never shipped, but they do have
    to exist on whichever machine builds this).
#>

[CmdletBinding()]
param(
    [string]$CertificateThumbprint,
    [string]$PfxFile,
    [securestring]$PfxPassword,
    [string]$TimestampUrl = "http://timestamp.digicert.com",
    [switch]$RequireSigning
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
. (Join-Path $root "Sign-Artifacts.ps1")
$willSign = [bool]($CertificateThumbprint -or $PfxFile)
if ($RequireSigning -and -not $willSign) { throw "-RequireSigning was given but there is no certificate (-CertificateThumbprint or -PfxFile)." }
if ($PfxFile -and -not $PfxPassword) { $PfxPassword = Read-Host "Password for $PfxFile" -AsSecureString }

function Step($msg) { Write-Host ""; Write-Host "== $msg ==" -ForegroundColor Cyan }

$distDir = Join-Path $root "dist"
$payloadDir = Join-Path $distDir "payload"
if (Test-Path $distDir) { Remove-Item $distDir -Recurse -Force }
New-Item -ItemType Directory -Path $payloadDir -Force | Out-Null

# ── 1. Revit add-in (also bundles the sibling web app — see
#      SportfyRevit.csproj's BundleWebApp target — as long as this repo
#      and sportfify_goldbeck are checked out side by side, same as always) ──
Step "Building the Revit add-in (SportfyRevit)"
$addinProj = Join-Path $root "SportfyRevit\SportfyRevit\SportfyRevit.csproj"
dotnet build $addinProj -c Release
if ($LASTEXITCODE -ne 0) { throw "SportfyRevit build failed." }

# The project sets a RuntimeIdentifier (QuestPDF's native engine needs it), so the output lands in ...\net8.0-windows\win-x64\, one folder deeper than a plain
# build: take whichever folder the newest SportfyRevit.dll is in, rather than a path that goes stale the next time the project's settings change.
$releaseDir = Join-Path $root "SportfyRevit\SportfyRevit\bin\Release"
$builtDll = Get-ChildItem $releaseDir -Recurse -Filter "SportfyRevit.dll" -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $builtDll) {
    throw "Expected build output not found under $releaseDir"
}
$addinOut = $builtDll.DirectoryName
Copy-Item "$addinOut\*" $payloadDir -Recurse -Force

if (-not (Test-Path (Join-Path $payloadDir "web\index.html"))) {
    Write-Warning "payload\web\index.html is missing — the sibling sportfify_goldbeck repo wasn't found at build time, so the installed add-in's web pane will show a 500 until that's fixed."
}

# ── 2. Sportify.Api, self-contained single-file — the piece that lets
#      SportfyRevitApp.EnsureApiRunning() start a real API with no
#      separate "dotnet run" step and no .NET install on the target
#      machine. Lands at payload\api\Sportify.Api.exe, exactly where
#      EnsureApiRunning() looks for it (<add-in folder>\api\). ──
Step "Publishing Sportify.Api (self-contained)"
$apiProj = Join-Path $root "Sportify.Api\Sportify.Api\Sportify.Api.csproj"
$apiOut = Join-Path $payloadDir "api"
dotnet publish $apiProj -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -o $apiOut
if ($LASTEXITCODE -ne 0) { throw "Sportify.Api publish failed." }

# Ship a clean database — ReferenceDataSeeder.cs rebuilds it from code on
# first run, and a leftover dev reference.db would just be stale data.
Get-ChildItem $apiOut -Filter "reference.db*" -ErrorAction SilentlyContinue | Remove-Item -Force

# ── 3. The installer itself, self-contained single-file — runs on a
#      machine with nothing installed at all, per its own csproj comment. ──
Step "Publishing Sportify_Revit.exe (the installer)"
$installerProj = Join-Path $root "Sportify.Installer\Sportify.Installer.csproj"
dotnet publish $installerProj -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -o $distDir
if ($LASTEXITCODE -ne 0) { throw "Sportify.Installer publish failed." }

# ── 4. Signing: what Sportify ships that is Sportify's own (the add-in, the API, the installer), not the runtime and WebView2 files that Microsoft already signed ──
if ($willSign) {
    Step "Signing"
    $ours = @((Join-Path $payloadDir "SportfyRevit.dll"), (Join-Path $payloadDir "api\Sportify.Api.exe"), (Join-Path $distDir "Sportify_Revit.exe")) | Where-Object { Test-Path $_ }
    $signed = Sign-SportifyFiles -Files $ours -CertificateThumbprint $CertificateThumbprint -PfxFile $PfxFile -PfxPassword $PfxPassword -TimestampUrl $TimestampUrl
    if (($signed | Where-Object { $_.Status -ne "Valid" }).Count -gt 0) {
        Write-Warning "Signed, but this machine does not trust the certificate (a self-signed or private-CA certificate): fine for trying, not for shipping."
    }
}
else {
    Write-Warning "UNSIGNED BUILD: no certificate was given (-CertificateThumbprint or -PfxFile). SmartScreen will warn when the installer is run and Revit will ask whether to load an add-in from an unknown publisher."
}

Step "Done"
Write-Host "dist\Sportify_Revit.exe + dist\payload\ are ready$(if ($willSign) { ' (signed)' } else { ' (UNSIGNED)' })." -ForegroundColor Green
Write-Host "Zip the whole dist\ folder — the .exe needs payload\ sitting right next to it." -ForegroundColor Green
