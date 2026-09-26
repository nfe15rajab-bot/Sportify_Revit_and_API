using System.Diagnostics;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Sportify.Mechanical;

/// <summary>
/// One SOLIDWORKS instance for the run: the one already open on this machine if there is one (left open when we finish), else a new one that we start
/// (hidden unless asked, and closed when we finish unless asked to keep it). Everything is in metres and kilograms: the API's own system units.
/// </summary>
internal sealed class SolidWorksSession : IDisposable
{
    public ISldWorks App { get; }
    public bool StartedByUs { get; }
    readonly bool _keepOpen;

    SolidWorksSession(ISldWorks app, bool started, bool keepOpen, DateTime beganAt) { App = app; StartedByUs = started; _keepOpen = keepOpen; _started = beganAt; }

    public static SolidWorksSession Open(bool visible, bool keepOpen)
    {
        var beganAt = DateTime.UtcNow;
        var running = Process.GetProcessesByName("SLDWORKS").Length > 0;
        var type = Type.GetTypeFromProgID("SldWorks.Application") ?? throw new InvalidOperationException("SOLIDWORKS is not registered on this machine (SldWorks.Application).");
        var app = (ISldWorks)(Activator.CreateInstance(type) ?? throw new InvalidOperationException("SOLIDWORKS did not start."));
        if (!running) { app.Visible = visible; _runStartedAt = beganAt; }              // an instance the user already has open keeps its own visibility
        return new SolidWorksSession(app, !running, keepOpen, beganAt);
    }

    /// <summary>The default part or assembly template SOLIDWORKS is set up with, else the first one under its ProgramData templates folder (its name follows the language of the install).</summary>
    public string Template(bool assembly)
    {
        var pref = App.GetUserPreferenceStringValue((int)(assembly ? swUserPreferenceStringValue_e.swDefaultTemplateAssembly : swUserPreferenceStringValue_e.swDefaultTemplatePart));
        if (!string.IsNullOrWhiteSpace(pref) && File.Exists(pref)) return pref;

        var extension = assembly ? "*.asmdot" : "*.prtdot";
        foreach (var root in new[] { @"C:\ProgramData\SolidWorks", System.Environment.GetFolderPath(System.Environment.SpecialFolder.CommonApplicationData) + @"\SolidWorks" })
        {
            if (!Directory.Exists(root)) continue;
            var found = Directory.EnumerateFiles(root, extension, SearchOption.AllDirectories)
                .OrderBy(p => p.Contains("2026") ? 0 : 1).ThenBy(p => Path.GetFileName(p).Contains("Part") || Path.GetFileName(p).Contains("Teil") || Path.GetFileName(p).Contains("Assembly") || Path.GetFileName(p).Contains("Baugruppe") ? 0 : 1)
                .FirstOrDefault();
            if (found != null) return found;
        }
        throw new InvalidOperationException("No SOLIDWORKS " + (assembly ? "assembly" : "part") + " template was found (set one in Tools > Options > Default Templates).");
    }

    public string Info() => "SOLIDWORKS " + App.RevisionNumber() + (StartedByUs ? " (started for this run)" : " (already open)");

    public void Dispose()
    {
        if (StartedByUs && !_keepOpen)
        {
            try { App.ExitApp(); } catch { /* it is going away anyway */ }
            // a hidden instance that was started by automation does not always exit when asked (found live): give it a few seconds, then stop what this run started
            var deadline = DateTime.UtcNow.AddSeconds(8);
            while (DateTime.UtcNow < deadline && Process.GetProcessesByName("SLDWORKS").Any(p => p.StartTime.ToUniversalTime() >= _started.AddSeconds(-5))) Thread.Sleep(500);
            foreach (var name in new[] { "SLDWORKS", "sldworks_fs" })
                foreach (var p in Process.GetProcessesByName(name))
                {
                    try { if (p.StartTime.ToUniversalTime() >= _started.AddSeconds(-5)) p.Kill(); } catch { /* already gone */ }
                }
        }
    }

    readonly DateTime _started;

    static DateTime? _runStartedAt;

    /// <summary>Stops the SOLIDWORKS (and its file-search helper) this run started, when it will not answer. A SOLIDWORKS the person already had open is left alone.</summary>
    public static void KillStartedByThisRun()
    {
        if (_runStartedAt == null) return;
        foreach (var name in new[] { "SLDWORKS", "sldworks_fs" })
            foreach (var p in Process.GetProcessesByName(name))
            {
                try { if (p.StartTime.ToUniversalTime() >= _runStartedAt.Value.AddSeconds(-5)) p.Kill(); } catch (Exception) { /* already gone */ }
            }
    }
}
