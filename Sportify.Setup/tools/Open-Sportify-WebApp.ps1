<#
  Opens the Sportify web app in Chrome (or the default browser when there is no Chrome), starting the local API first if it is not running. The API (Sportify.Api.exe) also
  serves the web app on http://localhost:5107/, so this works with Revit closed; with Revit open the app also talks to the add-in. Used by the installer's last page, the
  Start menu shortcut "Sportify web app" and the README. Nothing here needs administrator rights.
#>
param([switch]$NoBrowser, [int]$WaitSeconds = 90)
$ErrorActionPreference = "Stop"
$app = Split-Path -Parent $PSScriptRoot                       # <install folder>\tools\ -> <install folder>
$api = Join-Path $app "api\Sportify.Api.exe"
$url = "http://localhost:5107/"

function Test-Api { try { (Invoke-WebRequest -Uri ($url + "api/AnalysisParameters") -UseBasicParsing -TimeoutSec 2).StatusCode -eq 200 } catch { $false } }

if (-not (Test-Api)) {
    if (-not (Test-Path $api)) { Write-Error "Sportify.Api.exe was not found at $api"; exit 1 }
    Start-Process -FilePath $api -WorkingDirectory (Split-Path $api) -WindowStyle Hidden
    $deadline = (Get-Date).AddSeconds($WaitSeconds)
    while ((Get-Date) -lt $deadline -and -not (Test-Api)) { Start-Sleep -Milliseconds 700 }
}
if ($NoBrowser) { if (Test-Api) { exit 0 } else { exit 2 } }

$chrome = $null
foreach ($key in "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\chrome.exe", "HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\chrome.exe") {
    $p = (Get-ItemProperty $key -ErrorAction SilentlyContinue)."(default)"
    if ($p -and (Test-Path $p)) { $chrome = $p; break }
}
if ($chrome) { Start-Process -FilePath $chrome -ArgumentList $url } else { Start-Process $url }
