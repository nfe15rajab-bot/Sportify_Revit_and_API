using System.IO;
using System.Text.Json;

namespace SportfyRevit
{
    /// <summary>One kind of deliverable, and the subfolder of the workspace it lives in.</summary>
    internal sealed record WorkspaceKind(string Key, string Folder, string Title, string Hint);

    /// <summary>A file in the workspace, as the web app lists it.</summary>
    internal sealed class DeliverableFile
    {
        public string Kind = "";
        public string Name = "";
        public long Size;
        public DateTime ModifiedUtc;
    }

    /// <summary>
    /// The folder on the user's machine where everything Sportify makes goes: layouts, sport and garden data, the charts and results of the physical analyses, their videos,
    /// the analysis report, schedules and diagrams, one subfolder each. The installer creates it (default Documents\Sportify, or a folder the person chooses) and records
    /// the choice in %APPDATA%\Sportify\settings.json; the add-in reads that file, so both always agree, and makes any missing subfolder on first use. The web app writes
    /// and reads its files through the add-in's local server (WorkspaceEndpoints), so nothing has to be imported or exported by hand.
    /// Revit-free on purpose: the installer compiles this file too, and Tools/ContractCheck tests it.
    /// </summary>
    internal static class SportifyWorkspace
    {
        public static readonly WorkspaceKind[] Kinds =
        {
            new("layouts", "Layouts", "Layouts", "The layout as designed in Combine (JSON) and the roof image (PNG)."),
            new("sport", "Sport fields", "Sport fields", "Field data (JSON) and CAD outlines (DXF) from the Sport tab."),
            new("garden", "Garden", "Garden", "Garden parcels (JSON) from the Garden tab."),
            new("analysis", "Physical analysis", "Physical analysis", "The charts of the physical analyses as PDF (structure, dynamics, wind, rain, sun) and the sun-path chart (PNG)."),
            new("videos", "Videos", "3D videos", "The recordings Unity makes of the analyses."),
            new("reports", "Analysis reports", "Analysis reports", "The analysis report (PDF): every check that has been run, the schedule and the diagrams."),
            new("schedules", "Schedules", "Schedules", "Component schedules (CSV)."),
            new("diagrams", "Diagrams", "Diagrams", "Functional diagrams exported from Revit (PNG)."),
        };

        static string? _override;
        static string? _settingsOverride;

        public static string SettingsPath => _settingsOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Sportify", "settings.json");

        public static string DefaultFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Sportify");

        /// <summary>The workspace folder: the one in the settings file when there is one, else the default.</summary>
        public static string Folder
        {
            get
            {
                if (_override != null) return _override;
                try
                {
                    if (File.Exists(SettingsPath))
                    {
                        using var doc = JsonDocument.Parse(File.ReadAllText(SettingsPath));
                        if (doc.RootElement.TryGetProperty("workspace_folder", out var f) && f.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(f.GetString()))
                            return f.GetString()!;
                    }
                }
                catch (Exception) { /* an unreadable settings file: the default */ }
                return DefaultFolder;
            }
        }

        /// <summary>For tests: use this folder and ignore the settings file (null goes back to normal).</summary>
        public static void UseFolder(string? folder) { _override = folder; }

        /// <summary>For tests: read and write this settings file instead of the one in the user's AppData folder.</summary>
        public static void UseSettingsFile(string? path) { _settingsOverride = path; }

        /// <summary>Records the folder in the settings file (what the installer does), keeping any other settings.</summary>
        public static void SaveSetting(string folder)
        {
            var settings = new Dictionary<string, JsonElement>();
            try
            {
                if (File.Exists(SettingsPath))
                    foreach (var p in JsonDocument.Parse(File.ReadAllText(SettingsPath)).RootElement.EnumerateObject()) settings[p.Name] = p.Value.Clone();
            }
            catch (Exception) { /* start afresh */ }
            settings["workspace_folder"] = JsonDocument.Parse(JsonSerializer.Serialize(folder)).RootElement.Clone();
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        }

        /// <summary>Makes the workspace and every subfolder that is missing; returns the workspace folder.</summary>
        public static string EnsureCreated()
        {
            var root = Folder;
            Directory.CreateDirectory(root);
            foreach (var k in Kinds) Directory.CreateDirectory(Path.Combine(root, k.Folder));
            return root;
        }

        public static WorkspaceKind? KindOf(string? key) => Kinds.FirstOrDefault(k => string.Equals(k.Key, key, StringComparison.OrdinalIgnoreCase));

