using System.IO;
using Microsoft.Win32;

namespace SportfyRevit
{
    /// <summary>
    /// The real look at this computer for <see cref="SportifyCapabilities"/>: it uses what the add-in already knows about each tool (UnityHeadlessRunner.TryLocate finds Unity's
    /// Editor and the Sportify.Simulation project; MechanicalTool finds SOLIDWORKS and the Sportify SOLIDWORKS tool) so there is one way to find each, not two. Installed by
    /// SportfyRevitApp.OnStartup. Nothing here starts a tool.
    /// </summary>
    internal static class CapabilityProbes
    {
        public static Capabilities Detect()
        {
            var unity = Unity(out var free);
            return new Capabilities(unity, free, SolidWorks(), Chrome());
        }

        static ToolStatus Unity(out bool projectFree)
        {
            projectFree = false;
            try
            {
                if (!UnityHeadlessRunner.TryLocate(out var install, out var problem))
                    return new ToolStatus(false, null, string.IsNullOrWhiteSpace(problem) ? "Unity, or the Sportify.Simulation project it renders with, was not found on this computer." : problem);
                projectFree = !UnityHeadlessRunner.IsProjectOpenInUnity(install!.ProjectDir);
                return new ToolStatus(true, install.ExePath, projectFree
                    ? "Unity was found: the 3D videos can be rendered."
                    : "Unity was found, but its Editor has the Sportify.Simulation project open (a project can only be open once): close it to render a video.");
            }
            catch (Exception ex) { return new ToolStatus(false, null, "Unity could not be looked for: " + ex.Message); }
        }

        static ToolStatus SolidWorks()
        {
            try
            {
                if (!MechanicalTool.SolidWorksInstalled()) return new ToolStatus(false, null, "SOLIDWORKS is not installed on this computer.");
                var tool = MechanicalTool.Locate(out var problem);
                return tool != null
                    ? new ToolStatus(true, tool, "SOLIDWORKS is installed and the Sportify SOLIDWORKS tool is there: dynamic units can be built as assemblies.")
                    : new ToolStatus(false, null, "SOLIDWORKS is installed, but the Sportify SOLIDWORKS tool (Sportify.Mechanical.exe) is not part of this installation. " + problem);
            }
            catch (Exception ex) { return new ToolStatus(false, null, "SOLIDWORKS could not be looked for: " + ex.Message); }
        }

        /// <summary>Chrome by the way Windows itself finds it (its App Paths entry), then by its usual folders.</summary>
        static ToolStatus Chrome()
        {
            try
            {
                foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
                {
                    using var key = hive.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\chrome.exe");
                    if (key?.GetValue(null) is string path && File.Exists(path)) return new ToolStatus(true, path, "Chrome was found: the web app opens in it.");
                }
                foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) })
                {
                    var path = Path.Combine(root, "Google", "Chrome", "Application", "chrome.exe");
                    if (File.Exists(path)) return new ToolStatus(true, path, "Chrome was found: the web app opens in it.");
                }
                return new ToolStatus(false, null, "Chrome was not found: the web app opens in your default browser.");
            }
            catch (Exception ex) { return new ToolStatus(false, null, "Chrome could not be looked for: " + ex.Message); }
        }
    }
}
