using System;
using System.Collections.Generic;
using System.Linq;

namespace SportfyRevit
{
    /// <summary>
    /// The Sportify worksets of a Revit model, organised exactly like the "Push to Sportify" drop-down: one workset per item (Roof, Structure, Entries, Openings, Edge, Drains, Equipment,
    /// Slab). An element in "Sportify Entries" is pushed by "Entries: stairs, lifts, doors", and so on, so a model sorted onto these worksets can be pushed by workset instead of by picking.
    /// Free of Revit types (tested in Sportify.Simulation/Tools/AddinCheck): PushWorksetAssigner turns a Revit element into a <see cref="PushKind"/> and this class says which workset it belongs in.
    ///
    /// The rules are by CATEGORY (and, where Revit has no category, by name), like the collectors' own: a roof or floor is the roof / slab, a stair, ramp, door or lift is an entry, grid lines,
    /// structural columns, beams, foundations and load-bearing walls are structure, shafts and roof windows are openings, railings and parapets are the edge, drains are found by their family
    /// names, mechanical and electrical equipment is equipment. Anything else belongs to no Sportify workset and is left alone.
    /// </summary>
    internal enum PushKind
    {
        Other,
        Roof, Floor,
        Grid, Column, Beam, Foundation, BearingWall,
        Stair, Ramp, Door, Lift,
        ShaftOpening, RoofWindow,
        Railing, ParapetWall,
        Drain,
        MechanicalEquipment, ElectricalEquipment,
    }

    internal static class PushWorksets
    {
        /// <summary>Every workset starts with this, so Sportify's are told from the designer's own.</summary>
        public const string Prefix = "Sportify ";

        /// <summary>The worksets in the order of the drop-down: the scope each one is pushed by, and its name.</summary>
        public static readonly (RoofPushScope Scope, string Name)[] All =
        {
            (RoofPushScope.Roof, Prefix + "Roof"),
            (RoofPushScope.Structure, Prefix + "Structure"),
            (RoofPushScope.Entries, Prefix + "Entries"),
            (RoofPushScope.Openings, Prefix + "Openings"),
            (RoofPushScope.Edge, Prefix + "Edge"),
            (RoofPushScope.Drains, Prefix + "Drains"),
            (RoofPushScope.Equipment, Prefix + "Equipment"),
            (RoofPushScope.SlabLevels, Prefix + "Slab"),
        };

        public static string[] Names => All.Select(w => w.Name).ToArray();

        public static string NameOf(RoofPushScope scope) => All.First(w => w.Scope == scope).Name;

        public static bool IsSportifyWorkset(string? name) => name != null && name.StartsWith(Prefix, StringComparison.Ordinal) && All.Any(w => w.Name == name);

        /// <summary>
        /// A workset Revit makes when worksharing is turned on (Workset1, "Shared Levels and Grids", and their German names): an element in one of them has not been sorted yet, so it may move to
        /// its Sportify workset without asking. Elements in any other workset were put there on purpose.
        /// </summary>
        public static bool IsDefaultWorksetName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return true;
            var n = name.Trim();
            if (System.Text.RegularExpressions.Regex.IsMatch(n, @"^(Workset|Arbeitssatz)\s*\d*$")) return true;
            return n is "Shared Levels and Grids" or "Shared Levels & Grids" or "Gemeinsam genutzte Ebenen und Raster";
        }

        /// <summary>The scope whose workset an element of this kind belongs in, or null when it belongs in none of them.</summary>
        public static RoofPushScope? ScopeOf(PushKind kind) => kind switch
        {
            PushKind.Roof => RoofPushScope.Roof,
            PushKind.Floor => RoofPushScope.SlabLevels,
            PushKind.Grid or PushKind.Column or PushKind.Beam or PushKind.Foundation or PushKind.BearingWall => RoofPushScope.Structure,
            PushKind.Stair or PushKind.Ramp or PushKind.Door or PushKind.Lift => RoofPushScope.Entries,
            PushKind.ShaftOpening or PushKind.RoofWindow => RoofPushScope.Openings,
            PushKind.Railing or PushKind.ParapetWall => RoofPushScope.Edge,
            PushKind.Drain => RoofPushScope.Drains,
            PushKind.MechanicalEquipment or PushKind.ElectricalEquipment => RoofPushScope.Equipment,
            _ => null,
        };

        public static string? WorksetFor(PushKind kind) => ScopeOf(kind) is { } s ? NameOf(s) : null;

