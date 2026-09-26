using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace SportfyRevit
{
    /// <summary>
    /// The PROFILE: how a person wants Sportify to look and behave (view Simple or Advanced, role, theme), and who they are (a name and a photo). The web app keeps it in the browser
    /// and sends every change here (POST /profile); the add-in keeps it in <c>%APPDATA%\Sportify\settings.json</c> under "profile", so the ribbon and the web app read one copy, and writes it
    /// into the Profile folder of the Sportify folder as <c>Sportify-PROFILE.json</c> (and the photo as a picture), so it is one of the deliverables.
    ///
    /// What is stored is never what was received: <see cref="Sanitize"/> rebuilds the profile from the fields it knows, with the same limits as the web app's profileCore.js (Tools/ContractCheck
    /// tests both against the same hostile inputs). A photo is only ever a small JPEG, PNG or WebP data URL: no SVG (it can carry script), no address, nothing else.
    /// Revit-free on purpose: Tools/ContractCheck compiles this file.
    /// </summary>
    internal static class SportifyProfile
    {
        public const string KindKey = "profile";
        public const string FileName = "Sportify-PROFILE.json";
        public const string PhotoBaseName = "Sportify-PROFILE-photo";
        public const string FileKind = "sportify-profile";
        public const int MaxPhotoChars = 120000;
        public const int MaxPersonNameChars = 60;

        const int MaxTextChars = 60, MaxListItems = 24, MaxProfileNameChars = 40;

        static readonly Regex PhotoPattern = new(@"^data:image/(jpeg|png|webp);base64,[A-Za-z0-9+/]+={0,2}$", RegexOptions.CultureInvariant);
        static readonly string[] Views = { "simple", "advanced" };
        static readonly string[] Roles = { "planner", "client" };
        static readonly string[] Themes = { "dark", "light" };
        static readonly object Gate = new();
        static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        // ------------------------------------------------------------------------------------------------------------ what a profile may hold

        /// <summary>The profile rebuilt from what was received, or null when it is not a JSON object. Unknown fields are dropped, every value is checked and cut to size.</summary>
        public static JsonObject? Sanitize(JsonNode? raw)
        {
            if (raw is not JsonObject o) return null;
            var name = Trimmed(o["name"]);
            var profile = new JsonObject
            {
                ["schema"] = 1,
                ["name"] = string.IsNullOrEmpty(name) ? "PROFILE" : Cut(name, MaxProfileNameChars),
                ["updated"] = Iso(o["updated"]),
                ["view"] = OneOf(o["view"], Views) ?? "advanced",
                ["role"] = OneOf(o["role"], Roles) ?? "planner",
                ["theme"] = OneOf(o["theme"], Themes),
                ["quiz"] = Quiz(o["quiz"]),
                ["person"] = Person(o["person"]),
            };
            return profile;
        }

        static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

        static string? Trimmed(JsonNode? n) => Str(n)?.Trim();

        static string Cut(string s, int max) => s.Length > max ? s[..max] : s;

        static string? OneOf(JsonNode? n, string[] allowed) => Str(n) is { } s && allowed.Contains(s) ? s : null;

        static string? Text(JsonNode? n)
        {
            var s = Trimmed(n);
            return string.IsNullOrEmpty(s) ? null : Cut(s, MaxTextChars);
        }

        /// <summary>A time in the form the web app writes (2026-09-25T20:00:00.000Z), or null when it is not a time.</summary>
        static string? Iso(JsonNode? n)
        {
            var s = Str(n);
            if (s == null || !DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t)) return null;
            return t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        }

        static JsonArray List(JsonNode? n)
        {
            var list = new JsonArray();
            if (n is JsonArray a)
                foreach (var item in a.Select(Text).Where(t => t != null).Take(MaxListItems)) list.Add(item);
            return list;
        }

        /// <summary>The start-up quiz's answers: short texts and short lists; null when there is nothing.</summary>
        static JsonNode? Quiz(JsonNode? n)
        {
            if (n is not JsonObject q) return null;
            var goal = Text(q["goal"]);
            var experience = Text(q["experience"]);
            var analyses = List(q["analyses"]);
            var siteData = List(q["site_data"]);
            if (goal == null && experience == null && analyses.Count == 0 && siteData.Count == 0) return null;
            return new JsonObject { ["goal"] = goal, ["analyses"] = analyses, ["site_data"] = siteData, ["experience"] = experience, ["taken"] = Iso(q["taken"]) };
        }

        /// <summary>One clean line: control characters and runs of blanks become one space, at most 60 characters.</summary>
        public static string CleanPersonName(string? s)
        {
            if (s == null) return "";
            var line = Regex.Replace(new string(s.Select(c => char.IsControl(c) ? ' ' : c).ToArray()), @"\s+", " ").Trim();
            return Cut(line, MaxPersonNameChars);
        }

        /// <summary>Is this a photo the profile may keep: a small JPEG, PNG or WebP as a data URL, and nothing else?</summary>
        public static bool IsPhoto(string? s) => s != null && s.Length <= MaxPhotoChars && PhotoPattern.IsMatch(s);

        static JsonNode Person(JsonNode? n)
        {
            var o = n as JsonObject;
            var photo = Str(o?["photo"]);
            return new JsonObject { ["name"] = CleanPersonName(Str(o?["name"])), ["photo"] = IsPhoto(photo) ? photo : null };
        }

        // ------------------------------------------------------------------------------------------------------------ the settings file

        /// <summary>The profile the settings file holds, or null when there is none (or the file cannot be read).</summary>
        public static JsonObject? Read()
        {
            try
            {
                lock (Gate)
                {
                    if (!File.Exists(SportifyWorkspace.SettingsPath)) return null;
                    return (JsonNode.Parse(File.ReadAllText(SportifyWorkspace.SettingsPath)) as JsonObject)?["profile"] is JsonObject p ? Sanitize(p) : null;
                }
            }
            catch (Exception) { return null; }
        }

        /// <summary>Writes the profile into the settings file, keeping every other setting; the file is replaced whole, never left half written.</summary>
        public static void SaveToSettings(JsonObject profile)
        {
            lock (Gate)
            {
                var path = SportifyWorkspace.SettingsPath;
                JsonObject root;
                try { root = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new JsonObject() : new JsonObject(); }
                catch (Exception) { root = new JsonObject(); }      // an unreadable settings file: start afresh, as the installer's own write does
                root["profile"] = profile.DeepClone();
                WriteAtomically(path, root.ToJsonString(Indented));
            }
        }

        static void WriteAtomically(string path, string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            File.WriteAllText(temp, text, new UTF8Encoding(false));
            // Windows refuses to replace a file that something (a reader, a virus scanner) has open at that moment: a few short retries, then the error is real
            for (var attempt = 1; ; attempt++)
            {
                try { File.Move(temp, path, true); return; }
                catch (IOException) when (attempt < 4) { Thread.Sleep(40 * attempt); }
            }
        }

        // ------------------------------------------------------------------------------------------------------------ the Profile folder of the Sportify folder

        /// <summary>Where Sportify-PROFILE.json is (or would be) in the Sportify folder.</summary>
        public static string FilePath() => Path.Combine(SportifyWorkspace.PathFor(KindKey), FileName);

        /// <summary>
        /// Writes Sportify-PROFILE.json into the Profile folder (replacing the last one: it is always the profile in force) and the photo beside it as Sportify-PROFILE-photo.jpg / .png / .webp
        /// (removed when the profile has none). Never throws: a folder that cannot be written is reported, and the settings file already has the profile.
        /// </summary>
        public static (string? File, string? Photo, string? Error) WriteToFolder(JsonObject profile)
        {
            try
            {
                lock (Gate)      // one writer at a time: two browsers, or the ribbon and the app, saving together
                {
                    var dir = SportifyWorkspace.PathFor(KindKey);
                    var envelope = new JsonObject { ["kind"] = FileKind, ["profile"] = profile.DeepClone() };
                    var file = Path.Combine(dir, FileName);
                    WriteAtomically(file, envelope.ToJsonString(Indented));

                    foreach (var old in Directory.GetFiles(dir, PhotoBaseName + ".*")) File.Delete(old);
                    string? photoPath = null;
                    if (Str(profile["person"]?["photo"]) is { } photo && IsPhoto(photo))
                    {
                        var comma = photo.IndexOf(',');
                        var extension = photo.StartsWith("data:image/jpeg", StringComparison.Ordinal) ? "jpg" : photo.StartsWith("data:image/png", StringComparison.Ordinal) ? "png" : "webp";
                        photoPath = Path.Combine(dir, PhotoBaseName + "." + extension);
                        File.WriteAllBytes(photoPath, Convert.FromBase64String(photo[(comma + 1)..]));
                    }
                    return (file, photoPath, null);
                }
            }
            catch (Exception ex) { return (null, null, ex.Message); }
        }
    }
}
