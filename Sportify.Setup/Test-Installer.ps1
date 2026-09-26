<#
.SYNOPSIS
    Installs the built Sportify installer silently into scratch folders, checks that everything landed where it should (and that the add-in is registered without pasting anything),
    optionally that the installed API serves the web app, then uninstalls and checks that it is gone. Never touches the real Revit Addins folder or the real settings.

.PARAMETER Setup
    The installer to test. Default: the newest dist\Sportify-Setup-*.exe.
.PARAMETER RunApi
    Also start the installed API (port 5107 must be free) and check that it serves the web app and the catalogue, and that the launcher script works.

.NOTES
    Uses the installer's own test switches: /ADDINSDIR /SETTINGSDIR /DELIVERABLES /NOSTARTAPI /NOPREREQS /DISABLELEGACY. Exit code 0 = everything held, 1 = something did not.
#>
[CmdletBinding()]
param([string]$Setup, [switch]$RunApi, [switch]$KeepFiles)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
if (-not $Setup) { $Setup = Get-ChildItem (Join-Path $repo "dist") -Filter "Sportify-Setup-*.exe" -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1 -ExpandProperty FullName }
if (-not $Setup -or -not (Test-Path $Setup)) { Write-Host "SKIP: no installer built (dist\Sportify-Setup-*.exe)."; exit 2 }
$version = (Get-Content (Join-Path $repo "VERSION") -Raw).Trim()

$failures = New-Object System.Collections.Generic.List[string]
function Expect($ok, $what) { if ($ok) { Write-Host "  ok   $what" } else { Write-Host "  FAIL $what" -ForegroundColor Red; $failures.Add($what) } }

$tmp = Join-Path $env:TEMP ("sportify-install-test-" + [guid]::NewGuid().ToString("N").Substring(0, 8))
$app = "$tmp\app folder"; $addins = "$tmp\addins"; $settings = "$tmp\settings"; $work = "$tmp\my sportify files"
New-Item -ItemType Directory -Force $addins | Out-Null
# an older add-in manifest, as found on machines that had the first Sportify: setup must switch it off when asked to
$legacy = '<RevitAddIns><AddIn Type="Application"><Name>Sportify</Name><Assembly>Sportify\Sportify.dll</Assembly></AddIn></RevitAddIns>'
Set-Content "$addins\Sportify.addin" $legacy
# ... and the same one for all users of the computer (C:\ProgramData\Autodesk\Revit\Addins\2025 on a real machine; redirected here so that the real one is never touched)
New-Item -ItemType Directory -Force "$tmp\allusers" | Out-Null
Set-Content "$tmp\allusers\Sportify.addin" $legacy

Write-Host "Installing $(Split-Path $Setup -Leaf) into $tmp"
$p = Start-Process -FilePath $Setup -ArgumentList @("/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/SP-", "/DIR=`"$app`"", "/ADDINSDIR=`"$addins`"", "/ALLUSERSADDINSDIR=`"$tmp\allusers`"", "/SETTINGSDIR=`"$settings`"", "/DELIVERABLES=`"$work`"", "/NOSTARTAPI=1", "/NOPREREQS=1", "/DISABLELEGACY=1", "/NOSHORTCUTS=1", "/LOG=`"$tmp\setup.log`"") -Wait -PassThru
Expect ($p.ExitCode -eq 0) "the installer exits with code 0 (got $($p.ExitCode))"

Write-Host "What was installed:"
foreach ($f in "SportfyRevit.dll", "web\index.html", "web\kineticsPostAnalysis.js", "api\Sportify.Api.exe", "tools\Open-Sportify-WebApp.ps1", "tools\Open-Sportify-InRevit.ps1", "tools\Stop-Sportify.ps1",
               "LICENSE_AGREEMENT.txt", "Library\Worksets\Sportify_Worksets_and_Phases.json", "Library\Database\README.txt", "Library\README.txt", "uninstall\unins000.exe") {
    Expect (Test-Path (Join-Path $app $f)) "installed: $f"
}
Expect ((Test-Path "$app\Library\Documentation\Sportify-User-Guide.pdf") -or (Test-Path "$app\Library\Documentation\Sportify-User-Guide.html")) "installed: the user guide"
Expect (Test-Path "$app\Library\Documentation\LICENSE_AGREEMENT.txt") "installed: the license in the library's documentation"
Expect (-not (Test-Path "$app\api\reference.db")) "no reference.db is shipped (the API makes it)"
$guide = Get-ChildItem "$app\Library\Documentation" -Filter "Sportify-User-Guide.html" -ErrorAction SilentlyContinue
if ($guide) { Expect (-not ((Get-Content $guide.FullName -Raw) -match '\{\{VERSION\}\}')) "the user guide has its version filled in"; Expect ((Get-Content $guide.FullName -Raw) -match [regex]::Escape($version)) "the user guide names version $version" }