        /// <summary>
        /// The kinds the "Select manually" pick offers for a push (what the drop-down item is about): the roof push takes a roof or a floor, the entries push stairs and ramps only, the structure
        /// push grid lines (and the columns, beams and bearing walls with them), and so on. The workset push of a scope takes the elements of these kinds that sit in the scope's workset.
        /// </summary>
        public static IReadOnlyList<PushKind> PickableKinds(RoofPushScope scope) => scope switch
        {
            RoofPushScope.Roof => new[] { PushKind.Roof, PushKind.Floor },
            RoofPushScope.Structure => new[] { PushKind.Grid, PushKind.Column, PushKind.Beam, PushKind.BearingWall, PushKind.Foundation },
            RoofPushScope.Entries => new[] { PushKind.Stair, PushKind.Ramp },
            RoofPushScope.Openings => new[] { PushKind.ShaftOpening, PushKind.RoofWindow },
            RoofPushScope.Edge => new[] { PushKind.Railing, PushKind.ParapetWall },
            RoofPushScope.Drains => new[] { PushKind.Drain },
            RoofPushScope.Equipment => new[] { PushKind.MechanicalEquipment, PushKind.ElectricalEquipment },
            RoofPushScope.SlabLevels => new[] { PushKind.Roof, PushKind.Floor },
            _ => Array.Empty<PushKind>(),
        };

        // ------------------------------------------------------------------ sorting a model onto the worksets

        /// <summary>One element as the assigner sees it: what it is, and the workset it is in now.</summary>
        internal sealed record Item(long Id, string Name, PushKind Kind, string CurrentWorkset, bool CurrentIsDefault);

        internal enum Verdict
        {
            /// <summary>Belongs in no Sportify workset: not touched.</summary>
            NotSportify,
            /// <summary>Already in the right workset.</summary>
            Right,
            /// <summary>In a default workset (Workset1, Shared Levels and Grids): it moves to its Sportify workset.</summary>
            Assign,
            /// <summary>In another workset (the designer's own, or another Sportify one): flagged, and only moved when the designer asks for that.</summary>
            Wrong,
        }

        internal sealed record Decision(Item Item, string? Target, Verdict Verdict);

        /// <summary>
        /// Decides, for every element, what its workset should be. An element in a default workset is assigned; one in another workset than the rule says is WRONG: it is flagged
        /// (and moved only when the designer asks, see <see cref="Moves"/>), because a designer's own worksets ("Architecture") are theirs and a Sportify workset holding the wrong kind of element is a mistake to see.
        /// </summary>
        public static List<Decision> Plan(IEnumerable<Item> items)
        {
            var result = new List<Decision>();
            foreach (var item in items)
            {
                var target = WorksetFor(item.Kind);
                if (target == null) { result.Add(new Decision(item, null, Verdict.NotSportify)); continue; }
                if (item.CurrentWorkset == target) { result.Add(new Decision(item, target, Verdict.Right)); continue; }
                result.Add(new Decision(item, target, item.CurrentIsDefault ? Verdict.Assign : Verdict.Wrong));
            }
            return result;
        }

        /// <summary>The decisions that move an element: every Assign, and every Wrong when the designer asked to fix them.</summary>
        public static IEnumerable<Decision> Moves(IEnumerable<Decision> plan, bool moveWrong) =>
            plan.Where(d => d.Verdict == Verdict.Assign || (moveWrong && d.Verdict == Verdict.Wrong));

        /// <summary>The wording of one flagged element: "Stair 'Stair 1' is in Architecture; it belongs in Sportify Entries".</summary>
        public static string Describe(Decision d) => $"{d.Item.Kind} \"{d.Item.Name}\" is in {d.Item.CurrentWorkset}; it belongs in {d.Target}";

        /// <summary>A push scope's worksets: the workset of the scope itself (the roof push also takes the slab workset, the slab push the roof one, since a floor may be the roof).</summary>
        public static IReadOnlyList<string> WorksetsToPush(RoofPushScope scope)
        {
            var names = new List<string>();
            foreach (var (s, name) in All)
                if (scope.HasFlag(s) && s != RoofPushScope.None) names.Add(name);
            if ((scope.HasFlag(RoofPushScope.Roof) || scope.HasFlag(RoofPushScope.SlabLevels)))
            {
                foreach (var n in new[] { NameOf(RoofPushScope.Roof), NameOf(RoofPushScope.SlabLevels) })
                    if (!names.Contains(n)) names.Add(n);
            }
            return names;
        }

        /// <summary>A name that is a drain by its words (Revit has no roof-drain category), the same words RoofFeatureCollector reads them by.</summary>
        public static bool LooksLikeDrain(string? name)
        {
            var n = (name ?? "").ToLowerInvariant();
            return DrainWords.Any(n.Contains);
        }

        /// <summary>A name that is a lift by its words.</summary>
        public static bool LooksLikeLift(string? name)
        {
            var n = (name ?? "").ToLowerInvariant();
            return LiftWords.Any(n.Contains);
        }

        /// <summary>A name that is a parapet by its words.</summary>
        public static bool LooksLikeParapet(string? name)
        {
            var n = (name ?? "").ToLowerInvariant();
            return ParapetWords.Any(n.Contains);
        }

        static readonly string[] DrainWords =
        {
            "drain", "abfluss", "gully", "gulli", "entw", "einlauf", "ablauf", "overflow", "notüberlauf", "notueberlauf", "überlauf", "ueberlauf", "scupper", "speier",
        };

        static readonly string[] LiftWords = { "lift", "aufzug", "elevator", "fahrstuhl" };

        static readonly string[] ParapetWords = { "parapet", "attika", "brüstung", "bruestung" };
    }
}
