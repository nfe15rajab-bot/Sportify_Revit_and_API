using Sportify.Installer;

// The install record and the uninstaller, on scratch folders: what the install copies is exactly what the uninstall removes, another add-in and the person's own folder are not
// touched, Revit running stops it, a locked file is reported and the record kept, a record that names a path outside the Addins folder is refused, and a dry run removes nothing.
int fails = 0;
void Check(string name, bool ok, string extra = "") { if (!ok) fails++; Console.WriteLine((ok ? "PASS  " : "FAIL  ") + name + (extra.Length > 0 ? "  " + extra : "")); }

var root = Path.Combine(Path.GetTempPath(), "sportify-installer-check-" + Guid.NewGuid().ToString("N").Substring(0, 8));
var payload = Path.Combine(root, "payload");
Directory.CreateDirectory(Path.Combine(payload, "web", "vendor", "leaflet"));
Directory.CreateDirectory(Path.Combine(payload, "api"));
Directory.CreateDirectory(Path.Combine(payload, "runtimes", "win-x64", "native"));
foreach (var f in new[] { "SportfyRevit.dll", "SportfyRevit.pdb", "web/index.html", "web/vendor/leaflet/leaflet.js", "api/Sportify.Api.exe", "runtimes/win-x64/native/WebView2Loader.dll" })
    File.WriteAllText(Path.Combine(payload, f), "x " + f);
string ManifestXml(string dll) => "<RevitAddIns><Assembly>" + dll + "</Assembly></RevitAddIns>";

Uninstaller.RevitIsRunning = () => false;
Uninstaller.StopApi = _ => { };

InstallLocations Fresh(string name)
{
    var w = new InstallLocations(Path.Combine(root, name, "Addins", "2025"), Path.Combine(root, name, "Sportify"));
    Directory.CreateDirectory(w.AddinsDir);
    // things that are not ours, in the same folder
    Directory.CreateDirectory(Path.Combine(w.AddinsDir, "OtherAddin"));
    File.WriteAllText(Path.Combine(w.AddinsDir, "OtherAddin.addin"), "<other/>");
    File.WriteAllText(Path.Combine(w.AddinsDir, "OtherAddin", "other.dll"), "other");
    return w;
}
string[] Tree(string dir) => Directory.Exists(dir) ? Directory.GetFileSystemEntries(dir, "*", SearchOption.AllDirectories).Select(p => Path.GetRelativePath(dir, p).Replace('\\', '/')).OrderBy(x => x, StringComparer.Ordinal).ToArray() : Array.Empty<string>();

