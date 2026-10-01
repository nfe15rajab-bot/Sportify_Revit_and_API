# Reads a Revit project with the model inspector and prints what is in it. Revit must be CLOSED: this starts one, unattended, and it is closed again at the end.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File Run-Inspect.ps1 -File "C:\path\model.rvt" [-Out C:\some\folder] [-TimeoutMinutes 25]
#
# What it does, in order: builds Sportify.ModelInspect, writes a one-run manifest (SportifyModelInspect.addin) into the user's Revit 2025 Addins folder, starts Revit with the two
# environment variables the inspector reads, answers the two known start-up prompts (click-dialogs.ps1), waits for inspection.json, prints inspection.txt, and removes the manifest
# again (also when something fails). Sportify's own add-in is neither replaced nor changed. The project itself is only READ: the inspector works on a copy in the output folder.
param(
    [Parameter(Mandatory = $true)][string]$File,
    [string]$Out = "",
    [int]$TimeoutMinutes = 25,
    [string]$Layout = ""
)
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $Out) { $Out = Join-Path $here ("inspection-" + (Get-Date -Format "yyyyMMdd-HHmmss")) }
if (-not (Test-Path $File)) { "The file does not exist: $File"; exit 2 }
if (Get-Process Revit -ErrorAction SilentlyContinue) { "Revit is running: close it first"; exit 2 }
$revit = "C:\Program Files\Autodesk\Revit 2025\Revit.exe"
if (-not (Test-Path $revit)) { "Revit 2025 was not found at $revit"; exit 2 }

$buildOutput = dotnet build (Join-Path $here "Sportify.ModelInspect.csproj") -c Release 2>&1
if ($buildOutput | Select-String -Pattern " error ") { $buildOutput | Select-String -Pattern " error " | Select-Object -First 10; "BUILD FAILED"; exit 1 }
$dll = Join-Path $here "bin\Release\net8.0-windows\Sportify.ModelInspect.dll"
$manifest = Join-Path $env:APPDATA "Autodesk\Revit\Addins\2025\SportifyModelInspect.addin"
New-Item -ItemType Directory -Force $Out | Out-Null
$p = $null
try {
    @"
<?xml version="1.0" encoding="utf-8"?>
<RevitAddIns>
  <AddIn Type="Application">
    <Name>SportifyModelInspect</Name>
    <Assembly>$dll</Assembly>
    <AddInId>9b1a4a2e-6f0c-4d0e-9d3a-51d2f4a7c101</AddInId>
    <FullClassName>Sportify.ModelInspect.InspectApp</FullClassName>
    <VendorId>SPFY</VendorId>
    <VendorDescription>Sportify model inspector (one run)</VendorDescription>
  </AddIn>
</RevitAddIns>
"@ | Set-Content -Path $manifest -Encoding UTF8
    $env:SPORTIFY_INSPECT_FILE = (Resolve-Path $File).Path
    $env:SPORTIFY_INSPECT_OUT = (Resolve-Path $Out).Path
    if ($Layout) { $env:SPORTIFY_INSPECT_LAYOUT = (Resolve-Path $Layout).Path }
    $p = Start-Process -FilePath $revit -PassThru
    "started Revit pid $($p.Id); report goes to $Out"
    $report = Join-Path $Out "inspection.json"
    $deadline = (Get-Date).AddMinutes($TimeoutMinutes)
    while ((Get-Date) -lt $deadline) {
        if (Test-Path $report) { break }
        if (-not (Get-Process -Id $p.Id -ErrorAction SilentlyContinue)) { "Revit exited without a report"; break }
        powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $here "click-dialogs.ps1") | Where-Object { $_ -like "pressed*" }
        Start-Sleep -Seconds 3
    }
    if (Test-Path $report) {
        # give it a moment to write the text and the pictures, then show the text
        Start-Sleep -Seconds 2
        Get-Content (Join-Path $Out "inspection.txt") -Raw
        if (Test-Path (Join-Path $Out "bim-audit.txt")) { Get-Content (Join-Path $Out "bim-audit.txt") -Raw }
    } else {
        "No report after $TimeoutMinutes minutes. Windows Revit has open:"
        Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
        $root = [System.Windows.Automation.AutomationElement]::RootElement
        $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, [int]$p.Id)
        foreach ($w in $root.FindAll([System.Windows.Automation.TreeScope]::Children, $cond)) { "  '" + $w.Current.Name + "'" }
    }
    $wait = (Get-Date).AddSeconds(90)
    while ((Get-Date) -lt $wait -and (Get-Process -Id $p.Id -ErrorAction SilentlyContinue)) { Start-Sleep -Seconds 2 }
}
finally {
    if ($p -and (Get-Process -Id $p.Id -ErrorAction SilentlyContinue)) { "Revit is still open; stopping it"; Stop-Process -Id $p.Id -Force }
    if (Test-Path $manifest) { Remove-Item $manifest -Force }
    Remove-Item Env:SPORTIFY_INSPECT_FILE -ErrorAction SilentlyContinue
    Remove-Item Env:SPORTIFY_INSPECT_OUT -ErrorAction SilentlyContinue
    Remove-Item Env:SPORTIFY_INSPECT_LAYOUT -ErrorAction SilentlyContinue
}
"done; the manifest is removed. Everything is in $Out"