        /// <summary>The subfolder of a kind, made if it is missing. Throws for a kind that does not exist.</summary>
        public static string PathFor(string key)
        {
            var kind = KindOf(key) ?? throw new ArgumentException("There is no deliverable kind \"" + key + "\".");
            var dir = Path.Combine(Folder, kind.Folder);
            Directory.CreateDirectory(dir);
            return dir;
        }

        /// <summary>A file name that is only a name: no folders, nothing Windows refuses. Empty when nothing is left.</summary>
        public static string SafeName(string? name)
        {
            var bad = Path.GetInvalidFileNameChars();
            var s = new string((Path.GetFileName(name ?? "") ?? "").Where(c => !bad.Contains(c)).ToArray()).Trim().TrimEnd('.');
            return s.StartsWith(".") ? s.TrimStart('.') : s;
        }

        /// <summary>The path a new file of this name would take in the kind's folder: the name itself, or with a number when it exists (nothing is overwritten).</summary>
        public static string UniquePath(string key, string name)
        {
            var dir = PathFor(key);
            var safe = SafeName(name);
            if (safe.Length == 0) throw new ArgumentException("The file has no usable name.");
            var stem = Path.GetFileNameWithoutExtension(safe);
            var ext = Path.GetExtension(safe);
            var path = Path.Combine(dir, safe);
            for (var n = 2; File.Exists(path); n++) path = Path.Combine(dir, $"{stem} ({n}){ext}");
            return path;
        }

        /// <summary>Writes a file into a kind's folder (under a free name); returns the path.</summary>
        public static string Save(string key, string name, byte[] data)
        {
            var path = UniquePath(key, name);
            File.WriteAllBytes(path, data);
            return path;
        }

        /// <summary>
        /// Copies a file that was made somewhere else (Unity's Recordings folder) into the workspace and returns the copy's path, so the workspace holds what was made.
        /// When it cannot be copied the original path comes back: the result then still points at a file that exists.
        /// </summary>
        public static string Adopt(string key, string sourcePath)
        {
            try
            {
                if (string.IsNullOrEmpty(sourcePath) || !File.Exists(sourcePath)) return sourcePath;
                var dir = PathFor(key);
                if (string.Equals(Path.GetDirectoryName(Path.GetFullPath(sourcePath)), Path.GetFullPath(dir), StringComparison.OrdinalIgnoreCase)) return sourcePath;
                var target = UniquePath(key, Path.GetFileName(sourcePath));
                File.Copy(sourcePath, target);
                return target;
            }
            catch (Exception) { return sourcePath; }
        }

        /// <summary>Copies a file into the workspace under a name of your choosing (a timestamped one, say); returns the copy, or the original when it cannot be copied.</summary>
        public static string AdoptAs(string key, string sourcePath, string name)
        {
            try
            {
                if (string.IsNullOrEmpty(sourcePath) || !File.Exists(sourcePath)) return sourcePath;
                var target = UniquePath(key, name);
                File.Copy(sourcePath, target);
                return target;
            }
            catch (Exception) { return sourcePath; }
        }

        /// <summary>The file for a kind and name, when it is really in that kind's folder (a name with a folder or ".." in it is refused).</summary>
        public static bool TryResolve(string? key, string? name, out string path)
        {
            path = "";
            var kind = KindOf(key);
            if (kind == null || string.IsNullOrEmpty(name) || SafeName(name) != name) return false;
            var full = Path.GetFullPath(Path.Combine(Folder, kind.Folder, name));
            if (!string.Equals(Path.GetDirectoryName(full), Path.GetFullPath(Path.Combine(Folder, kind.Folder)), StringComparison.OrdinalIgnoreCase) || !File.Exists(full)) return false;
            path = full;
            return true;
        }

        /// <summary>Every file in every subfolder, newest first.</summary>
        public static List<DeliverableFile> List()
        {
            var files = new List<DeliverableFile>();
            var root = Folder;
            foreach (var k in Kinds)
            {
                var dir = Path.Combine(root, k.Folder);
                if (!Directory.Exists(dir)) continue;
                foreach (var f in new DirectoryInfo(dir).EnumerateFiles())
                    files.Add(new DeliverableFile { Kind = k.Key, Name = f.Name, Size = f.Length, ModifiedUtc = f.LastWriteTimeUtc });
            }
            return files.OrderByDescending(f => f.ModifiedUtc).ToList();
        }
    }
}
