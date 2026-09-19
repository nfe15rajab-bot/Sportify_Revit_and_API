using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sportify.Simulation.Structure;

namespace SportfyRevit
{
    /// <summary>What the designer chose in a command's result dialog.</summary>
    internal enum SummaryChoice { Close, Video, Review }

    /// <summary>What the designer decided about one assumption: entered their own value, accepted the built-in one, or neither yet.</summary>
    internal sealed class AssumptionDecision
    {
        public string Key = "";

        /// <summary>AnalysisAssumptions.Entered, .Accepted or .Unconfirmed.</summary>
        public string State = AnalysisAssumptions.Unconfirmed;

        /// <summary>For an entered value: a number as invariant text, or the key of the choice made.</summary>
        public string Value = "";
    }

    /// <summary>One assumption as the dialog shows it: its definition and where it currently stands.</summary>
    internal sealed class AssumptionRow
    {
        public AssumptionDef Def = null!;
        public AssumptionDecision Decision = new AssumptionDecision();
    }

    /// <summary>
    /// Reads which of the structural analyses' assumptions a layout export has the designer's word on (a value entered in the web
    /// app's Site tab, or a built-in value accepted there), and writes the designer's decisions from the Revit dialog back into the
    /// export before it goes to the analysis and to Unity. Revit-free on purpose, and tested that way.
    ///
    /// The values live where the analyses already read them (structure.deck_capacity_kn_m2, structure.natural_frequency_hz,
    /// site_conditions.snow_zone / altitude_m / day_schedule); the comfort limits and the accepted keys are in "analysis_assumptions".
    /// </summary>
    internal static class AnalysisAssumptionsPatcher
    {
        /// <summary>The inputs an analysis used, one per line, for a dialog: what the value was and whether the designer confirmed it.</summary>
        public static string DescribeInputs(IEnumerable<AssumptionUse> uses)
        {
            var lines = new List<string>();
            foreach (var u in uses)
            {
                var how = u.state == AnalysisAssumptions.Entered ? "entered"
                        : u.state == AnalysisAssumptions.Accepted ? "built-in, accepted"
                        : "built-in, NOT CONFIRMED";
                lines.Add($"  • {u.label}: {u.value.Replace("m2", "m²")} — {how}");
            }
            return string.Join("\n", lines);
        }

        static bool Uses(AssumptionDef d, string analysis)
        {
            return d.Analyses.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(analysis);
        }

        static double? Num(JsonNode? n)
        {
            return n is JsonValue v && v.TryGetValue<double>(out var d) && !double.IsNaN(d) ? d : null;
        }

        static string Str(JsonNode? n)
        {
            return n is JsonValue v && v.TryGetValue<string>(out var s) && s != null ? s.Trim() : "";
        }

        static string Invariant(double v) => v.ToString("R", CultureInfo.InvariantCulture);

        static JsonObject Obj(JsonObject parent, string name)
        {
            if (parent[name] is JsonObject o) return o;
            var made = new JsonObject();
            parent[name] = made;
            return made;
        }

        static JsonObject Root(string layoutJson)
        {
            return JsonNode.Parse(layoutJson) as JsonObject ?? throw new InvalidOperationException("The layout is not a JSON object.");
        }

        static string Write(JsonObject root)
        {
            return root.ToJsonString();
        }

