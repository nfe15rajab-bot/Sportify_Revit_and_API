using System.Diagnostics;
using System.Text.Json;

namespace Sportify.Installer;

/// <summary>Where an install puts things and keeps its record. Overridable so that the install and the uninstall can be tried on a scratch folder (Tools/InstallerCheck).</summary>
internal sealed record InstallLocations(string AddinsDir, string DataDir, string ManifestFileName = "SportfyRevit.addin")
{
    /// <summary>What the uninstaller reads: the list of every file the install copied.</summary>
    public string RecordPath => Path.Combine(DataDir, "install.json");
    /// <summary>A copy of the installer, kept so that Windows' Apps &amp; features can run it with --uninstall after the download folder is gone.</summary>
    public string UninstallerDir => Path.Combine(DataDir, "uninstall");
    public string AddinManifestPath => Path.Combine(AddinsDir, ManifestFileName);

    public static InstallLocations Default() => new(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Autodesk", "Revit", "Addins", "2025"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Sportify"));
}

/// <summary>What an install did: enough for the uninstaller to remove exactly that and nothing else.</summary>
internal sealed class InstallRecord
{
    public string Product { get; set; } = "Sportify for Revit 2025";
    public string Version { get; set; } = "";
    public DateTime InstalledAtUtc { get; set; }
    public string AddinsDir { get; set; } = "";
    /// <summary>Every file copied from the payload, relative to AddinsDir.</summary>
    public List<string> Files { get; set; } = new();
    /// <summary>The .addin manifest written (relative to AddinsDir).</summary>
    public string? AddinManifest { get; set; }
    /// <summary>Where the person's own files are kept. Never removed: it is theirs.</summary>
    public string? WorkspaceFolder { get; set; }
    public string? UninstallerPath { get; set; }

    public static InstallRecord? Read(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<InstallRecord>(File.ReadAllText(path)) : null; }
        catch (Exception) { return null; }
    }

    public void Write(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}

