# Runs the unattended IFC-worksets self-test (SportfyRevit/IfcWorksetsSelfTest.cs) on a copy of a real project, in a real Revit. Revit must be CLOSED, and the add-in in
# %APPDATA%\Autodesk\Revit\Addins\2025 must be the build that has the self-test (a Release build of SportfyRevit deploys it).
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File Run-IfcWorksetsSelfTest.ps1 -File "C:\path\model.rvt" [-RoofIfcTag 16792485] [-Out C:\some\folder] [-TimeoutMinutes 20]
#
# It starts Revit with the environment variables the self-test reads, answers the known start-up prompt of the duplicate Sportify add-in (the "Startup Error" dialog: Close), waits for
# ifc-worksets-selftest.json, prints it, and stops Revit if it is still open afterwards. The project itself is never opened: the self-test works on a copy in the output folder.
param(
    [Parameter(Mandatory = $true)][string]$File,
    [string]$Out = "",
    [string]$RoofIfcTag = "",
    [int]$TimeoutMinutes = 20
)
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $Out) { $Out = Join-Path $here ("ifc-worksets-selftest-" + (Get-Date -Format "yyyyMMdd-HHmmss")) }
if (-not (Test-Path $File)) { "The file does not exist: $File"; exit 2 }
if (Get-Process Revit -ErrorAction SilentlyContinue) { "Revit is running: close it first"; exit 2 }
$revit = "C:\Program Files\Autodesk\Revit 2025\Revit.exe"
if (-not (Test-Path $revit)) { "Revit 2025 was not found at $revit"; exit 2 }
New-Item -ItemType Directory -Force $Out | Out-Null
$report = Join-Path $Out "ifc-worksets-selftest.json"
if (Test-Path $report) { Remove-Item $report -Force }

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class SelfTestWin { [DllImport("user32.dll")] public static extern bool SetProcessDPIAware(); [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h); [DllImport("user32.dll")] public static extern void keybd_event(byte b, byte s, uint f, UIntPtr e); }
"@
[void][SelfTestWin]::SetProcessDPIAware()
$closeName = "Schlie" + [char]0x00DF + "en"

function Answer-Prompts([int]$processId) {
    try {
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $processId)
    foreach ($w in $root.FindAll([System.Windows.Automation.TreeScope]::Children, $cond)) {
        $target = $null
        $find = { param($name) $w.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $name))) }
        if (& $find "Security - Unsigned Add-In") { $target = & $find "Load Once" }
        elseif (& $find "Sportify - Sportify - Startup Error") { $target = & $find $closeName }
        elseif (& $find ("Sportify - Sportify " + [char]0x2014 + " Startup Error")) { $target = & $find $closeName }
        if (-not $target) { continue }
        [SelfTestWin]::keybd_event(0x12, 0, 0, [UIntPtr]::Zero); [SelfTestWin]::keybd_event(0x12, 0, 2, [UIntPtr]::Zero)
        [void][SelfTestWin]::SetForegroundWindow([IntPtr]$w.Current.NativeWindowHandle)
        $target.SetFocus()
        [System.Windows.Forms.SendKeys]::SendWait(" ")
        "answered a start-up prompt with '" + $target.Current.Name + "'"
    }
    } catch { }      # a window that closes while it is being read is not an error
}

$p = $null
try {
    $env:SPORTIFY_IFC_WORKSETS_SELFTEST = (Resolve-Path $Out).Path
    $env:SPORTIFY_IFC_WORKSETS_FILE = (Resolve-Path $File).Path
    if ($RoofIfcTag) { $env:SPORTIFY_IFC_WORKSETS_ROOF_IFCTAG = $RoofIfcTag }
    $p = Start-Process -FilePath $revit -PassThru
    "started Revit pid $($p.Id); the report goes to $report"
    $deadline = (Get-Date).AddMinutes($TimeoutMinutes)
    while ((Get-Date) -lt $deadline) {
        if (Test-Path $report) { break }
        if (-not (Get-Process -Id $p.Id -ErrorAction SilentlyContinue)) { "Revit exited without a report"; break }
        Answer-Prompts $p.Id
        Start-Sleep -Seconds 3
    }
    if (Test-Path $report) { Start-Sleep -Seconds 1; Get-Content $report -Raw } else { "No report after $TimeoutMinutes minutes." }
    $wait = (Get-Date).AddSeconds(90)
    while ((Get-Date) -lt $wait -and (Get-Process -Id $p.Id -ErrorAction SilentlyContinue)) { Start-Sleep -Seconds 2 }
}
finally {
    if ($p -and (Get-Process -Id $p.Id -ErrorAction SilentlyContinue)) { "Revit is still open; stopping it"; Stop-Process -Id $p.Id -Force }
    Remove-Item Env:SPORTIFY_IFC_WORKSETS_SELFTEST -ErrorAction SilentlyContinue
    Remove-Item Env:SPORTIFY_IFC_WORKSETS_FILE -ErrorAction SilentlyContinue
    Remove-Item Env:SPORTIFY_IFC_WORKSETS_ROOF_IFCTAG -ErrorAction SilentlyContinue
}
"done. Everything is in $Out"
