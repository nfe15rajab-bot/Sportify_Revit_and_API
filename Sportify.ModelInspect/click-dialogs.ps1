# Answers the two known start-up prompts of a test Revit: "Load Once" (unsigned add-in prompt) and "Schliessen" (the duplicate-add-in "Startup Error"). Nothing else.
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class W { [DllImport("user32.dll")] public static extern bool SetProcessDPIAware(); [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h); [DllImport("user32.dll")] public static extern void keybd_event(byte b, byte s, uint f, UIntPtr e); }
"@
[void][W]::SetProcessDPIAware()
$root = [System.Windows.Automation.AutomationElement]::RootElement
$rp = Get-Process Revit -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $rp) { "no revit"; exit 1 }
$cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, [int]$rp.Id)
function ByName($w, $name) { $w.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $name))) }
foreach ($w in $root.FindAll([System.Windows.Automation.TreeScope]::Children, $cond)) {
    $target = $null; $what = ""
    if (ByName $w "Security - Unsigned Add-In") { $target = ByName $w "Load Once"; $what = "Load Once" }
    elseif (ByName $w "Sportify - Sportify — Startup Error") { $target = ByName $w "Schließen"; $what = "Schliessen" }
    if (-not $target) { continue }
    [W]::keybd_event(0x12, 0, 0, [UIntPtr]::Zero); [W]::keybd_event(0x12, 0, 2, [UIntPtr]::Zero)
    [void][W]::SetForegroundWindow([IntPtr]$w.Current.NativeWindowHandle)
    $target.SetFocus()
    [System.Windows.Forms.SendKeys]::SendWait(" ")
    "pressed space on '$what'"
}
"checked"