        /// <summary>The value the layout carries for a key as the designer entered it ("" = none).</summary>
        static string EnteredValue(JsonObject root, string key)
        {
            var structure = root["structure"] as JsonObject;
            var site = root["site_conditions"] as JsonObject;
            var extra = root["analysis_assumptions"] as JsonObject;
            switch (key)
            {
                case AnalysisAssumptions.DeckCapacity: { var v = Num(structure?["deck_capacity_kn_m2"]); return v > 0 ? Invariant(v.Value) : ""; }
                case AnalysisAssumptions.NaturalFrequency: { var v = Num(structure?["natural_frequency_hz"]); return v > 0 ? Invariant(v.Value) : ""; }
                case AnalysisAssumptions.SnowZone: return Str(site?["snow_zone"]);
                case AnalysisAssumptions.Altitude: { var v = Num(site?["altitude_m"]); return v.HasValue ? Invariant(v.Value) : ""; }
                case AnalysisAssumptions.DaySchedule: return Str(site?["day_schedule"]);
                case AnalysisAssumptions.ComfortWalking: { var v = Num(extra?["comfort_limit_walking_g"]); return v > 0 ? Invariant(v.Value) : ""; }
                case AnalysisAssumptions.ComfortRhythmic: { var v = Num(extra?["comfort_limit_rhythmic_g"]); return v > 0 ? Invariant(v.Value) : ""; }
                default: return "";
            }
        }

        static HashSet<string> AcceptedKeys(JsonObject root)
        {
            var set = new HashSet<string>();
            if ((root["analysis_assumptions"] as JsonObject)?["accepted"] is JsonArray a)
                foreach (var n in a) { var s = Str(n); if (s != "") set.Add(s); }
            return set;
        }

        /// <summary>Every assumption the analysis uses, with where it stands in this layout: entered, accepted, or not confirmed.</summary>
        public static List<AssumptionRow> Read(string layoutJson, string analysis)
        {
            var root = Root(layoutJson);
            var accepted = AcceptedKeys(root);
            var rows = new List<AssumptionRow>();
            foreach (var def in AnalysisAssumptions.Editable.Where(d => Uses(d, analysis)))
            {
                var value = EnteredValue(root, def.Key);
                var decision = new AssumptionDecision { Key = def.Key };
                if (value != "") { decision.State = AnalysisAssumptions.Entered; decision.Value = value; }
                else if (accepted.Contains(def.Key)) decision.State = AnalysisAssumptions.Accepted;
                rows.Add(new AssumptionRow { Def = def, Decision = decision });
            }
            return rows;
        }

        public static bool AnyUnconfirmed(IEnumerable<AssumptionRow> rows)
        {
            return rows.Any(r => r.Decision.State == AnalysisAssumptions.Unconfirmed);
        }

        /// <summary>Checks what was typed for a number or picked for a choice. On success <paramref name="normalised"/> is the invariant text to store.</summary>
        public static bool TryParse(AssumptionDef def, string? text, out string normalised, out string error)
        {
            normalised = "";
            error = "";
            text = (text ?? "").Trim();
            if (def.Kind == "choice")
            {
                var hit = def.Choices.FirstOrDefault(c => string.Equals(c.Key, text, StringComparison.OrdinalIgnoreCase));
                if (hit == null) { error = $"{def.Label}: pick one of the choices."; return false; }
                normalised = hit.Key;
                return true;
            }

            if (!double.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || double.IsNaN(v) || double.IsInfinity(v))
            {
                error = $"{def.Label}: enter a number.";
                return false;
            }
            if (v < def.Min || v > def.Max)
            {
                error = $"{def.Label} must be between {Invariant(def.Min)} and {Invariant(def.Max)} {def.Unit}.".Replace("  ", " ");
                return false;
            }
            normalised = Invariant(v);
            return true;
        }

