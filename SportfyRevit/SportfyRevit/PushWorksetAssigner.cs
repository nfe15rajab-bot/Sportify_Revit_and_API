using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI.Selection;

namespace SportfyRevit
{
    /// <summary>
    /// The Revit side of PushWorksets: tells what a model element IS (a <see cref="PushKind"/>), sorts the model onto the Sportify worksets (creating them), flags what sits in the wrong one,
    /// and reads back the elements of a scope's worksets for a push by workset. The rules themselves are in PushWorksets (Revit-free, tested).
    ///
    /// Sportify's own elements (what an import made: pieces with a Sportify_Category, floors of a "Sportify - ..." type) are never sorted here: they have their own worksets (Sports, Gardens, Combine,
    /// see "Organize Multi-Worksets"), and the roof pushed to the web app is the model's, not Sportify's.
    /// </summary>
    internal static class PushWorksetAssigner
    {
        /// <summary>The categories that can hold something the push reads (the scan of the model for the assigner).</summary>
        static readonly BuiltInCategory[] Scanned =
        {
            BuiltInCategory.OST_Roofs, BuiltInCategory.OST_Floors,
            BuiltInCategory.OST_Grids, BuiltInCategory.OST_StructuralColumns, BuiltInCategory.OST_StructuralFraming, BuiltInCategory.OST_StructuralFoundation, BuiltInCategory.OST_Walls,
            BuiltInCategory.OST_Stairs, BuiltInCategory.OST_Ramps, BuiltInCategory.OST_Doors,
            BuiltInCategory.OST_ShaftOpening, BuiltInCategory.OST_Windows, BuiltInCategory.OST_Railings,
            BuiltInCategory.OST_PlumbingFixtures, BuiltInCategory.OST_PipeAccessory, BuiltInCategory.OST_SpecialityEquipment, BuiltInCategory.OST_GenericModel,
            BuiltInCategory.OST_MechanicalEquipment, BuiltInCategory.OST_ElectricalEquipment,
        };

        static long Id(BuiltInCategory c) => (long)c;

        // ------------------------------------------------------------------ what an element is

        /// <summary>What a model element is for the push: by category, and by name where Revit has no category (lifts, drains, parapets).</summary>
        public static PushKind KindOf(Element e)
        {
            var cat = e.Category?.Id.Value;
            if (cat == null) return PushKind.Other;
            if (IsSportifys(e)) return PushKind.Other;

            var name = NameOf(e);
            // by name first, in the categories Revit has no roof-drain or lift category for
            if (cat == Id(BuiltInCategory.OST_PlumbingFixtures) || cat == Id(BuiltInCategory.OST_PipeAccessory) || cat == Id(BuiltInCategory.OST_SpecialityEquipment)
                || cat == Id(BuiltInCategory.OST_GenericModel) || cat == Id(BuiltInCategory.OST_MechanicalEquipment))
            {
                if (PushWorksets.LooksLikeDrain(name)) return PushKind.Drain;
                if (cat == Id(BuiltInCategory.OST_SpecialityEquipment) || cat == Id(BuiltInCategory.OST_GenericModel))
                    return PushWorksets.LooksLikeLift(name) ? PushKind.Lift : PushKind.Other;
            }

            if (cat == Id(BuiltInCategory.OST_Roofs)) return PushKind.Roof;
            if (cat == Id(BuiltInCategory.OST_Floors)) return PushKind.Floor;
            if (cat == Id(BuiltInCategory.OST_Grids)) return PushKind.Grid;
            if (cat == Id(BuiltInCategory.OST_StructuralColumns)) return PushKind.Column;
            if (cat == Id(BuiltInCategory.OST_StructuralFraming)) return PushKind.Beam;
            if (cat == Id(BuiltInCategory.OST_StructuralFoundation)) return PushKind.Foundation;
            if (cat == Id(BuiltInCategory.OST_Stairs)) return PushKind.Stair;
            if (cat == Id(BuiltInCategory.OST_Ramps)) return PushKind.Ramp;
            if (cat == Id(BuiltInCategory.OST_Doors)) return PushKind.Door;
            // Revit has no elevator category (OST_Elev is the ELEVATION VIEW marker, not a lift — a real project's elevation markers must never
            // be swept onto a Sportify workset): a lift is told by name, in Generic Model / Specialty Equipment, same as above.
            if (cat == Id(BuiltInCategory.OST_ShaftOpening)) return PushKind.ShaftOpening;
            if (cat == Id(BuiltInCategory.OST_Railings)) return PushKind.Railing;
            if (cat == Id(BuiltInCategory.OST_MechanicalEquipment)) return PushKind.MechanicalEquipment;
            if (cat == Id(BuiltInCategory.OST_ElectricalEquipment)) return PushKind.ElectricalEquipment;
            if (cat == Id(BuiltInCategory.OST_Windows)) return e is FamilyInstance fi && fi.Host is RoofBase ? PushKind.RoofWindow : PushKind.Other;
            if (cat == Id(BuiltInCategory.OST_Walls) && e is Wall wall)
            {
                if (IsBearing(wall)) return PushKind.BearingWall;
                return PushWorksets.LooksLikeParapet(name) ? PushKind.ParapetWall : PushKind.Other;
            }
            return PushKind.Other;
        }