try
{
    // ---- install, then uninstall: back to exactly what was there
    var w = Fresh("a");
    var workspace = Path.Combine(root, "a", "My Sportify Files");
    Directory.CreateDirectory(workspace); File.WriteAllText(Path.Combine(workspace, "layout.json"), "the person's own file");
    var before = Tree(w.AddinsDir);
    var record = Installation.Install(payload, w, "SportfyRevit.dll", "1.2.3", ManifestXml);
    record.WorkspaceFolder = workspace; record.Write(w.RecordPath);
    Check("the install copies every payload file, subfolders included, and writes the manifest and the record", record.Files.Count == 6 && File.Exists(Path.Combine(w.AddinsDir, "web", "vendor", "leaflet", "leaflet.js")) && File.Exists(w.AddinManifestPath) && File.Exists(w.RecordPath) && File.ReadAllText(w.AddinManifestPath).Contains(Path.Combine(w.AddinsDir, "SportfyRevit.dll")));
    var again = InstallRecord.Read(w.RecordPath)!;
    Check("the record reads back: version, the files, the manifest, the workspace", again.Version == "1.2.3" && again.Files.Count == 6 && again.AddinManifest == "SportfyRevit.addin" && again.WorkspaceFolder == workspace);

    var dry = Uninstaller.Run(w, purge: false, dryRun: true);
    Check("a dry run says what would go (the six files, the manifest, the folders it made, and the folders that would be empty then) and removes nothing", dry.Ok && dry.Removed.Count >= 7 && dry.Removed.Any(r => r.StartsWith("web") && r.EndsWith(Path.DirectorySeparatorChar.ToString())) && !dry.Kept.Any(k => k.Contains("still has")) && File.Exists(Path.Combine(w.AddinsDir, "SportfyRevit.dll")) && File.Exists(w.RecordPath));

    var gone = Uninstaller.Run(w, purge: false, dryRun: false);
    var after = Tree(w.AddinsDir);
    Check("the uninstall removes exactly what the install copied: the folder is as it was before", gone.Ok && after.SequenceEqual(before), string.Join(",", after.Except(before).Concat(before.Except(after))));
    Check("another add-in in the same folder is untouched", File.Exists(Path.Combine(w.AddinsDir, "OtherAddin.addin")) && File.Exists(Path.Combine(w.AddinsDir, "OtherAddin", "other.dll")));
    Check("the person's own Sportify folder is untouched and named as kept", File.Exists(Path.Combine(workspace, "layout.json")) && gone.Kept.Any(k => k.Contains(workspace)));
    Check("the record goes when everything went", !File.Exists(w.RecordPath));

    // ---- Revit running
    var w2 = Fresh("b");
    Installation.Install(payload, w2, "SportfyRevit.dll", "1.0.0", ManifestXml);
    Uninstaller.RevitIsRunning = () => true;
    var blocked = Uninstaller.Run(w2, false, false);
    Check("with Revit running the uninstall stops before removing anything, and says why", blocked.Stopped != null && blocked.Stopped.Contains("Revit") && File.Exists(Path.Combine(w2.AddinsDir, "SportfyRevit.dll")) && File.Exists(w2.RecordPath));
    var dryWhileRunning = Uninstaller.Run(w2, false, true);
    Check("...but a dry run is allowed (it changes nothing)", dryWhileRunning.Stopped == null && dryWhileRunning.Removed.Count > 0);
    Uninstaller.RevitIsRunning = () => false;

    // ---- a locked file
    using (var locked = new FileStream(Path.Combine(w2.AddinsDir, "SportfyRevit.dll"), FileMode.Open, FileAccess.Read, FileShare.None))
    {
        var partial = Uninstaller.Run(w2, false, false);
        Check("a file another program holds is reported as failed, the rest is removed, and the record stays so that the uninstall can be run again",
            !partial.Ok && partial.Failed.Any(f => f.StartsWith("SportfyRevit.dll")) && File.Exists(w2.RecordPath) && !File.Exists(Path.Combine(w2.AddinsDir, "web", "index.html")));
    }
    var retry = Uninstaller.Run(w2, false, false);
    Check("run again once the file is free, it finishes", retry.Ok && !File.Exists(Path.Combine(w2.AddinsDir, "SportfyRevit.dll")) && !File.Exists(w2.RecordPath));

    // ---- something else in a folder the install made
    var w3 = Fresh("c");
    Installation.Install(payload, w3, "SportfyRevit.dll", "1.0.0", ManifestXml);
    File.WriteAllText(Path.Combine(w3.AddinsDir, "web", "my-notes.txt"), "put here later by someone");
    var kept = Uninstaller.Run(w3, false, false);
    Check("a folder with a file the install did not put there is kept, with the file", kept.Ok && File.Exists(Path.Combine(w3.AddinsDir, "web", "my-notes.txt")) && kept.Kept.Any(k => k.StartsWith("web")) && !File.Exists(Path.Combine(w3.AddinsDir, "web", "index.html")));

    // ---- a record that reaches outside
    var w4 = Fresh("d");
    var outsideFile = Path.Combine(root, "d", "precious.txt");
    File.WriteAllText(outsideFile, "not ours");
    var rec = new InstallRecord { Version = "1", AddinsDir = w4.AddinsDir, Files = new List<string> { "../../precious.txt", "..\\..\\precious.txt", "ok.txt" } };
    File.WriteAllText(Path.Combine(w4.AddinsDir, "ok.txt"), "ours"); rec.Write(w4.RecordPath);
    var refused = Uninstaller.Run(w4, false, false);
    Check("a record that names a path outside the Addins folder is refused for that path (nothing outside is deleted), and the record stays", File.Exists(outsideFile) && refused.Refused.Count == 2 && !refused.Ok && File.Exists(w4.RecordPath));

    // ---- no record
    var w5 = Fresh("e");
    var none = Uninstaller.Run(w5, false, false);
    Check("no record: it removes nothing and says it does not guess", none.Stopped != null && none.Stopped.Contains("no install record") && File.Exists(Path.Combine(w5.AddinsDir, "OtherAddin.addin")));

    // ---- purge
    var w6 = Fresh("f");
    Installation.Install(payload, w6, "SportfyRevit.dll", "1.0.0", ManifestXml);
    Directory.CreateDirectory(Path.Combine(w6.DataDir, "logs")); File.WriteAllText(Path.Combine(w6.DataDir, "logs", "addin-x.log"), "log");
    File.WriteAllText(Path.Combine(w6.DataDir, "settings.json"), "{}");
    Directory.CreateDirectory(Path.Combine(w6.AddinsDir, "SportifyGeneratedFamilies")); File.WriteAllText(Path.Combine(w6.AddinsDir, "SportifyGeneratedFamilies", "a.rfa"), "rfa");
    var plain = Uninstaller.Run(w6, purge: false, dryRun: true);
    Check("without --purge the logs, settings and generated families are left", plain.Removed.All(r => !r.Contains("logs") && !r.Contains("settings") && !r.Contains("generated")));
    var purged = Uninstaller.Run(w6, purge: true, dryRun: false);
    Check("--purge removes them too", purged.Ok && !Directory.Exists(Path.Combine(w6.DataDir, "logs")) && !File.Exists(Path.Combine(w6.DataDir, "settings.json")) && !Directory.Exists(Path.Combine(w6.AddinsDir, "SportifyGeneratedFamilies")));

    // ---- installing twice
    var w7 = Fresh("g");
    Installation.Install(payload, w7, "SportfyRevit.dll", "1.0.0", ManifestXml);
    File.Delete(Path.Combine(payload, "SportfyRevit.pdb"));
    var second = Installation.Install(payload, w7, "SportfyRevit.dll", "1.0.1", ManifestXml);
    Check("an install over an earlier one still lists the earlier one's files, so a file a newer payload dropped is removed too", second.Files.Contains("SportfyRevit.pdb") && second.Version == "1.0.1");
    var twice = Uninstaller.Run(w7, false, false);
    Check("...and the uninstall removes it", twice.Ok && !File.Exists(Path.Combine(w7.AddinsDir, "SportfyRevit.pdb")) && !File.Exists(Path.Combine(w7.AddinsDir, "SportfyRevit.dll")));
}
finally
{
    try { Directory.Delete(root, true); } catch (IOException) { }
}

Console.WriteLine(fails == 0 ? "\nALL INSTALLER CHECKS PASSED" : "\n" + fails + " CHECK(S) FAILED");
return fails;
