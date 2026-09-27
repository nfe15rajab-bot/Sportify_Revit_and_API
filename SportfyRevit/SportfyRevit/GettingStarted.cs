using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

namespace SportfyRevit
{
    internal enum StepState
    {
        /// <summary>It holds right now (in this Revit session and this project).</summary>
        Done,
        /// <summary>It does not hold now, but it was seen to hold before (the checklist remembers it in the settings file).</summary>
        DoneBefore,
        /// <summary>The first step that is neither: what to do now.</summary>
        Next,
        Later,
    }

    /// <summary>One step of the checklist. Action is the internal name of the ribbon button that does it (RibbonLayout), or null when it is done by hand (select the roof, place pieces in the web app).</summary>
    internal sealed record GettingStartedStep(string Id, string Title, string Hint, string? Action, string? ActionText);

    /// <summary>What is true right now. Everything here is known without asking Revit anything except Built, which the command reads from the project's import ledger.</summary>
    internal sealed record GettingStartedFacts(bool WebApp, bool Roof, bool Layout, bool Built, bool Analyses, bool Documents)
    {
        public static readonly GettingStartedFacts Nothing = new(false, false, false, false, false, false);

        public bool Holds(string stepId) => stepId switch
        {
            "web_app" => WebApp,
            "roof" => Roof,
            "layout" => Layout,
            "built" => Built,
            "analyses" => Analyses,
            "documents" => Documents,
            _ => false,
        };
    }

    internal sealed record GettingStartedRow(GettingStartedStep Step, StepState State);

    /// <summary>
    /// The "Getting started" checklist behind the ribbon button of the same name: the walk-through of the user guide (open the web app, give it the roof, design the layout, send it to
    /// Revit, run the analyses, take the documents) as six steps whose state comes from what is true (a web page asked for its session, a roof was pushed, a layout arrived, the project
    /// holds an import, results were published, the Sportify folder holds a report, schedule or diagram), never from what the person says they did.
    ///
    /// A step that holds is remembered in the settings file (<c>%APPDATA%\Sportify\settings.json</c>, key "getting_started"), so that in the next Revit session, or another project,
    /// it shows as done before instead of as not started. The checklist is offered, never forced: nothing opens it by itself, and nothing else reads what it remembers.
    /// Revit-free on purpose: Tools/ContractCheck compiles this file and tests every rule below.
    /// </summary>
    internal static class GettingStarted
    {
        public const string SettingsKey = "getting_started";

        public static readonly IReadOnlyList<GettingStartedStep> Steps = new[]
        {
            new GettingStartedStep("web_app", "Open the web app",
                "Decide in the web app, build in Revit. Open it here, docked in Revit (or from the Start menu: it works with Revit closed too). The first time it asks four short questions; the Overview has a one-minute tour.",
                "OpenSportifyApp", "Open the Sportify web app here"),
            new GettingStartedStep("roof", "Give it your roof",
                "Select the roof (or a floor slab standing for it) in the model, then Push to Sportify, Everything. The outline, structure, entries, openings and edges reach the web app.",
                null, null),
            new GettingStartedStep("layout", "Design the layout in the web app",
                "Site, Structure inputs, Sport, Garden and Combine: place the pieces on the roof by hand or with the algorithm. What you place is sent here as you go.",
                null, null),
            new GettingStartedStep("built", "Send the layout to Revit",
                "In Combine, Export Combined JSON, then Import Configuration here (or turn Auto Import on): the pieces are built as families on their own worksets.",
                "ImportSportifyLayout", "Import Configuration"),
            new GettingStartedStep("analyses", "Run the analyses",
                "Send All to Web App runs the physical analyses on your layout and puts every result in the web app's Results tab; what rests on an input nobody confirmed is marked PRELIMINARY.",
                "SendPhysicalAnalysisToWeb", "Send All to Web App"),
            new GettingStartedStep("documents", "Take the documents",
                "The analysis report, the schedules and the functional diagrams are made from the Data Export panel here or the web app's Documents tab; the Sportify folder holds them all.",
                "OpenSportifyFolder", "Open the Sportify folder"),
        };

        // ------------------------------------------------------------------------------------------------------------ the rules

        /// <summary>
        /// The state of each step: Done when it holds now, DoneBefore when it was remembered, Next for the first of the others, Later for the rest.
        /// `before` are the remembered step ids (unknown ones are ignored).
        /// </summary>
        public static IReadOnlyList<GettingStartedRow> Rows(GettingStartedFacts facts, IEnumerable<string>? before)
        {
            var remembered = new HashSet<string>(before ?? Array.Empty<string>(), StringComparer.Ordinal);
            var rows = new List<GettingStartedRow>();
            var nextGiven = false;
            foreach (var step in Steps)
            {
                StepState state;
                if (facts.Holds(step.Id)) state = StepState.Done;
                else if (remembered.Contains(step.Id)) state = StepState.DoneBefore;
                else if (!nextGiven) { state = StepState.Next; nextGiven = true; }
                else state = StepState.Later;
                rows.Add(new GettingStartedRow(step, state));
            }
            return rows;
        }