        static bool IsBearing(Wall wall)
        {
            try
            {
                var significant = wall.get_Parameter(BuiltInParameter.WALL_STRUCTURAL_SIGNIFICANT);
                if (significant != null && significant.AsInteger() == 1) return true;
                return wall.StructuralUsage != StructuralWallUsage.NonBearing;
            }
            catch (Exception) { return false; }
        }

        /// <summary>The family and type name of an element together ("Basic Roof : Generic - 400mm"), what the name rules read.</summary>
        static string NameOf(Element e)
        {
            try
            {
                if (e is FamilyInstance fi && fi.Symbol != null) return fi.Symbol.FamilyName + " " + fi.Symbol.Name + " " + e.Name;
                return e.Name ?? "";
            }
            catch (Exception) { return ""; }
        }

        /// <summary>An element an import made: it has its own worksets and is not the model's.</summary>
        static bool IsSportifys(Element e)
        {
            if (e is Floor f && BimRules.IsSportifyTypeName(e.Document.GetElement(f.GetTypeId())?.Name)) return true;
            if (e is FamilyInstance && !string.IsNullOrWhiteSpace(SportifyElementScan.CategoryOf(e))) return true;
            return false;
        }

        // ------------------------------------------------------------------ the worksets of the model

        public static Dictionary<long, string> WorksetNames(Document doc) =>
            new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset).ToDictionary(w => (long)w.Id.IntegerValue, w => w.Name);

