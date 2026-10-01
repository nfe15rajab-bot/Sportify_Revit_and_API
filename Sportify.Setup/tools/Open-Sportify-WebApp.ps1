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
# Whether the page itself is served: an API started from a development folder answers the API calls but has no web folder beside it, so it answers GET / with 404.
function Test-Web { try { (Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec 2).StatusCode -eq 200 } catch { $false } }
function Get-PortOwner { try { $c = Get-NetTCPConnection -LocalPort 5107 -State Listen -ErrorAction Stop | Select-Object -First 1; Get-Process -Id $c.OwningProcess -ErrorAction Stop } catch { $null } }
function Start-InstalledApi {
    if (-not (Test-Path $api)) { Write-Error "Sportify.Api.exe was not found at $api"; exit 1 }
    Start-Process -FilePath $api -WorkingDirectory (Split-Path $api) -WindowStyle Hidden
    $deadline = (Get-Date).AddSeconds($WaitSeconds)
    while ((Get-Date) -lt $deadline -and -not (Test-Web)) { Start-Sleep -Milliseconds 700 }
}

if (-not (Test-Api)) {
    Start-InstalledApi
}
elseif (-not (Test-Web)) {
    # Something on port 5107 answers as the Sportify API but does not serve the web app (typically a Sportify.Api run from a development folder). Say so instead of opening a 404 page.
    if (-not $NoBrowser) {
        $owner = Get-PortOwner
        $what = "another program"
        $isSportifyApi = $false
        if ($owner) {
            $isSportifyApi = ($owner.ProcessName -eq "Sportify.Api")
            $where = ""; try { $where = $owner.Path } catch { }
            $what = "$($owner.ProcessName) (process $($owner.Id))" + $(if ($where) { " from " + $where } else { "" })
        }
        Add-Type -AssemblyName System.Windows.Forms
        if ($isSportifyApi) {
            $text = "Another Sportify server is already using port 5107 and does not serve the web app, so the page would only show HTTP ERROR 404:`n`n$what`n`nClose it and open the web app from this installation?"
            $answer = [System.Windows.Forms.MessageBox]::Show($text, "Sportify web app", [System.Windows.Forms.MessageBoxButtons]::YesNo, [System.Windows.Forms.MessageBoxIcon]::Question)
            if ($answer -eq [System.Windows.Forms.DialogResult]::Yes) {
                try { $owner.Kill(); $owner.WaitForExit(5000) | Out-Null } catch { }
                Start-InstalledApi
            }
        } else {
            [void][System.Windows.Forms.MessageBox]::Show("Port 5107 is used by $what, which does not serve the Sportify web app, so the page would only show HTTP ERROR 404.`n`nClose that program and open the Sportify web app again.", "Sportify web app", [System.Windows.Forms.MessageBoxButtons]::OK, [System.Windows.Forms.MessageBoxIcon]::Warning)
        }
    }
}
if ($NoBrowser) { if (Test-Web) { exit 0 } else { exit 2 } }

if (-not (Test-Web)) { exit 2 }
$chrome = $null
foreach ($key in "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\chrome.exe", "HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\chrome.exe") {
    $p = (Get-ItemProperty $key -ErrorAction SilentlyContinue)."(default)"
    if ($p -and (Test-Path $p)) { $chrome = $p; break }
}
if ($chrome) { Start-Process -FilePath $chrome -ArgumentList $url } else { Start-Process $url }
