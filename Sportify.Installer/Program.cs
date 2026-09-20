using System.Diagnostics;
using Microsoft.Web.WebView2.Core;

namespace Sportify.Installer;

/// <summary>
/// The whole point of this project: someone with no dev tools, no idea
/// what a DLL is, and Revit 2025 already installed should be able to run
/// one thing and have the Sportify add-in just work. Ships as one exe
/// sitting next to a "payload" folder (the add-in DLL + its WebView2
/// dependencies + the bundled web app + the self-contained API exe) —
/// see BuildDistribution.ps1 for how that folder gets assembled. Installs
/// per-user (no admin rights needed) by copying the payload into
/// %APPDATA%\Autodesk\Revit\Addins\2025\ and writing a .addin manifest
/// pointing at wherever it just installed to — the one thing that can
/// never be a static file in the payload, since it has to name a path
/// that doesn't exist until install time.
/// </summary>
internal static class Program
{
    private const string AddinFileName = "SportfyRevit.addin";
    private const string MainAssemblyName = "SportfyRevit.dll";

    private static int Main(string[] args)
    {
        Console.WriteLine("=======================================");
        Console.WriteLine(" Sportify — Revit 2025 Add-in Installer");
        Console.WriteLine("=======================================");
        Console.WriteLine();

        var installerDir = AppContext.BaseDirectory;
        var payloadDir = Path.Combine(installerDir, "payload");

        if (!Directory.Exists(payloadDir) || !File.Exists(Path.Combine(payloadDir, MainAssemblyName)))
        {
            Console.WriteLine($"ERROR: couldn't find the payload folder next to this installer.");
            Console.WriteLine($"Expected: {payloadDir}");
            Console.WriteLine("Make sure this .exe stayed in the same folder it was extracted/downloaded with.");
            return Fail();
        }

        var revitAddinsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Autodesk", "Revit", "Addins", "2025");

        try
        {
            Console.WriteLine($"Installing to: {revitAddinsDir}");
            Directory.CreateDirectory(revitAddinsDir);
            CopyDirectory(payloadDir, revitAddinsDir);
            Console.WriteLine("Files copied.");

            var dllPath = Path.Combine(revitAddinsDir, MainAssemblyName);
            WriteAddinManifest(Path.Combine(revitAddinsDir, AddinFileName), dllPath);
            Console.WriteLine(".addin manifest written.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ERROR during install: {ex.Message}");
            return Fail();
        }

        Console.WriteLine();
        ChooseWorkspace(args);

        Console.WriteLine();
        CheckWebView2Runtime();

        Console.WriteLine();
        var revitExe = ResolveRevitExePath();
        LaunchRevit(revitExe);

        return Success();
    }

    /// <summary>
    /// Where Sportify keeps what it makes: the layouts, the charts and PDFs of the analyses, the videos, the schedules. Default is a "Sportify Workspace" folder in Documents;
    /// the person can type another (or pass --workspace "D:\\Some Folder" for a silent install, or --default-workspace to take the default without asking). The folder
    /// is made now with a subfolder for every kind of deliverable, and the choice is written to %APPDATA%\Sportify\settings.json, which the add-in reads: nothing
    /// has to be imported or exported by hand afterwards, the web app and Revit both read and write that folder.
    /// </summary>
    private static void ChooseWorkspace(string[] args)
    {
        var chosen = SportfyRevit.SportifyWorkspace.Folder;                       // an earlier install's choice, else the default
        var given = ArgValue(args, "--workspace");
        if (given != null) chosen = given;
        else if (!args.Contains("--default-workspace"))
        {
            Console.WriteLine("Where should Sportify keep your files (layouts, analysis charts, reports, videos)?");
            Console.Write($"Press Enter for {chosen}, or type another folder: ");
            var typed = Console.ReadLine()?.Trim().Trim('"');
            if (!string.IsNullOrEmpty(typed)) chosen = typed;
        }

        try
        {
            chosen = Path.GetFullPath(chosen);
            SportfyRevit.SportifyWorkspace.UseFolder(chosen);
            SportfyRevit.SportifyWorkspace.EnsureCreated();
            SportfyRevit.SportifyWorkspace.SaveSetting(chosen);
            Console.WriteLine($"Your Sportify folder: {chosen}");
            Console.WriteLine("  with a subfolder for " + string.Join(", ", SportfyRevit.SportifyWorkspace.Kinds.Select(k => k.Folder)) + ".");
            Console.WriteLine("  (Change it later by editing workspace_folder in " + SportfyRevit.SportifyWorkspace.SettingsPath + ".)");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Couldn't set up the folder \"{chosen}\": {ex.Message}");
            Console.WriteLine("Sportify will use the default (Documents\\Sportify Workspace) and make it the first time it saves something.");
        }
    }

