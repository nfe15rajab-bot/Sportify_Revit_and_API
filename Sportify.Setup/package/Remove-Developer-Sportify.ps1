<#
.SYNOPSIS
    Takes the DEVELOPER side of Sportify out of Revit 2025 (the files a Visual Studio / dotnet build of the add-in copies into Revit's Addins folder, and the older June add-in), so that the
    installer can be tried on a clean Revit. Nothing is deleted: everything is MOVED into a backup folder you can move back.

.DESCRIPTION
    Looks in %APPDATA%\Autodesk\Revit\Addins\2025 (and %PROGRAMDATA%\Autodesk\Revit\Addins\2025 for Sportify manifests) and moves, when they are there:
      the manifests   SportfyRevit.addin (the developer build) and Sportify.addin (the older June add-in)
      the developer build's files   SportfyRevit.*, api\, web\, mechanical\, Templates\, SportifyGeneratedFamilies\, Sportify\ (the older add-in's folder),
                                    and the libraries that came with it (WebView2, QuestPDF, qpdf) - only when SportfyRevit.dll is there, so another add-in's copies are never taken
    It keeps in place (they are yours, not the add-in's): SportifyKineticsInputs.json, SportifySharedParameters.txt, and your Sportify folder of layouts and reports.
    It never touches another add-in (HivePlugin and the like) and stops if Revit is running.

    A Sportify that was INSTALLED with the installer is not touched: remove it in Windows Settings > Apps > Sportify > Uninstall.

.PARAMETER DryRun
    Only list what would be moved.
.PARAMETER Yes
    Do not ask.
.PARAMETER Purge
    Also move %APPDATA%\Sportify (settings, logs, the family and template locations Sportify remembered): a truly first-time start. Your Sportify folder of files is still kept.
.PARAMETER BackupDir
    Where the moved files go. Default: Documents\Sportify-developer-backup-<date and time>.

.EXAMPLE
    .\Remove-Developer-Sportify.ps1 -DryRun          # look first
    .\Remove-Developer-Sportify.ps1                  # then do it (asks once)
    To put everything back: move the contents of the backup folder back into %APPDATA%\Autodesk\Revit\Addins\2025.
#>
[CmdletBinding()]
param([switch]$DryRun, [switch]$Yes, [switch]$Purge, [string]$BackupDir, [string]$Year = "2025")

$ErrorActionPreference = "Stop"
if (Get-Process Revit -ErrorAction SilentlyContinue) { Write-Host "Revit is running. Close it first, then run this again." -ForegroundColor Red; exit 1 }

$userAddins = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$Year"
$allUsersAddins = Join-Path $env:ProgramData "Autodesk\Revit\Addins\$Year"
if (-not $BackupDir) { $BackupDir = Join-Path ([Environment]::GetFolderPath("MyDocuments")) ("Sportify-developer-backup-" + (Get-Date -Format "yyyyMMdd-HHmm")) }

$items = New-Object System.Collections.Generic.List[object]
function Add-Item($root, $name, $why) {
    $path = Join-Path $root $name
    if (Test-Path $path) {
        $size = if ((Get-Item $path).PSIsContainer) { (Get-ChildItem $path -Recurse -File -ErrorAction SilentlyContinue | Measure-Object Length -Sum).Sum } else { (Get-Item $path).Length }
        $items.Add([pscustomobject]@{ Path = $path; Name = $name; Root = $root; MB = [math]::Round(($size / 1MB), 1); Why = $why })
    }
}

# the manifests: ours and the older one, in the per-user and the all-users Addins folders
foreach ($root in $userAddins, $allUsersAddins) {
    if (-not (Test-Path $root)) { continue }
    Add-Item $root "SportfyRevit.addin" "the developer build's manifest"
    Add-Item $root "Sportify.addin" "the older June add-in's manifest"
}
# the files of the developer build (only when its main DLL is there)
if (Test-Path (Join-Path $userAddins "SportfyRevit.dll")) {
    foreach ($n in "SportfyRevit.dll", "SportfyRevit.pdb", "SportfyRevit.deps.json", "SportfyRevit.runtimeconfig.json", "api", "web", "mechanical", "Templates", "SportifyGeneratedFamilies",
                   "Microsoft.Web.WebView2.Core.dll", "Microsoft.Web.WebView2.Core.xml", "Microsoft.Web.WebView2.WinForms.dll", "Microsoft.Web.WebView2.WinForms.xml",
                   "Microsoft.Web.WebView2.Wpf.dll", "Microsoft.Web.WebView2.Wpf.xml", "WebView2Loader.dll", "QuestPDF.dll", "QuestPDF.Fonts.Lato.br", "QuestPdfSkia.dll", "qpdf.dll") {
        Add-Item $userAddins $n "the developer build"
    }
    # the native libraries folder: only when it holds nothing but the platform folders this build makes
    $rt = Join-Path $userAddins "runtimes"
    if ((Test-Path $rt) -and -not (Get-ChildItem $rt -Force | Where-Object { $_.Name -notmatch '^win(-x64|-x86|-arm64|-arm)?$' })) { Add-Item $userAddins "runtimes" "the developer build's native libraries" }
}
Add-Item $userAddins "Sportify" "the older June add-in's folder"
if ($Purge -and (Test-Path (Join-Path $env:APPDATA "Sportify"))) { Add-Item $env:APPDATA "Sportify" "Sportify's settings and logs (-Purge)" }

Write-Host ""
Write-Host "Sportify, developer side, in Revit $Year" -ForegroundColor Cyan
if ($items.Count -eq 0) { Write-Host "  nothing to move: there is no developer build or older add-in here." -ForegroundColor Green }
else { $items | ForEach-Object { Write-Host ("  {0,-42} {1,7} MB   {2}   [{3}]" -f $_.Name, $_.MB, $_.Why, $_.Root) } }
Write-Host "  kept in place: SportifyKineticsInputs.json, SportifySharedParameters.txt, your Sportify folder (layouts, reports), other add-ins$(if (-not $Purge) { ', %APPDATA%\Sportify' })"
if ($items.Count -eq 0) { exit 0 }
if ($DryRun) { Write-Host "`n(dry run: nothing was moved)" -ForegroundColor Yellow; exit 0 }

if (-not $Yes) {
    Write-Host ""
    $answer = Read-Host "Move these into $BackupDir ? (y/n)"
    if ($answer -notmatch '^(y|j)') { Write-Host "Cancelled: nothing was moved."; exit 0 }
}
New-Item -ItemType Directory -Force $BackupDir | Out-Null
foreach ($i in $items) {
    # keep the folder layout in the backup, so that moving it back is one copy: <backup>\<Addins folder name or ProgramData\...>\<file>
    $tag = if ($i.Root -eq $userAddins) { "AppData-Addins" } elseif ($i.Root -eq $allUsersAddins) { "ProgramData-Addins" } else { "AppData-Sportify" }
    $target = Join-Path $BackupDir $tag
    New-Item -ItemType Directory -Force $target | Out-Null
    try { Move-Item -LiteralPath $i.Path -Destination $target -Force; Write-Host "  moved $($i.Name)" }
    catch { Write-Host "  NOT moved $($i.Name) [$($i.Root)]: $($_.Exception.Message)" -ForegroundColor Yellow; if ($i.Root -eq $allUsersAddins) { Write-Host "     (it belongs to all users: run this script again as administrator)" -ForegroundColor Yellow } }
}
Write-Host "`nDone. Everything is in $BackupDir" -ForegroundColor Green
Write-Host "Start Revit once to see that the Sportify tab is gone, close it, then run the Sportify setup."
