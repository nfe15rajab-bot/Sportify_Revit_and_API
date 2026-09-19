#nullable disable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Sportify.Simulation.Structure
{
    // ---------------------------------------------------------------------------------------
    //  The assumptions behind the structural analyses, in one place.
    //
    //  The static and dynamic structural analyses give numbers that look more certain than the values they are built on:
    //  the deck's capacity is a stand-in for the structural engineer's figure, the deck's frequency is estimated, the snow
    //  zone, the day's schedule and the comfort limits are the author's choices. This register lists every one of them
    //  with where it comes from (a standard, published guidance, or just a judgement) so that
    //
    //    - the web app (assumptions.js, kept identical to this list by Tools/StructuralCheck/assumptions-parity.js) and the Revit add-in's
    //      dialog can ask the designer to ENTER each value or explicitly ACCEPT the built-in one,
    //    - the reports and the videos can say which inputs are still unconfirmed defaults ("PRELIMINARY"), which the
    //      designer accepted, and which they entered.
    //
    //  An EDITABLE assumption is one the designer can replace. A FIXED one is a constant of the model, listed so that its
    //  source is visible; changing it is a change to the model, not to the project.
    //
    //  Like the models it uses nothing from UnityEngine (compiled into Unity, the add-in and the check tools).
    // ---------------------------------------------------------------------------------------

    /// <summary>Where a built-in value comes from.</summary>
    public static class AssumptionStatus
    {
        public const string Standard = "standard";        // a value from a named clause of a standard
        public const string Literature = "literature";    // published guidance or research, not a code
        public const string Assumed = "assumed";          // the author's judgement: no source, awaiting the team
        public const string Placeholder = "placeholder";  // stands in for a project figure that must be supplied
        public const string Estimated = "estimated";      // computed from the model, good to a stated accuracy

        public static string Describe(string status)
        {
            switch (status)
            {
                case Standard: return "from a standard";
                case Literature: return "from published guidance";
                case Placeholder: return "PLACEHOLDER for a project figure";
                case Estimated: return "ESTIMATED from the model";
                default: return "the author's assumption";
            }
        }
    }

    public sealed class AssumptionChoice
    {
        public string Key, Label;
    }

    public sealed class AssumptionDef
    {
        public string Key;
        public string Label;                 // "Deck capacity"
        public string ShortLabel;            // "deck capacity", for a banner
        public string Unit = "";
        public string Kind;                  // "number" | "choice" | "fixed"
        public string Analyses = "";         // "structural dynamic": which analyses use it
        public double Default = double.NaN;  // numbers: the built-in value (NaN = estimated by the model)
        public double Min, Max;              // numbers: the range a designer's value must be in
        public string DefaultKey = "";       // choices: the built-in choice
        public string DefaultText = "";      // how the built-in value reads: "8.0 kN/m2", "zone 2"
        public string Status;                // AssumptionStatus
        public string Reference = "";        // the standard or guidance
        public string Basis = "";            // why the built-in value is what it is
        public List<AssumptionChoice> Choices = new List<AssumptionChoice>();

        public bool Editable { get { return Kind != "fixed"; } }
    }

    /// <summary>One line of a report: an input, its value, and whether the designer confirmed it. Serialised by Unity's JsonUtility.</summary>
    [Serializable]
    public class AssumptionUse
    {
        public string key, label, value;
        public string state;       // entered | accepted | unconfirmed  (what the designer did; "fixed" for a constant of the model)
        public string status;      // standard | literature | assumed | placeholder | estimated  (where the built-in value comes from)
        public string reference;
    }

    public static class AnalysisAssumptions
    {
        public const string DeckCapacity = "deck_capacity";
        public const string NaturalFrequency = "natural_frequency";
        public const string SnowZone = "snow_zone";
        public const string Altitude = "altitude";
        public const string DaySchedule = "day_schedule";
        public const string ComfortWalking = "comfort_limit_walking";
        public const string ComfortRhythmic = "comfort_limit_rhythmic";

        public const double DefaultComfortWalkingG = 0.02;
        public const double DefaultComfortRhythmicG = 0.05;

        public const string Entered = "entered";
        public const string Accepted = "accepted";
        public const string Unconfirmed = "unconfirmed";

        static AssumptionChoice Choice(string key, string label) { return new AssumptionChoice { Key = key, Label = label }; }

        /// <summary>What the designer can enter or accept, in the order they are asked.</summary>
        public static readonly List<AssumptionDef> Editable = new List<AssumptionDef>
        {
            new AssumptionDef
            {
                Key = DeckCapacity, Label = "Deck capacity", ShortLabel = "deck capacity", Unit = "kN/m2", Kind = "number", Analyses = "structural dynamic",
                Default = 8.0, Min = 0.5, Max = 30.0, DefaultText = "8.0 kN/m2", Status = AssumptionStatus.Placeholder,
                Reference = "No code value: it is the structural engineer's figure for the deck (characteristic permanent + imposed load per m2).",
                Basis = "A stand-in, not the engineer's figure: 5.0 for sports use (DIN EN 1991-1-1/NA Table 6.1DE, category C4) plus 3.0 permanent. Every over-capacity result depends on it.",
            },
            new AssumptionDef
            {
                Key = NaturalFrequency, Label = "Deck's first natural frequency", ShortLabel = "natural frequency", Unit = "Hz", Kind = "number", Analyses = "dynamic",
                Default = double.NaN, Min = 0.5, Max = 40.0, DefaultText = "estimated from the spans", Status = AssumptionStatus.Estimated,
                Reference = "The first vertical mode from the engineer's structural model (DIN EN 1990/NA, vibration serviceability).",
                Basis = "Each bay as a simply supported strip along its long span, depth span/25, E 30 GPa, 3% damping. Good to about 25%, and the response is taken over that band.",
            },
            new AssumptionDef
            {
                Key = SnowZone, Label = "Snow load zone", ShortLabel = "snow zone", Kind = "choice", Analyses = "dynamic",
                DefaultKey = "2", DefaultText = "zone 2", Status = AssumptionStatus.Assumed,
                Reference = "DIN EN 1991-1-3/NA: the German snow load zone map (zones 1, 1a, 2, 2a, 3).",
                Basis = "Zone 2 is a mid-range stand-in: there is no lookup by address, so the site's zone must be read off the map.",
                Choices = new List<AssumptionChoice> { Choice("1", "Zone 1"), Choice("1a", "Zone 1a"), Choice("2", "Zone 2"), Choice("2a", "Zone 2a"), Choice("3", "Zone 3") },
            },
            new AssumptionDef
            {
                Key = Altitude, Label = "Site altitude", ShortLabel = "altitude", Unit = "m", Kind = "number", Analyses = "dynamic",
                Default = 100.0, Min = -10.0, Max = 2500.0, DefaultText = "100 m above sea level", Status = AssumptionStatus.Assumed,
                Reference = "DIN EN 1991-1-3/NA: the ground snow load rises with the altitude above sea level in every zone.",
                Basis = "100 m is a lowland stand-in. Altitude matters most in zones 2, 2a and 3.",
            },
            new AssumptionDef
            {
                Key = DaySchedule, Label = "Use over the day", ShortLabel = "day schedule", Kind = "choice", Analyses = "dynamic",
                DefaultKey = "sports_day", DefaultText = "school and club sports day", Status = AssumptionStatus.Assumed,
                Reference = "Not defined by any standard: the operator's booking plan is the right source.",
                Basis = "Three generic hour-by-hour schedules (share of players, seated spectators and garden visitors present); the author's.",
                Choices = new List<AssumptionChoice>
                {
                    Choice("sports_day", "School and club sports day"), Choice("event_day", "Evening event with full stands"), Choice("community_day", "Community garden day"),
                },
            },
            new AssumptionDef
            {
                Key = ComfortWalking, Label = "Comfort limit, walking", ShortLabel = "walking limit", Unit = "g", Kind = "number", Analyses = "dynamic",
                Default = DefaultComfortWalkingG, Min = 0.001, Max = 0.5, DefaultText = "0.02 g", Status = AssumptionStatus.Assumed,
                Reference = "No DIN table. Published guidance (SCI P354, CCIP-016, ISO 10137) ranges from about 0.5% g for offices upward with the use.",
                Basis = "0.02 g (2% g) is the lenient end, meant for an open roof deck where people walk about; a quiet space would need a lower limit.",
            },
            new AssumptionDef
            {
                Key = ComfortRhythmic, Label = "Comfort limit, rhythmic activity", ShortLabel = "rhythmic limit", Unit = "g", Kind = "number", Analyses = "dynamic",
                Default = DefaultComfortRhythmicG, Min = 0.001, Max = 0.5, DefaultText = "0.05 g", Status = AssumptionStatus.Assumed,
                Reference = "SCI P354 gives about 3% g for gymnasia; ISO 10137 and CCIP-016 give the framework. No DIN table.",
                Basis = "0.05 g (5% g) for court play and a jumping crowd, the author's choice within the published range.",
            },
        };

        static AssumptionDef Fixed(string key, string label, string text, string status, string reference, string analyses = "structural dynamic")
        {
            return new AssumptionDef { Key = key, Label = label, ShortLabel = label.ToLowerInvariant(), Kind = "fixed", DefaultText = text, Status = status, Reference = reference, Analyses = analyses };
        }

        /// <summary>Constants of the model, listed so their source is visible. Not per-project inputs.</summary>
        public static readonly List<AssumptionDef> FixedConstants = new List<AssumptionDef>
        {
            Fixed("court_live", "Imposed load, sports court", "5.0 kN/m2", AssumptionStatus.Standard, "DIN EN 1991-1-1/NA Table 6.1DE, category C4 (sport and dance floors)", "structural"),
            Fixed("activity_live", "Imposed load, play and assembly area", "4.0 kN/m2", AssumptionStatus.Assumed, "Within the C3 range of DIN EN 1991-1-1/NA (3 to 5 kN/m2): the value is the author's pick", "structural"),
            Fixed("accessible_live", "Imposed load, accessible garden and walkway", "3.0 kN/m2", AssumptionStatus.Assumed, "Author's pick: check the roof-terrace category of DIN EN 1991-1-1/NA Table 6.1DE", "structural"),
            Fixed("roof_live", "Imposed load, roof not accessible", "0.75 kN/m2", AssumptionStatus.Assumed, "Category H: the National Annex value is NOT verified", "structural"),
            Fixed("finishes", "Roof finishes and court floor build-up", "0.5 + 0.5 kN/m2", AssumptionStatus.Assumed, "Author's pick for waterproofing, insulation and a sports surface", "structural"),
            Fixed("person_mass", "Weight of a person", "90 kg", AssumptionStatus.Assumed, "The Sportify reference value (as in the Live Loads analysis)"),
            Fixed("psi", "Combination factors, crowd and snow", "0.7 and 0.5", AssumptionStatus.Standard, "DIN EN 1990/NA Table A.1.1: category C (assembly) 0.7, snow below NN+1000 m 0.5 (cross-checked in secondary sources, not the standard's text)", "dynamic"),
            Fixed("snow_shape", "Snow shape coefficient, flat roof", "0.8", AssumptionStatus.Standard, "DIN EN 1991-1-3 (mu1 for a flat roof); no drift, exposure and thermal coefficients taken as 1.0", "dynamic"),
            Fixed("cloudburst", "Cloudburst", "108 mm/h for 10 min", AssumptionStatus.Assumed, "The rain analysis's design storm; no KOSTRA lookup for the site", "dynamic"),
            Fixed("event_density", "Jumping crowd density", "0.25 people/m2 on courts and play areas", AssumptionStatus.Literature, "Bachmann and Ammann, Vibrations in Structures (crowd density for rhythmic activity)", "dynamic"),
            Fixed("harmonics", "Dynamic load factors of jumping and walking", "1.8, 1.29, 0.67 (jumping); 0.4, 0.1, 0.1 (walking)", AssumptionStatus.Literature, "Half-sine pulse train (Bachmann and Ammann); ISO 10137 for walking", "dynamic"),
            Fixed("sync", "Synchronisation of the crowd", "0 walking, 0.2 court play, 0.6 jumping event", AssumptionStatus.Assumed, "Author's choice: N people add as sync x N + (1 - sync) x sqrt(N)", "dynamic"),
            Fixed("deck_model", "Deck stiffness and damping", "concrete, E 30 GPa, depth span/25, damping 3%", AssumptionStatus.Assumed, "Reinforced-concrete slab strip; 3% is a usual value for a finished floor", "dynamic"),
        };

        public static AssumptionDef Find(string key)
        {
            foreach (var d in Editable) if (d.Key == key) return d;
            foreach (var d in FixedConstants) if (d.Key == key) return d;
            return null;
        }

        // ------------------------------------------------------------------ what a report says about an input

        /// <summary>
        /// A report line for an input: the value the analysis used, and whether the designer entered it, accepted the built-in one, or
        /// did neither. <paramref name="entered"/> is whether the layout carried a value; <paramref name="accepted"/> the keys the
        /// designer accepted the built-in value of.
        /// </summary>
        public static AssumptionUse Use(string key, bool entered, string valueText, ICollection<string> accepted)
        {
            var d = Find(key);
            return new AssumptionUse
            {
                key = key,
                label = d != null ? d.Label : key,
                value = valueText ?? "",
                state = entered ? Entered : (accepted != null && accepted.Contains(key) ? Accepted : Unconfirmed),
                status = d != null ? d.Status : AssumptionStatus.Assumed,
                reference = d != null ? d.Reference : "",
            };
        }

        public static bool IsPreliminary(IEnumerable<AssumptionUse> uses)
        {
            return uses != null && uses.Any(u => u.state == Unconfirmed);
        }

        static string ShortOf(AssumptionUse u, bool withValue = true)
        {
            var d = Find(u.key);
            return (d != null ? d.ShortLabel : u.label.ToLowerInvariant()) + (withValue ? " " + u.value : "");
        }

        /// <summary>The line for the top of a video and a dialog: which inputs are still built-in defaults nobody confirmed. Empty when none.</summary>
        public static string PreliminaryNote(IEnumerable<AssumptionUse> uses)
        {
            if (uses == null) return "";
            var open = uses.Where(u => u.state == Unconfirmed).Select(u => ShortOf(u, true)).ToList();
            if (open.Count == 0) return "";
            return "PRELIMINARY: not confirmed by the designer - " + string.Join("; ", open) + " (built-in values)";
        }

        /// <summary>The names of the inputs still unconfirmed, comma separated (no values). Empty when none.</summary>
        public static string PreliminaryNames(IEnumerable<AssumptionUse> uses)
        {
            return uses == null ? "" : string.Join(", ", uses.Where(u => u.state == Unconfirmed).Select(u => ShortOf(u, false)));
        }

        /// <summary>The same for a video's top strip: names only (the deck capacity with its value), so it fits two lines. Empty when nothing is open.</summary>
        public static string PreliminaryBanner(IEnumerable<AssumptionUse> uses)
        {
            if (uses == null) return "";
            var open = uses.Where(u => u.state == Unconfirmed).Select(u => u.key == DeckCapacity ? "deck capacity " + u.value : ShortOf(u, false)).ToList();
            if (open.Count == 0) return "";
            return "PRELIMINARY - built-in values the designer has not confirmed: " + string.Join("; ", open) + ". Not a structural verdict.";
        }

        /// <summary>A quieter line for the inputs the designer accepted as built-in values, for the summary of a video. Empty when none.</summary>
        public static string AcceptedNote(IEnumerable<AssumptionUse> uses)
        {
            if (uses == null) return "";
            var acc = uses.Where(u => u.state == Accepted).Select(u => ShortOf(u, true)).ToList();
            if (acc.Count == 0) return "";
            return "Built-in values accepted by the designer: " + string.Join("; ", acc);
        }

        /// <summary>The report's assumptions list gets one line per input.</summary>
        public static List<string> Lines(IEnumerable<AssumptionUse> uses)
        {
            var lines = new List<string>();
            foreach (var u in uses)
            {
                var how = u.state == Entered ? "entered by the designer"
                        : u.state == Accepted ? "built-in value, accepted by the designer (" + AssumptionStatus.Describe(u.status) + ")"
                        : "built-in value, NOT CONFIRMED (" + AssumptionStatus.Describe(u.status) + ")";
                lines.Add("Input - " + u.label + ": " + u.value + ", " + how + ". " + u.reference);
            }
            return lines;
        }

        // ------------------------------------------------------------------ the register as JSON (for the web app's parity check)

        /// <summary>The register as JSON text, so a script can check that the web app's copy (assumptions.js) is the same list.</summary>
        public static string ToJson()
        {
            var sb = new StringBuilder();
            sb.Append("{\"editable\":[");
            for (var i = 0; i < Editable.Count; i++) { if (i > 0) sb.Append(','); Append(sb, Editable[i]); }
            sb.Append("],\"fixed\":[");
            for (var i = 0; i < FixedConstants.Count; i++) { if (i > 0) sb.Append(','); Append(sb, FixedConstants[i]); }
            sb.Append("]}");
            return sb.ToString();
        }

        static void Append(StringBuilder sb, AssumptionDef d)
        {
            sb.Append("{\"key\":").Append(Q(d.Key)).Append(",\"label\":").Append(Q(d.Label)).Append(",\"shortLabel\":").Append(Q(d.ShortLabel)).Append(",\"kind\":").Append(Q(d.Kind)).Append(",\"status\":").Append(Q(d.Status));
            sb.Append(",\"unit\":").Append(Q(d.Unit)).Append(",\"analyses\":").Append(Q(d.Analyses)).Append(",\"defaultText\":").Append(Q(d.DefaultText));
            if (d.Kind == "number")
                sb.Append(",\"default\":").Append(double.IsNaN(d.Default) ? "null" : d.Default.ToString("R", CultureInfo.InvariantCulture))
                  .Append(",\"min\":").Append(d.Min.ToString("R", CultureInfo.InvariantCulture)).Append(",\"max\":").Append(d.Max.ToString("R", CultureInfo.InvariantCulture));
            if (d.Kind == "choice")
            {
                sb.Append(",\"defaultKey\":").Append(Q(d.DefaultKey)).Append(",\"choices\":[");
                for (var i = 0; i < d.Choices.Count; i++) { if (i > 0) sb.Append(','); sb.Append("{\"key\":").Append(Q(d.Choices[i].Key)).Append(",\"label\":").Append(Q(d.Choices[i].Label)).Append('}'); }
                sb.Append(']');
            }
            sb.Append(",\"reference\":").Append(Q(d.Reference)).Append(",\"basis\":").Append(Q(d.Basis)).Append('}');
        }

        static string Q(string s)
        {
            if (s == null) s = "";
            var sb = new StringBuilder("\"");
            foreach (var c in s)
            {
                if (c == '"') sb.Append("\\\"");
                else if (c == '\\') sb.Append("\\\\");
                else if (c < 32) sb.Append(' ');
                else sb.Append(c);
            }
            return sb.Append('"').ToString();
        }
    }
}