    private static string? ArgValue(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1].Trim('"') : null;
    }

    /// <summary>
    /// The add-in itself always installs to the per-user Addins folder
    /// above, regardless of where Revit is installed — Revit only reads
    /// that folder's .addin manifests, and this installer never needs (or
    /// wants) write access to Revit's own Program Files folder. The only
    /// reason to know Revit's install location at all is to launch it for
    /// the user right after installing, so the "load this add-in?" prompt
    /// and the Sportify pane show up immediately instead of leaving a
    /// silent install with no visible result.
    /// </summary>
    private static string? ResolveRevitExePath()
    {
        var defaultPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Autodesk", "Revit 2025", "Revit.exe");

        if (File.Exists(defaultPath))
        {
            Console.Write($"Found Revit 2025 at the default location ({defaultPath}). Launch it now? [Y/n]: ");
            var answer = Console.ReadLine()?.Trim();
            if (string.IsNullOrEmpty(answer) || string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase))
                return defaultPath;
            return null;
        }

        Console.WriteLine($"Couldn't find Revit 2025 at the default location ({defaultPath}).");
        Console.Write("Enter the full path to Revit.exe (or press Enter to skip launching it): ");
        var typed = Console.ReadLine()?.Trim().Trim('"');
        if (string.IsNullOrEmpty(typed)) return null;

        if (File.Exists(typed)) return typed;

        Console.WriteLine($"Couldn't find a Revit.exe at \"{typed}\" — skipping the launch.");
        Console.WriteLine("The add-in is installed either way; open Revit 2025 yourself when you're ready.");
        return null;
    }

    private static void LaunchRevit(string? revitExe)
    {
        if (revitExe == null)
        {
            Console.WriteLine("Done. Open Revit 2025 yourself — look for the \"Sportify\" tab in the ribbon.");
            Console.WriteLine("(If Revit was already open, restart it so it picks up the new add-in.)");
            return;
        }

        Console.WriteLine("Launching Revit 2025...");
        Console.WriteLine("Revit may ask whether to load the \"Sportify Roof Push\" add-in — choose Always Load.");
        Console.WriteLine("Once it's loaded, the Sportify web app opens automatically in a docked pane.");
        try
        {
            Process.Start(new ProcessStartInfo(revitExe) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Couldn't launch Revit automatically: {ex.Message}");
            Console.WriteLine("Open Revit 2025 yourself instead — the add-in is installed either way.");
        }
    }

    private static void CopyDirectory(string sourceDir, string destDir)
    {
        foreach (var dirPath in Directory.GetDirectories(sourceDir, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(dirPath.Replace(sourceDir, destDir));

        foreach (var filePath in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
            File.Copy(filePath, filePath.Replace(sourceDir, destDir), overwrite: true);
    }

    /// <summary>
    /// Same shape as the manifest this project has used all along
    /// (AddInId/VendorId included verbatim) — only Assembly's path is
    /// generated, since that's the one field that has to match wherever
    /// this specific machine's install actually landed.
    /// </summary>
    private static void WriteAddinManifest(string manifestPath, string dllPath)
    {
        var xml = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <RevitAddIns>
              <AddIn Type="Application">
                <Name>Sportify Roof Push</Name>
                <Assembly>{dllPath}</Assembly>
                <AddInId>82eddc84-f3e0-48d3-98ca-74276a24df8b</AddInId>
                <FullClassName>SportfyRevit.SportfyRevitApp</FullClassName>
                <VendorId>SPRT</VendorId>
                <VendorDescription>Sportify (school project)</VendorDescription>
              </AddIn>
            </RevitAddIns>
            """;
        File.WriteAllText(manifestPath, xml);
    }

    /// <summary>
    /// Needed for the "Open Sportify App" dockable pane. Present on most
    /// Windows 10/11 machines already (it ships with Edge) — this only
    /// matters on the machines where it isn't. Never downloads anything
    /// without asking first: worst case, the professor sees a message and
    /// a link instead of an add-in feature silently failing later.
    /// </summary>
    private static void CheckWebView2Runtime()
    {
        string? version = null;
        try { version = CoreWebView2Environment.GetAvailableBrowserVersionString(); }
        catch (WebView2RuntimeNotFoundException) { /* handled below */ }

        if (!string.IsNullOrEmpty(version))
        {
            Console.WriteLine($"WebView2 Runtime: present ({version}) — the in-Revit browser pane will work.");
            return;
        }

        Console.WriteLine("WebView2 Runtime: NOT found.");
        Console.WriteLine("This is only needed for the \"Open Sportify App\" in-Revit browser pane —");
        Console.WriteLine("everything else (Analysis, Deliverables, DXF import) works without it.");
        Console.Write("Open the official Microsoft download page now? [y/N]: ");
        var answer = Console.ReadLine();
        if (string.Equals(answer?.Trim(), "y", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                Process.Start(new ProcessStartInfo("https://developer.microsoft.com/microsoft-edge/webview2/") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Couldn't open the browser automatically: {ex.Message}");
                Console.WriteLine("Visit https://developer.microsoft.com/microsoft-edge/webview2/ manually instead.");
            }
        }
    }

    private static int Fail()
    {
        Console.WriteLine();
        Console.WriteLine("Installation did not complete. Press any key to exit.");
        WaitForKey();
        return 1;
    }

    private static int Success()
    {
        Console.WriteLine();
        Console.WriteLine("Press any key to exit.");
        WaitForKey();
        return 0;
    }

    /// <summary>Waits for a key when a person is at the console; a scripted (silent) install has no keyboard and must just finish.</summary>
    private static void WaitForKey()
    {
        if (!Console.IsInputRedirected) Console.ReadKey();
    }
}