        /// <summary>The Sportify worksets that exist by name, creating the missing ones (inside a transaction). Names to ids.</summary>
        public static Dictionary<string, WorksetId> EnsureWorksets(Document doc, List<string> created)
        {
            var existing = new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset).ToDictionary(w => w.Name, w => w.Id);
            var result = new Dictionary<string, WorksetId>();
            foreach (var name in PushWorksets.Names)
            {
                if (existing.TryGetValue(name, out var id)) { result[name] = id; continue; }
                var made = Workset.Create(doc, name);
                result[name] = made.Id;
                created.Add(name);
            }
            return result;
        }

        // ------------------------------------------------------------------ sorting a model

        internal sealed class SortReport
        {
            public List<PushWorksets.Decision> Plan = new();
            public List<Element> Flagged = new();
            public List<string> Created = new();
            public Dictionary<string, int> Moved = new();
            public int Right, Refused, OwnedByOthers, Elements;
        }

        /// <summary>
        /// Looks at every element the push could read and works out what its workset should be. Inside a transaction when <paramref name="apply"/> is set: creates the worksets and moves the
        /// elements in a default workset (and, with <paramref name="moveWrong"/>, those in a wrong one). Elements in a wrong workset are flagged either way.
        /// </summary>
        public static SortReport Sort(Document doc, bool apply, bool moveWrong)
        {
            var report = new SortReport();
            var names = WorksetNames(doc);
            var byId = new Dictionary<long, Element>();
            var items = new List<PushWorksets.Item>();
            foreach (var category in Scanned)
                foreach (var e in new FilteredElementCollector(doc).OfCategory(category).WhereElementIsNotElementType())
                {
                    if (byId.ContainsKey(e.Id.Value)) continue;
                    var kind = KindOf(e);
                    var p = e.get_Parameter(BuiltInParameter.ELEM_PARTITION_PARAM);
                    if (p == null) continue;                                  // not a workshared element
                    byId[e.Id.Value] = e;
                    var current = names.TryGetValue(p.AsInteger(), out var n) ? n : "?";
                    items.Add(new PushWorksets.Item(e.Id.Value, NameOf(e).Trim(), kind, current, PushWorksets.IsDefaultWorksetName(current)));
                }
            report.Elements = items.Count;
            report.Plan = PushWorksets.Plan(items);
            report.Right = report.Plan.Count(d => d.Verdict == PushWorksets.Verdict.Right);
            report.Flagged = report.Plan.Where(d => d.Verdict == PushWorksets.Verdict.Wrong).Select(d => byId[d.Item.Id]).ToList();
            if (!apply) return report;

            var worksets = EnsureWorksets(doc, report.Created);
            foreach (var d in PushWorksets.Moves(report.Plan, moveWrong))
            {
                var el = byId[d.Item.Id];
                try
                {
                    if (WorksharingUtils.GetCheckoutStatus(doc, el.Id) == CheckoutStatus.OwnedByOtherUser) { report.OwnedByOthers++; continue; }
                    SportifyLayoutBuilder.SetWorkset(el, worksets[d.Target!]);
                    report.Moved[d.Target!] = report.Moved.GetValueOrDefault(d.Target!) + 1;
                }
                catch (Exception) { report.Refused++; }
            }
            return report;
        }

        // ------------------------------------------------------------------ pushing by workset

        /// <summary>The elements that sit in the given worksets (by name); worksets that do not exist give nothing.</summary>
        public static List<Element> ElementsIn(Document doc, IEnumerable<string> worksetNames)
        {
            var result = new List<Element>();
            if (!doc.IsWorkshared) return result;
            var wanted = new HashSet<string>(worksetNames);
            foreach (var w in new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset).Where(w => wanted.Contains(w.Name)))
                result.AddRange(new FilteredElementCollector(doc).WherePasses(new ElementWorksetFilter(w.Id)).WhereElementIsNotElementType());
            return result;
        }

        /// <summary>The elements of the workset that are not what the workset is for (a duct in "Sportify Structure"): flagged in the push's dialog.</summary>
        public static List<Element> Misplaced(Document doc, RoofPushScope scope)
        {
            var result = new List<Element>();
            foreach (var (s, name) in PushWorksets.All.Where(w => scope.HasFlag(w.Scope)))
                foreach (var e in ElementsIn(doc, new[] { name }))
                {
                    if (PushWorksets.ScopeOf(KindOf(e)) != s) result.Add(e);    // includes a Sportify-made element: it belongs on Sports/Gardens/Combine, not here either
                }
            return result;
        }

        // ------------------------------------------------------------------ picking

        /// <summary>The selection filter of "Select manually": only the kinds the drop-down item is about (entries: stairs and ramps only).</summary>
        internal sealed class KindSelectionFilter : ISelectionFilter
        {
            readonly HashSet<PushKind> _kinds;
            public KindSelectionFilter(IEnumerable<PushKind> kinds) { _kinds = new HashSet<PushKind>(kinds); }
            public bool AllowElement(Element elem) => _kinds.Contains(KindOf(elem));
            public bool AllowReference(Reference reference, XYZ position) => true;
        }
    }
}