        public static GettingStartedRow? NextRow(IReadOnlyList<GettingStartedRow> rows) => rows.FirstOrDefault(r => r.State == StepState.Next);

        /// <summary>The line that says where the person stands.</summary>
        public static string Headline(IReadOnlyList<GettingStartedRow> rows)
        {
            var next = NextRow(rows);
            if (next != null)
            {
                var n = rows.ToList().IndexOf(next) + 1;
                return "Next, step " + n + ": " + next.Step.Title;
            }
            return rows.All(r => r.State == StepState.Done) ? "Every step is done" : "Every step is done, some of them earlier";
        }

        /// <summary>The list, one line a step: [x] holds now, [x] with "(before)" was seen to hold before, [>] is next, [ ] is later.</summary>
        public static string Checklist(IReadOnlyList<GettingStartedRow> rows)
        {
            var sb = new StringBuilder();
            for (var i = 0; i < rows.Count; i++)
            {
                var r = rows[i];
                var mark = r.State switch { StepState.Done => "[x]", StepState.DoneBefore => "[x]", StepState.Next => "[>]", _ => "[  ]" };
                sb.Append(mark).Append("  ").Append(i + 1).Append(". ").Append(r.Step.Title);
                if (r.State == StepState.DoneBefore) sb.Append("  (before)");
                if (i < rows.Count - 1) sb.AppendLine();
            }
            return sb.ToString();
        }

        /// <summary>The hint of the next step, or a closing sentence when there is none.</summary>
        public static string Hint(IReadOnlyList<GettingStartedRow> rows) =>
            NextRow(rows)?.Step.Hint ?? "Nothing is left to do here. Kinetics (moving shading) and the SOLIDWORKS assembly are next, when the analyses call for them: the user guide shows how.";

        /// <summary>Is there a report, a schedule or a diagram in the Sportify folder?</summary>
        public static bool DocumentsMade()
        {
            try { return SportifyWorkspace.List().Any(f => f.Kind is "reports" or "schedules" or "diagrams"); }
            catch (Exception) { return false; }
        }

        // ------------------------------------------------------------------------------------------------------------ what is remembered

        static readonly object Gate = new();
        // the resolver is given explicitly: a JsonNode holding values writes itself through the options, which must then be able to describe them (they are made read-only on first use)
        static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, TypeInfoResolver = new DefaultJsonTypeInfoResolver() };

        /// <summary>The steps seen to hold before, in the order of the steps: only the ones that exist, each once. Never throws (an unreadable settings file remembers nothing).</summary>
        public static IReadOnlyList<string> Remembered()
        {
            try
            {
                lock (Gate)
                {
                    if (!File.Exists(SportifyWorkspace.SettingsPath)) return Array.Empty<string>();
                    var root = JsonNode.Parse(File.ReadAllText(SportifyWorkspace.SettingsPath)) as JsonObject;
                    return Known(root?[SettingsKey]?["done"] as JsonArray);
                }
            }
            catch (Exception) { return Array.Empty<string>(); }
        }

        static IReadOnlyList<string> Known(JsonArray? ids)
        {
            var wanted = ids == null ? new HashSet<string?>() : ids.Select(n => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null).ToHashSet();
            return Steps.Select(s => s.Id).Where(wanted.Contains).ToList();
        }

        /// <summary>Adds the steps that hold to what is remembered (never removes one) and keeps every other setting. Best effort: a settings file that cannot be written is left as it was.</summary>
        public static void Remember(IEnumerable<string> holding)
        {
            try
            {
                lock (Gate)
                {
                    var now = Remembered().Union(holding).ToHashSet();
                    var ids = new JsonArray();
                    foreach (var s in Steps.Where(s => now.Contains(s.Id))) ids.Add(s.Id);
                    Write(new JsonObject { ["done"] = ids, ["updated"] = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture) });
                }
            }
            catch (Exception ex) { SportifyLog.Warn("getting-started", "what was done could not be remembered: " + ex.Message); }
        }

        /// <summary>Forgets what was remembered ("start again"); the other settings stay.</summary>
        public static void Forget()
        {
            try { lock (Gate) Write(null); }
            catch (Exception ex) { SportifyLog.Warn("getting-started", "what was remembered could not be forgotten: " + ex.Message); }
        }

        static void Write(JsonObject? value)
        {
            var path = SportifyWorkspace.SettingsPath;
            JsonObject root;
            try { root = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new JsonObject() : new JsonObject(); }
            catch (Exception) { root = new JsonObject(); }      // an unreadable settings file: start afresh, as the installer's own write does
            if (value == null) root.Remove(SettingsKey); else root[SettingsKey] = value;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            File.WriteAllText(temp, root.ToJsonString(Indented), new UTF8Encoding(false));
            for (var attempt = 1; ; attempt++)
            {
                try { File.Move(temp, path, true); return; }
                catch (IOException) when (attempt < 4) { Thread.Sleep(40 * attempt); }
            }
        }
    }
}
