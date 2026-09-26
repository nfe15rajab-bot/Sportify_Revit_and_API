<#
  Starts Revit 2025. The Sportify add-in opens its docked web app pane by itself the first time Revit is idle after it starts, so the app appears inside Revit. Used by the
  installer's last page ("open the web app in Revit instead") and the Start menu.
#>
$ErrorActionPreference = "Stop"
$revit = $null
foreach ($p in "$env:ProgramFiles\Autodesk\Revit 2025\Revit.exe", "${env:ProgramFiles(x86)}\Autodesk\Revit 2025\Revit.exe") { if (Test-Path $p) { $revit = $p; break } }
if (-not $revit) { Write-Error "Revit 2025 was not found."; exit 1 }
Start-Process -FilePath $revit
