<#
.SYNOPSIS
    Builds what Sportify ships: dist\payload\ (the add-in DLL + its WebView2 dependencies + the bundled web app + a self-contained copy of Sportify.Api, and the SOLIDWORKS
    tool when SOLIDWORKS is installed) and from it the ONE file people install with: dist\Sportify-Setup-<version>-Revit2025.exe (Inno Setup; Sportify.Setup\Build-Installer.ps1).
    The version comes from the VERSION file at the root of the repository. The installer also carries the library (Revit templates and families, the worksets and phases
    sheet, the documentation) and the Microsoft components the web app needs; -RequireLibrary fails the build when the templates or families are missing (release builds).
    -NoInstaller stops after the payload; -LegacyInstaller also publishes the older console installer (Sportify_Revit.exe, which needs payload\ beside it).

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

.PARAMETER WebAppDir
    The folder of the web app (sportfify_goldbeck) to bundle into the add-in. Default: a folder called sportfify_goldbeck next to this repository, as always. A build
    server that checks the two repositories out under one workspace names the folder here.

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
    [switch]$RequireSigning,
    [string]$WebAppDir,
    [switch]$NoInstaller,
    [switch]$LegacyInstaller,
    [switch]$RequireLibrary,
    [string]$TemplatesDir,
    [string]$FamiliesDir,
    [switch]$SkipPrerequisiteDownload
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
$webArgs = @()
if ($WebAppDir) { $webArgs = @("-p:SportifyWebAppDir=" + ((Resolve-Path $WebAppDir).Path.TrimEnd('\') + '\')) }
dotnet build $addinProj -c Release @webArgs
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

# ── 3. Signing what Sportify ships that is Sportify's own (the add-in and the API), BEFORE the installer carries them: not the runtime and WebView2 files Microsoft already signed ──
if ($willSign) {
    Step "Signing the add-in and the API"
    $ours = @((Join-Path $payloadDir "SportfyRevit.dll"), (Join-Path $payloadDir "api\Sportify.Api.exe")) | Where-Object { Test-Path $_ }
    $signed = Sign-SportifyFiles -Files $ours -CertificateThumbprint $CertificateThumbprint -PfxFile $PfxFile -PfxPassword $PfxPassword -TimestampUrl $TimestampUrl
    if (($signed | Where-Object { $_.Status -ne "Valid" }).Count -gt 0) {
        Write-Warning "Signed, but this machine does not trust the certificate (a self-signed or private-CA certificate): fine for trying, not for shipping."
    }
}

# ── 4. The installer: dist\Sportify-Setup-<version>-Revit2025.exe (Inno Setup, see Sportify.Setup\Build-Installer.ps1). ONE file, nothing else to copy: it carries the payload above, the
#      library (templates, families, worksets, documentation) and the Microsoft components the web app needs. -RequireLibrary makes a release fail when the templates or the families
#      are missing (they are made in Revit, not in Git: -TemplatesDir / -FamiliesDir say where they are). ──
$setupExe = $null
if (-not $NoInstaller) {
    Step "Building the installer (Inno Setup)"
    $installerArgs = @{ PayloadDir = $payloadDir; OutputDir = $distDir }
    if ($RequireLibrary) { $installerArgs.RequireLibrary = $true }
    if ($TemplatesDir) { $installerArgs.TemplatesDir = $TemplatesDir }
    if ($FamiliesDir) { $installerArgs.FamiliesDir = $FamiliesDir }
    if ($SkipPrerequisiteDownload) { $installerArgs.SkipPrerequisiteDownload = $true }
    & (Join-Path $root "Sportify.Setup\Build-Installer.ps1") @installerArgs | Out-Null
    $version = (Get-Content (Join-Path $root "VERSION") -Raw).Trim()
    $setupExe = Join-Path $distDir "Sportify-Setup-$version-Revit2025.exe"
    if (-not (Test-Path $setupExe)) { throw "The installer was not written: $setupExe" }
    if ($willSign) {
        Step "Signing the installer"
        Sign-SportifyFiles -Files @($setupExe) -CertificateThumbprint $CertificateThumbprint -PfxFile $PfxFile -PfxPassword $PfxPassword -TimestampUrl $TimestampUrl | Out-Null
        (Get-FileHash $setupExe -Algorithm SHA256).Hash.ToLower() + "  " + (Split-Path $setupExe -Leaf) | Set-Content "$setupExe.sha256" -Encoding ascii
    }
}

# ── 5. The older console installer (Sportify_Revit.exe next to payload\), only on request ──
if ($LegacyInstaller) {
    Step "Publishing Sportify_Revit.exe (the older console installer)"
    $installerProj = Join-Path $root "Sportify.Installer\Sportify.Installer.csproj"
    dotnet publish $installerProj -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -o $distDir
    if ($LASTEXITCODE -ne 0) { throw "Sportify.Installer publish failed." }
}

if (-not $willSign) {
    Write-Warning "UNSIGNED BUILD: no certificate was given (-CertificateThumbprint or -PfxFile). SmartScreen will warn when the installer is run and Revit will ask whether to load an add-in from an unknown publisher."
}

Step "Done"
if ($setupExe) { Write-Host ("$setupExe is ready$(if ($willSign) { ' (signed)' } else { ' (UNSIGNED)' }), {0:N0} MB: one file, nothing else to copy." -f ((Get-Item $setupExe).Length / 1MB)) -ForegroundColor Green }
else { Write-Host "dist\payload\ is ready (-NoInstaller)." -ForegroundColor Green }