        /// <summary>
        /// The layout export with the decisions written in: an entered value goes where the analyses read it; an accepted or
        /// unconfirmed assumption has any value removed so the built-in one applies, and the accepted keys are listed.
        /// Decisions for keys this analysis does not use are still applied (the dialog shows only the ones it needs).
        /// </summary>
        public static string Apply(string layoutJson, IEnumerable<AssumptionDecision> decisions)
        {
            var root = Root(layoutJson);
            var accepted = AcceptedKeys(root);
            var extra = Obj(root, "analysis_assumptions");

            foreach (var d in decisions)
            {
                var entered = d.State == AnalysisAssumptions.Entered && d.Value != "";
                accepted.Remove(d.Key);
                if (d.State == AnalysisAssumptions.Accepted) accepted.Add(d.Key);

                double number = 0;
                if (entered && AnalysisAssumptions.Find(d.Key)?.Kind == "number")
                    double.TryParse(d.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out number);

                switch (d.Key)
                {
                    case AnalysisAssumptions.DeckCapacity: Obj(root, "structure")["deck_capacity_kn_m2"] = entered ? JsonValue.Create(number) : null; break;
                    case AnalysisAssumptions.NaturalFrequency: Obj(root, "structure")["natural_frequency_hz"] = entered ? JsonValue.Create(number) : null; break;
                    case AnalysisAssumptions.SnowZone: Obj(root, "site_conditions")["snow_zone"] = entered ? JsonValue.Create(d.Value) : null; break;
                    case AnalysisAssumptions.Altitude:
                        {
                            var site = Obj(root, "site_conditions");
                            site["altitude_m"] = entered ? JsonValue.Create(number) : null;
                            site["altitude_set"] = entered;
                            break;
                        }
                    case AnalysisAssumptions.DaySchedule: Obj(root, "site_conditions")["day_schedule"] = entered ? JsonValue.Create(d.Value) : null; break;
                    case AnalysisAssumptions.ComfortWalking: extra["comfort_limit_walking_g"] = entered ? JsonValue.Create(number) : null; break;
                    case AnalysisAssumptions.ComfortRhythmic: extra["comfort_limit_rhythmic_g"] = entered ? JsonValue.Create(number) : null; break;
                }
            }

            var list = new JsonArray();
            foreach (var key in AnalysisAssumptions.Editable.Select(e => e.Key).Where(accepted.Contains)) list.Add(key);
            extra["accepted"] = list;
            return Write(root);
        }
    }

    /// <summary>
    /// What was decided in the Revit dialog earlier in this Revit session, so a second analysis does not ask the same questions again.
    /// A decision made here is the designer's latest word and wins over what the layout carries for that key. It belongs to the
    /// layout it was made for (its roof size and site location): pushing a different project forgets it, so one project's deck
    /// capacity can never be applied silently to another.
    /// </summary>
    internal static class AssumptionsSession
    {
        static readonly Dictionary<string, AssumptionDecision> Decided = new Dictionary<string, AssumptionDecision>();
        static string _fingerprint = "";

        /// <summary>Identifies the project a layout belongs to: its roof size and where it is.</summary>
        public static string Fingerprint(string layoutJson)
        {
            try
            {
                if (JsonNode.Parse(layoutJson) is not JsonObject root) return "";
                var roof = root["roof_context"] as JsonObject;
                var site = root["site_location"] as JsonObject;
                string N(JsonNode? n, string f) => n is JsonValue v && v.TryGetValue<double>(out var d) ? d.ToString(f, CultureInfo.InvariantCulture) : "-";
                return $"{N(roof?["length_m"], "0.###")}x{N(roof?["width_m"], "0.###")}@{N(site?["latitude_deg"], "0.####")},{N(site?["longitude_deg"], "0.####")}";
            }
            catch (Exception)
            {
                return "";
            }
        }

        public static void Remember(string layoutJson, IEnumerable<AssumptionDecision> decisions)
        {
            var print = Fingerprint(layoutJson);
            if (print != _fingerprint) { Decided.Clear(); _fingerprint = print; }
            foreach (var d in decisions)
            {
                if (d.State == AnalysisAssumptions.Unconfirmed) Decided.Remove(d.Key);
                else Decided[d.Key] = new AssumptionDecision { Key = d.Key, State = d.State, Value = d.Value };
            }
        }

        public static void Forget()
        {
            Decided.Clear();
            _fingerprint = "";
        }

        /// <summary>The layout with this session's decisions written in, when they were made for this same project.</summary>
        public static string ApplyRemembered(string layoutJson)
        {
            if (Decided.Count == 0) return layoutJson;
            if (Fingerprint(layoutJson) != _fingerprint) { Forget(); return layoutJson; }
            return AnalysisAssumptionsPatcher.Apply(layoutJson, Decided.Values.ToList());
        }
    }
}
