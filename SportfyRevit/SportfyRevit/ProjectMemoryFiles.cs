using System.IO;
using System.Security.Cryptography;
using System.Text;
using Autodesk.Revit.DB;

namespace SportfyRevit
{
    /// <summary>
    /// What a project remembers (its last imported layout and its pushed roofs, per roof), also kept on this computer: %APPDATA%\Sportify\project-memory\
    /// &lt;central model&gt;\. Keyed by the CENTRAL model for a workshared project, so every new local made from it knows them too (found 2026-10-01: opening
    /// the central with "Create New Local" replaced the local the memory had been saved in, and the analyses had nothing again); by the file for any
    /// other project. The copy inside the project (ImportedLayoutStore, PushedRoofStore) still travels to the team when it is synchronized.
    /// </summary>
    internal static class ProjectMemoryFiles
    {
        /// <summary>The folder for this project, null when it has no file yet.</summary>
        internal static string? Folder(Document doc)
        {
            string id = "";
            try { if (doc.IsWorkshared) id = ModelPathUtils.ConvertModelPathToUserVisiblePath(doc.GetWorksharingCentralModelPath()); } catch (Exception) { id = ""; }
            if (string.IsNullOrEmpty(id)) id = doc.PathName;
            if (string.IsNullOrEmpty(id)) return null;
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id.ToLowerInvariant()))).Substring(0, 8);
            var name = string.Concat(Path.GetFileNameWithoutExtension(id).Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Sportify", "project-memory", name + "-" + hash);
        }

        internal static void Write(Document doc, string kind, string roofKey, string json)
        {
            try
            {
                var folder = Folder(doc);
                if (folder == null) return;
                Directory.CreateDirectory(folder);
                File.WriteAllText(Path.Combine(folder, kind + "-" + Safe(roofKey) + ".json"), json, Encoding.UTF8);
            }
            catch (Exception ex) { SportifyLog.Warn("memory", $"the {kind} could not be kept on this computer: " + ex.Message); }
        }

        /// <summary>Every file of this kind for the project: (roof key, written at UTC, json), newest first.</summary>
        internal static List<(string RoofKey, DateTime WrittenUtc, string Json)> Read(Document doc, string kind)
        {
            var list = new List<(string, DateTime, string)>();
            try
            {
                var folder = Folder(doc);
                if (folder == null || !Directory.Exists(folder)) return list;
                foreach (var f in Directory.GetFiles(folder, kind + "-*.json"))
                {
                    var key = Path.GetFileNameWithoutExtension(f).Substring(kind.Length + 1);
                    list.Add((key, File.GetLastWriteTimeUtc(f), File.ReadAllText(f, Encoding.UTF8)));
                }
            }
            catch (Exception ex) { SportifyLog.Warn("memory", $"the {kind} kept on this computer could not be read: " + ex.Message); }
            return list.OrderByDescending(x => x.Item2).ToList();
        }

        static string Safe(string key) => string.IsNullOrEmpty(key) ? "0" : string.Concat(key.Select(c => char.IsLetterOrDigit(c) ? c : '_'));
    }
}
