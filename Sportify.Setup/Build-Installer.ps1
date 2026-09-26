<#
.SYNOPSIS
    Builds the Sportify installer (Sportify-Setup-<version>-Revit2025.exe) with Inno Setup from the payload that BuildDistribution.ps1 staged.

.DESCRIPTION
    1. reads the version from the VERSION file at the root of the repository (the one place it is written);
    2. stages the library that is installed next to the add-in (dist\library): the Revit templates (.rte), the Revit families (.rfa), the workset and phase sheet, the notes on the
       database, and the documentation (the user guide as PDF, the license agreement, notes on the Sportify folder and on the Revit-SOLIDWORKS bridge);
    3. fetches the two Microsoft components the web app needs on a machine that lacks them (the WebView2 bootstrapper and the Visual C++ runtime), checks that Microsoft signed them
       and lets the installer carry them, so that setup needs the internet for nothing but WebView2's own download;
    4. runs Inno Setup (ISCC.exe) on Sportify.iss.

    The templates (about 80 MB each of Revit files) and the families are not in Git: they are made in Revit. They are taken from -TemplatesDir and -FamiliesDir; a build without
    them still works (the installer then simply has no such folder) unless -RequireLibrary is given, which a release build should.

.PARAMETER PayloadDir
    The folder BuildDistribution.ps1 filled: SportfyRevit.dll and its files, web\, api\ (and mechanical\ when SOLIDWORKS' interop was available).
.PARAMETER TemplatesDir
    Folder with the Sportify_*.rte templates. Default: SportfyRevit\SportfyRevit\Templates in this repository.
.PARAMETER FamiliesDir
    Folder with the .rfa families to ship. Default: the add-in's own cache in %APPDATA%\Autodesk\Revit\Addins\2025\SportifyGeneratedFamilies, when it is there.
.PARAMETER Iscc
    Path of ISCC.exe. Default: found in the usual places (per-user and per-machine installs of Inno Setup 6) or on the PATH.
.PARAMETER SkipPrerequisiteDownload
    Do not fetch the Microsoft components (the installer then downloads them on the person's computer, if it needs them).
.PARAMETER RequireLibrary
    Fail when there are no templates or no families to ship.
#>
[CmdletBinding()]
param(
    [string]$PayloadDir,
    [string]$OutputDir,
    [string]$TemplatesDir,
    [string]$FamiliesDir,
    [string]$Iscc,
    [switch]$SkipPrerequisiteDownload,
    [switch]$RequireLibrary
)

$ErrorActionPreference = "Stop"
$here = $PSScriptRoot
$repo = Split-Path -Parent $here
function Step($msg) { Write-Host ""; Write-Host "== $msg ==" -ForegroundColor Cyan }

if (-not $OutputDir) { $OutputDir = Join-Path $repo "dist" }
if (-not $PayloadDir) { $PayloadDir = Join-Path $OutputDir "payload" }
if (-not $TemplatesDir) { $TemplatesDir = Join-Path $repo "SportfyRevit\SportfyRevit\Templates" }
if (-not $FamiliesDir) { $FamiliesDir = Join-Path $env:APPDATA "Autodesk\Revit\Addins\2025\SportifyGeneratedFamilies" }
$PayloadDir = (Resolve-Path $PayloadDir).Path
if (-not (Test-Path (Join-Path $PayloadDir "SportfyRevit.dll"))) { throw "The payload has no SportfyRevit.dll: $PayloadDir (run BuildDistribution.ps1 first)." }
if (-not (Test-Path (Join-Path $PayloadDir "api\Sportify.Api.exe"))) { throw "The payload has no api\Sportify.Api.exe: $PayloadDir" }
if (-not (Test-Path (Join-Path $PayloadDir "web\index.html"))) { throw "The payload has no web\index.html: the web app was not bundled (BuildDistribution.ps1 -WebAppDir)." }

# ---- the version
Step "Version"
$version = (Get-Content (Join-Path $repo "VERSION") -Raw).Trim()
if ($version -notmatch '^(\d+)\.(\d+)\.(\d+)(-[0-9A-Za-z.-]+)?$') { throw "VERSION is not a Semantic Version (MAJOR.MINOR.PATCH[-pre-release]): '$version'" }
$numeric = "$($Matches[1]).$($Matches[2]).$($Matches[3]).0"
Write-Host "Sportify $version (file version $numeric)"

# ---- the library
Step "Staging the library"
$library = Join-Path $OutputDir "library"
if (Test-Path $library) { Remove-Item $library -Recurse -Force }
foreach ($d in "Templates", "Families", "Worksets", "Documentation", "Database") { New-Item -ItemType Directory -Force (Join-Path $library $d) | Out-Null }

$rte = @(Get-ChildItem $TemplatesDir -Filter "*.rte" -ErrorAction SilentlyContinue)
if ($rte.Count -gt 0) { $rte | Copy-Item -Destination (Join-Path $library "Templates"); Write-Host ("templates: {0}" -f (($rte | ForEach-Object { $_.Name }) -join ", ")) }
else { Remove-Item (Join-Path $library "Templates"); $msg = "No Revit templates in ${TemplatesDir}: the installer will have no Library\Templates."; if ($RequireLibrary) { throw $msg } else { Write-Warning $msg } }

$rfa = @(Get-ChildItem $FamiliesDir -Filter "*.rfa" -ErrorAction SilentlyContinue | Where-Object { $_.Name -notmatch '\.\d{4}\.rfa$' })      # not Revit's backup copies (Name.0001.rfa)
if ($rfa.Count -gt 0) { $rfa | Copy-Item -Destination (Join-Path $library "Families"); Write-Host "families: $($rfa.Count)" }
else { Remove-Item (Join-Path $library "Families"); $msg = "No Revit families in ${FamiliesDir}: the installer will have no Library\Families."; if ($RequireLibrary) { throw $msg } else { Write-Warning $msg } }

Copy-Item (Join-Path $here "Library\Worksets\*") (Join-Path $library "Worksets")
Copy-Item (Join-Path $here "Library\Database\*") (Join-Path $library "Database")

$docs = Join-Path $library "Documentation"
Copy-Item (Join-Path $here "LICENSE_AGREEMENT.txt") $docs
if (Test-Path (Join-Path $repo "WORKSPACE.md")) { Copy-Item (Join-Path $repo "WORKSPACE.md") (Join-Path $docs "The-Sportify-folder.md") }
if (Test-Path (Join-Path $repo "Sportify.Mechanical\BRIDGE.md")) { Copy-Item (Join-Path $repo "Sportify.Mechanical\BRIDGE.md") (Join-Path $docs "Revit-SOLIDWORKS-bridge.md") }
$guideHtml = Join-Path $docs "Sportify-User-Guide.html"
(Get-Content (Join-Path $here "docs\USER_GUIDE.html") -Raw -Encoding UTF8).Replace("{{VERSION}}", $version) | Set-Content $guideHtml -Encoding UTF8

# the guide as a PDF, printed by Edge (every Windows 10/11 has it); without it the HTML guide is still installed
$edge = @("${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe", "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
$guidePdf = Join-Path $docs "Sportify-User-Guide.pdf"
if ($edge) {
    $edgeProfile = Join-Path $env:TEMP ("sportify-edge-" + [guid]::NewGuid().ToString("N"))
    # Edge writes its own diagnostics to stderr even when it succeeds, which "Stop" would turn into an error: it runs as a process of its own
    $edgeArgs = @("--headless=new", "--disable-gpu", "--no-first-run", "--user-data-dir=`"$edgeProfile`"", "--no-pdf-header-footer", "--print-to-pdf=`"$guidePdf`"", "`"$(([uri]$guideHtml).AbsoluteUri)`"")
    Start-Process -FilePath $edge -ArgumentList $edgeArgs -Wait -WindowStyle Hidden
    Start-Sleep -Seconds 2
    Remove-Item $edgeProfile -Recurse -Force -ErrorAction SilentlyContinue
}
if (Test-Path $guidePdf) { Write-Host ("user guide: {0:N0} KB PDF" -f ((Get-Item $guidePdf).Length / 1KB)) } else { Write-Warning "The user guide could not be printed to PDF (no Edge?): the HTML guide is installed instead." }
@"
SPORTIFY $version - README
================================================================================
This folder holds what came with Sportify (Revit 2025 add-in, web app, local API).

  Sportify-User-Guide.pdf   the guide for people who use Sportify: install, first start, a walkthrough
  LICENSE_AGREEMENT.txt     the license agreement you accepted
  The-Sportify-folder.md    what goes where in your Sportify folder
  Revit-SOLIDWORKS-bridge.md how the Kinetics bridge between Revit and SOLIDWORKS works and what it does not do

Start menu > Sportify: the web app (Chrome), the web app inside Revit, your files, this library, the user guide, uninstall.
Sportify is open source, for educational purposes, and still under testing: https://github.com/nfe15rajab-bot/Sportify_Revit_and_API
Digital Tools and Methods - Group of Sports and Gardens, TH OWL.
"@ | Set-Content (Join-Path $docs "README.txt") -Encoding UTF8
Copy-Item (Join-Path $docs "README.txt") (Join-Path $library "README.txt")

# ---- the Microsoft components
$prereq = Join-Path $OutputDir "prereqs"
if (Test-Path $prereq) { Remove-Item $prereq -Recurse -Force }
New-Item -ItemType Directory -Force $prereq | Out-Null
if (-not $SkipPrerequisiteDownload) {
    Step "Fetching the Microsoft components (WebView2 bootstrapper, Visual C++ runtime)"
    $items = @(
        @{ Name = "MicrosoftEdgeWebview2Setup.exe"; Url = "https://go.microsoft.com/fwlink/p/?LinkId=2124703" },
        @{ Name = "vc_redist.x64.exe"; Url = "https://aka.ms/vs/17/release/vc_redist.x64.exe" })
    foreach ($item in $items) {
        $target = Join-Path $prereq $item.Name
        try {
            Invoke-WebRequest -Uri $item.Url -OutFile $target -UseBasicParsing
            $sig = Get-AuthenticodeSignature $target
            if ($sig.Status -ne "Valid" -or $sig.SignerCertificate.Subject -notmatch "O=Microsoft Corporation") { Remove-Item $target -Force; throw "not signed by Microsoft ($($sig.Status))" }
            Write-Host ("{0}: {1:N1} MB, signed by Microsoft" -f $item.Name, ((Get-Item $target).Length / 1MB))
        }
        catch { Write-Warning "$($item.Name) was not carried in the installer ($($_.Exception.Message)): setup will download it on the person's computer if it needs it."; Remove-Item $target -Force -ErrorAction SilentlyContinue }
    }
}

# ---- Inno Setup
Step "Compiling the installer"
if (-not $Iscc) {
    $Iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $Iscc) { $cmd = Get-Command iscc.exe -ErrorAction SilentlyContinue; if ($cmd) { $Iscc = $cmd.Source } }
}
if (-not $Iscc -or -not (Test-Path $Iscc)) { throw "Inno Setup 6 (ISCC.exe) was not found. Install it (winget install JRSoftware.InnoSetup) or pass -Iscc." }
$isccArgs = @("/Qp", "/DAppVersion=$version", "/DAppVersionNumeric=$numeric", "/DPayloadDir=$PayloadDir", "/DLibraryDir=$library", "/DPrereqDir=$prereq", "/DOutputDir=$OutputDir", (Join-Path $here "Sportify.iss"))
& $Iscc @isccArgs
if ($LASTEXITCODE -ne 0) { throw "ISCC failed (exit code $LASTEXITCODE)." }

$exe = Join-Path $OutputDir "Sportify-Setup-$version-Revit2025.exe"
if (-not (Test-Path $exe)) { throw "The installer was not written: $exe" }
$hash = (Get-FileHash $exe -Algorithm SHA256).Hash.ToLower()
"$hash  $(Split-Path $exe -Leaf)" | Set-Content "$exe.sha256" -Encoding ascii
Step "Done"
Write-Host ("{0}  {1:N0} MB" -f $exe, ((Get-Item $exe).Length / 1MB)) -ForegroundColor Green
Write-Host "sha256 $hash"
$exe