/// <summary>Copying the payload in, and recording what was copied.</summary>
internal static class Installation
{
    /// <summary>Copies the payload into the Addins folder, writes the .addin manifest and the install record. Returns the record.</summary>
    public static InstallRecord Install(string payloadDir, InstallLocations where, string mainAssemblyName, string version, Func<string, string> manifestXml)
    {
        Directory.CreateDirectory(where.AddinsDir);
        var files = new List<string>();
        foreach (var dir in Directory.GetDirectories(payloadDir, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(where.AddinsDir, Path.GetRelativePath(payloadDir, dir)));
        foreach (var file in Directory.GetFiles(payloadDir, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(payloadDir, file);
            File.Copy(file, Path.Combine(where.AddinsDir, relative), overwrite: true);
            files.Add(relative);
        }

        File.WriteAllText(where.AddinManifestPath, manifestXml(Path.Combine(where.AddinsDir, mainAssemblyName)));

        // A second install over the first keeps the earlier record's files that this payload no longer has, so that they are still removed by the uninstall.
        var earlier = InstallRecord.Read(where.RecordPath);
        var all = new SortedSet<string>(files, StringComparer.OrdinalIgnoreCase);
        if (earlier != null) foreach (var f in earlier.Files) all.Add(f);

        var record = new InstallRecord
        {
            Version = version,
            InstalledAtUtc = DateTime.UtcNow,
            AddinsDir = where.AddinsDir,
            Files = all.ToList(),
            AddinManifest = where.ManifestFileName,
            WorkspaceFolder = earlier?.WorkspaceFolder,
        };
        record.Write(where.RecordPath);
        return record;
    }
}

/// <summary>
/// Takes Sportify back out: the files the install copied, the manifest, the folders that are left empty, the record and the Apps &amp; features entry. It removes only what the
/// record lists (and refuses a path that leaves the Addins folder); another add-in in the same folder, the person's Sportify folder of layouts and reports, and Revit's own files
/// are not touched. Revit must be closed: it holds the DLLs.
/// </summary>
internal static class Uninstaller
{
    internal sealed class Result
    {
        public List<string> Removed { get; } = new();
        public List<string> Missing { get; } = new();
        public List<string> Failed { get; } = new();
        public List<string> Refused { get; } = new();
        public List<string> Kept { get; } = new();
        public string? Stopped { get; set; }
        public bool Ok => Failed.Count == 0 && Refused.Count == 0 && Stopped == null;
    }

    /// <summary>Whether Revit is running (its process holds the add-in's DLL). Replaced in tests.</summary>
    public static Func<bool> RevitIsRunning = () => Process.GetProcessesByName("Revit").Length > 0;

    /// <summary>Stops a Sportify API this add-in started (api\Sportify.Api.exe), which would keep its files locked. Replaced in tests.</summary>
    public static Action<string> StopApi = addinsDir =>
    {
        var exe = Path.GetFullPath(Path.Combine(addinsDir, "api", "Sportify.Api.exe"));
        foreach (var p in Process.GetProcessesByName("Sportify.Api"))
        {
            try { if (string.Equals(p.MainModule?.FileName, exe, StringComparison.OrdinalIgnoreCase)) { p.Kill(true); p.WaitForExit(5000); } }
            catch (Exception) { /* not ours, or already gone */ }
        }
    };

    /// <param name="purge">Also remove what the add-in made for itself: the logs, the settings file and the generated-families cache. Still never the person's Sportify folder.</param>
    /// <param name="dryRun">Say what would be removed; remove nothing.</param>
    public static Result Run(InstallLocations where, bool purge, bool dryRun)
    {
        var result = new Result();
        var record = InstallRecord.Read(where.RecordPath);
        if (record == null)
        {
            result.Stopped = "no install record at " + where.RecordPath + ": nothing this installer put there can be removed with certainty (it never guesses)";
            return result;
        }

        if (!dryRun && RevitIsRunning())
        {
            result.Stopped = "Revit is running and holds the add-in's files: close Revit, then run the uninstall again";
            return result;
        }
        if (!dryRun) StopApi(record.AddinsDir);

        var root = Path.GetFullPath(record.AddinsDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var dirs = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var planned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);      // what a dry run would have removed: a folder holding only those would go too

        void RemoveFile(string full, string shown)
        {
            if (!File.Exists(full)) { result.Missing.Add(shown); return; }
            if (dryRun) { result.Removed.Add(shown); planned.Add(full); return; }
            try { File.SetAttributes(full, FileAttributes.Normal); File.Delete(full); result.Removed.Add(shown); }
            catch (Exception ex) { result.Failed.Add(shown + " (" + ex.Message + ")"); }
        }

        foreach (var relative in record.Files.Append(record.AddinManifest ?? "").Where(f => !string.IsNullOrWhiteSpace(f)))
        {
            var full = Path.GetFullPath(Path.Combine(record.AddinsDir, relative));
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) { result.Refused.Add(relative + " (outside the Addins folder)"); continue; }
            RemoveFile(full, relative);
            for (var d = Path.GetDirectoryName(full); d != null && d.Length > root.Length - 1 && d.StartsWith(root, StringComparison.OrdinalIgnoreCase); d = Path.GetDirectoryName(d)) dirs.Add(d);
        }

        // folders the install made, deepest first, only when empty: a folder with something else in it is somebody else's
        foreach (var d in dirs.OrderByDescending(x => x.Length))
        {
            if (!Directory.Exists(d)) continue;
            if (Directory.EnumerateFileSystemEntries(d).Any(e => !planned.Contains(e))) { result.Kept.Add(Path.GetRelativePath(record.AddinsDir, d) + " (still has other files in it)"); continue; }
            if (dryRun) { result.Removed.Add(Path.GetRelativePath(record.AddinsDir, d) + Path.DirectorySeparatorChar); planned.Add(d); continue; }
            try { Directory.Delete(d); result.Removed.Add(Path.GetRelativePath(record.AddinsDir, d) + Path.DirectorySeparatorChar); }
            catch (Exception ex) { result.Failed.Add(Path.GetRelativePath(record.AddinsDir, d) + " (" + ex.Message + ")"); }
        }

        if (purge)
        {
            // what the add-in made for itself; the generated families are rebuilt on demand
            var families = Path.Combine(record.AddinsDir, "SportifyGeneratedFamilies");
            foreach (var target in new[] { Path.Combine(where.DataDir, "logs"), Path.Combine(where.DataDir, "settings.json"), Path.Combine(where.DataDir, "family-template.txt"), families })
            {
                var shown = target.StartsWith(where.DataDir, StringComparison.OrdinalIgnoreCase) ? Path.GetRelativePath(where.DataDir, target) + " (data)" : Path.GetFileName(target) + " (generated families)";
                if (Directory.Exists(target)) { if (dryRun) result.Removed.Add(shown); else try { Directory.Delete(target, true); result.Removed.Add(shown); } catch (Exception ex) { result.Failed.Add(shown + " (" + ex.Message + ")"); } }
                else if (File.Exists(target)) RemoveFile(target, shown);
            }
        }
        if (!string.IsNullOrEmpty(record.WorkspaceFolder)) result.Kept.Add(record.WorkspaceFolder + " (your Sportify folder: layouts, reports, videos; never removed)");

        // the record goes last, and only when everything else did: a failed uninstall can be run again
        if (!dryRun && result.Ok)
        {
            try { File.Delete(where.RecordPath); } catch (Exception) { /* nothing depends on it */ }
        }
        return result;
    }
}
