using System.Text.Json;

namespace SportfyRevit
{
    /// <summary>
    /// The names of the files Sportify makes carry the name of the session and of the iteration they are for, so that a folder of reports, schedules, diagrams, charts and films can be told apart
    /// (the two iterations of one session, the several sessions of one project). The web app's Documents tab has the two fields; they travel in the layout ("session": { "name", "iteration" }),
    /// which the add-in always holds the latest of, so every command that makes a file reads them from there and needs nothing typed in Revit.
    ///
    /// "DIGITAL TOOLS AND METHODS 2 - Roof and Sports" + "Algorithmic" gives "DIGITAL TOOLS AND METHODS 2 - Roof and Sports - Algorithmic - Sportify_Analysis_Report_20260926_140000.pdf".
    /// With neither name the file name is what it always was. Revit-free on purpose: Tools/AddinCheck tests it (and Tools/ContractCheck the files the server makes with it).
    /// </summary>
    internal static class DeliverableNaming
    {
        public const int MaxSession = 60, MaxIteration = 40;
        public const string Separator = " - ";

        static readonly char[] Bad = { '\\', '/', ':', '*', '?', '"', '<', '>', '|' };

        /// <summary>A name as a part of a file name: no character Windows refuses (or a control character), spaces collapsed, no dots or spaces at the ends, cut to <paramref name="max"/>. Empty when nothing is left.</summary>
        public static string Clean(string? name, int max = MaxSession)
        {
            var chars = (name ?? "").Select(c => char.IsControl(c) || Bad.Contains(c) ? ' ' : c).ToArray();
            var s = string.Join(" ", new string(chars).Split(' ', StringSplitOptions.RemoveEmptyEntries));
            if (s.Length > max) s = s[..max].TrimEnd();
            return s.Trim(' ', '.');
        }

        /// <summary>"Session - Iteration - " (one of them when only one is given, nothing when neither is).</summary>
        public static string Prefix(string? session, string? iteration)
        {
            var parts = new[] { Clean(session, MaxSession), Clean(iteration, MaxIteration) }.Where(p => p.Length > 0);
            var joined = string.Join(Separator, parts);
            return joined.Length == 0 ? "" : joined + Separator;
        }

        public static string Named(string fileName, string? session, string? iteration) => Prefix(session, iteration) + fileName;

        /// <summary>The session's and the iteration's name out of a layout's JSON ("session": { "name": ..., "iteration": ... }); empty for what is not there or not readable.</summary>
        public static (string Session, string Iteration) FromLayoutJson(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return ("", "");
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("session", out var s) || s.ValueKind != JsonValueKind.Object) return ("", "");
                string Read(string key) => s.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? Clean(v.GetString(), key == "name" ? MaxSession : MaxIteration) : "";
                return (Read("name"), Read("iteration"));
            }
            catch (Exception) { return ("", ""); }
        }

        // What the web app last told the add-in (POST /session-names). Kept apart from the layout on purpose: a layout's identity is a hash of the text the app sends (LayoutIdentity), and
        // naming the session must not make every result "about an earlier layout".
        static readonly object Gate = new();
        static string _session = "", _iteration = "";

        /// <summary>The names the web app sent (both empty clears them); returns them as they are kept, cleaned.</summary>
        public static (string Session, string Iteration) Set(string? session, string? iteration)
        {
            lock (Gate) { _session = Clean(session, MaxSession); _iteration = Clean(iteration, MaxIteration); return (_session, _iteration); }
        }

        public static (string Session, string Iteration) Stored() { lock (Gate) return (_session, _iteration); }

        /// <summary>The names for what is being made now: the ones the web app sent, else the ones the latest layout carries in its own JSON (a saved session that was loaded), else none.</summary>
        public static (string Session, string Iteration) Current()
        {
            var stored = Stored();
            if (stored.Session.Length > 0 || stored.Iteration.Length > 0) return stored;
            return RoofBoundaryServer.TryGetLatestCombinedLayout(out var json, out _) ? FromLayoutJson(json) : ("", "");
        }

        /// <summary>The names for a file made of this layout: its own if its JSON carries them, else <see cref="Current"/>.</summary>
        public static (string Session, string Iteration) Resolve(string? layoutJson)
        {
            var own = FromLayoutJson(layoutJson);
            return own.Session.Length > 0 || own.Iteration.Length > 0 ? own : Current();
        }

        /// <summary>The body of POST /session-names: { "name": ..., "iteration": ... }. Null when it is not that.</summary>
        public static (string Session, string Iteration)? ParseRequest(string? body)
        {
            if (string.IsNullOrWhiteSpace(body)) return null;
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
                string Read(string key) => doc.RootElement.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
                return (Read("name"), Read("iteration"));
            }
            catch (Exception) { return null; }
        }

        /// <summary>A file name with the current session and iteration in front of it.</summary>
        public static string Named(string fileName)
        {
            var (session, iteration) = Current();
            return Named(fileName, session, iteration);
        }

        /// <summary>
        /// The subfolder of a kind's folder that the current iteration's files go into (Analysis reports\Algorithmic): the iteration's name, cleaned, for a kind that is kept apart by iteration (SportifyWorkspace.PerIterationKinds);
        /// null when there is no iteration name (the files stay in the kind's folder as before) or the kind has none. With the layout that is being worked on, its names count as in Resolve.
        /// </summary>
        public static string? FolderFor(string kind, string? layoutJson = null)
        {
            if (!SportifyWorkspace.IsPerIteration(kind)) return null;
            var iteration = (layoutJson == null ? Current() : Resolve(layoutJson)).Iteration;
            var clean = Clean(iteration, MaxIteration);
            return clean.Length > 0 && SportifyWorkspace.SafeName(clean) == clean ? clean : null;
        }

        /// <summary>The file name pattern for "the newest file of this kind": the current session's and iteration's first, else any (a name is only what a person typed on top of the stem, the stem is what counts).</summary>
        public static string[] PatternsFor(string stem, string extension)
        {
            var (session, iteration) = Current();
            var prefix = Prefix(session, iteration);
            return prefix.Length > 0 ? new[] { prefix + stem + "*." + extension, "*" + stem + "*." + extension } : new[] { "*" + stem + "*." + extension };
        }
    }
}