Write-Host "How it is registered with Revit:"
$manifest = "$addins\SportfyRevit.addin"
Expect (Test-Path $manifest) "the add-in manifest is written to the Addins folder"
if (Test-Path $manifest) {
    [xml]$x = Get-Content $manifest -Raw -Encoding UTF8
    $ai = $x.RevitAddIns.AddIn
    Expect ($ai.Assembly -eq "$app\SportfyRevit.dll") "the manifest names the installed DLL by its full path ($($ai.Assembly))"
    Expect (Test-Path $ai.Assembly) "the DLL the manifest names exists"
    Expect ($ai.VendorDescription -eq "Digital Tools and Methods - Group of Sports and Gardens") "the author Revit shows is 'Digital Tools and Methods - Group of Sports and Gardens' ($($ai.VendorDescription))"
    Expect ($ai.FullClassName -eq "SportfyRevit.SportfyRevitApp" -and $ai.Type -eq "Application") "the manifest starts the application class"
}
Expect ((Test-Path "$addins\Sportify.addin.disabled") -and -not (Test-Path "$addins\Sportify.addin")) "the older Sportify.addin was switched off, not deleted"
Expect ((Test-Path "$tmp\allusers\Sportify.addin.disabled") -and -not (Test-Path "$tmp\allusers\Sportify.addin")) "the older all-users Sportify.addin was switched off too"

Write-Host "Settings and the Sportify folder:"
$json = Get-Content "$settings\settings.json" -Raw -Encoding UTF8 | ConvertFrom-Json
Expect ($json.workspace_folder -eq $work) "settings.json names the chosen deliverables folder"
Expect ($json.install_folder -eq $app) "settings.json names the install folder"
Expect ($json.version -eq $version) "settings.json carries version $version"
foreach ($n in "Layouts", "Sport fields", "Garden", "Physical analysis", "Videos", "Analysis reports", "Schedules", "Diagrams", "Mechanical", "Profile") { Expect (Test-Path (Join-Path $work $n)) "the Sportify folder has $n" }

if ($RunApi) {
    Write-Host "The installed API and web app:"
    $busy = $false; try { $null = Invoke-WebRequest "http://localhost:5107/api/AnalysisParameters" -UseBasicParsing -TimeoutSec 2; $busy = $true } catch { }
    if ($busy) { Write-Host "  SKIP port 5107 is in use" -ForegroundColor Yellow }
    else {
        # called, not started with -Wait: the launcher leaves the API running as its child, and -Wait would wait for that as well
        $null = & powershell -NoProfile -ExecutionPolicy Bypass -File "$app\tools\Open-Sportify-WebApp.ps1" -NoBrowser
        $rc = $LASTEXITCODE
        Expect ($rc -eq 0) "Open-Sportify-WebApp.ps1 starts the API and it answers (exit code $rc)"
        try {
            $index = Invoke-WebRequest "http://localhost:5107/" -UseBasicParsing -TimeoutSec 10
            Expect ($index.StatusCode -eq 200 -and $index.Content -match "Sportify") "the API serves the web app at http://localhost:5107/"
            $js = Invoke-WebRequest "http://localhost:5107/kineticsPostAnalysis.js" -UseBasicParsing -TimeoutSec 10
            Expect ($js.StatusCode -eq 200) "the API serves the web app's scripts"
            $cat = Invoke-WebRequest "http://localhost:5107/api/AnalysisParameters" -UseBasicParsing -TimeoutSec 10
            Expect ($cat.StatusCode -eq 200) "the catalogue answers (the database was made from the shipped data)"
            $sess = Invoke-WebRequest "http://localhost:5107/api/session" -Headers @{ "Sec-Fetch-Site" = "same-origin" } -UseBasicParsing -TimeoutSec 10
            Expect ($sess.StatusCode -eq 200) "the web app served by the API gets its session (same-origin handshake)"
            $denied = $false; try { $null = Invoke-WebRequest "http://localhost:5107/api/session" -UseBasicParsing -TimeoutSec 10 } catch { $denied = $_.Exception.Response.StatusCode.value__ -eq 403 }
            Expect $denied "a request with no Origin and no same-origin proof is still refused the write key"
        }
        catch { Expect $false "the installed API could not be queried: $($_.Exception.Message)" }
        Expect (Test-Path "$app\api\reference.db") "the API made its database (reference.db) in the install folder"
        $null = & powershell -NoProfile -ExecutionPolicy Bypass -File "$app\tools\Stop-Sportify.ps1"
        Start-Sleep -Seconds 2
        $still = Get-Process -Name "Sportify.Api" -ErrorAction SilentlyContinue | Where-Object { try { $_.Path -like "$app\*" } catch { $false } }
        Expect (-not $still) "Stop-Sportify.ps1 stops that API"
    }
}

Write-Host "Uninstalling:"
$u = Start-Process -FilePath "$app\uninstall\unins000.exe" -ArgumentList @("/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/ADDINSDIR=`"$addins`"", "/SETTINGSDIR=`"$settings`"") -Wait -PassThru
Expect ($u.ExitCode -eq 0) "the uninstaller exits with code 0 (got $($u.ExitCode))"
Start-Sleep -Seconds 2
Expect (-not (Test-Path $manifest)) "the add-in manifest is removed"
Expect (-not (Test-Path "$app\SportfyRevit.dll")) "the add-in is removed"
Expect (-not (Test-Path "$app\api")) "the API and its database are removed"
Expect (Test-Path "$work\Layouts") "the person's Sportify folder is kept"

if (-not $KeepFiles) { Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue }
if ($failures.Count -eq 0) { Write-Host "INSTALLER OK" -ForegroundColor Green; exit 0 }
Write-Host ("INSTALLER: {0} thing(s) did not hold (log: $tmp\setup.log)" -f $failures.Count) -ForegroundColor Red
exit 1
