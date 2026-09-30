using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace SportfyRevit
{
    /// <summary>
    /// Finds Sportify-placed elements that stand for the same placement twice — most often a roof's leftovers from
    /// before an import tracked it (ImportLedger predates RoofId, and a project opened between add-in versions can
    /// carry elements no ledger entry ever covered), sitting right under a freshly reimported copy — and deletes all
    /// but the newest. Two elements are the same placement when they carry the same non-empty Sportify_QualityKey (a
    /// placement's own id from the web app, stable across reimports) on the same roof; one with no QualityKey (an
    /// older export, before that parameter existed) is instead compared by category, variant and a rounded location.
    /// Inside a transaction.
    /// </summary>
    internal static class DuplicateCleanup
    {
        // ~4.5 cm: independent imports of the same unchanged layout land within millimetres of each other; anything
        // farther apart is a different piece standing nearby, not a duplicate.
        private const double LocationToleranceFt = 0.15;

        private static readonly BuiltInCategory[] Categories =
        {
            BuiltInCategory.OST_GenericModel, BuiltInCategory.OST_Planting,
            BuiltInCategory.OST_Floors, BuiltInCategory.OST_StructuralFoundation,
        };

        /// <summary>
        /// Only this roof's own elements are compared, so one pushed roof's placements never delete another's: those recorded with its id
        /// (Sportify_RoofId), and — given the roof — those recorded with an id that names no live building roof ("0", "", one of Sportify's own
        /// floors that a push once took for the roof) that stand on it. Nothing inside a design option, and nothing "Import Iterations" built, is ever compared.
        /// </summary>
        internal static int RemoveForRoof(Document doc, string roofId, Element? roof = null)
        {
            var roofBox = roof?.get_BoundingBox(null);
            var liveRoofs = new Dictionary<string, bool>();
            bool Ours(Element el)
            {
                var id = el.LookupParameter("Sportify_RoofId")?.AsString() ?? "";
                if (id == roofId) return true;
                if (roofBox == null) return false;
                if (!liveRoofs.TryGetValue(id, out var live)) liveRoofs[id] = live = RoofIdentity.RoofOfKey(doc, id) != null;
                return !live && RoofIdentity.StandsOn(el, roofBox);
            }
            // What "Import Iterations" built is the same pieces ON PURPOSE, once per iteration, each on its own workset: never a duplicate (found
            // 2026-09-30: an auto import deleted 44 of them, the pergola columns and ping pong parts of all but the newest iteration).
            var iterations = IterationLedger.ReadUniqueIds(doc);
            var filter = new LogicalOrFilter(Categories.Select(c => (ElementFilter)new ElementCategoryFilter(c)).ToList());
            var candidates = new FilteredElementCollector(doc).WhereElementIsNotElementType().WherePasses(filter)
                .Where(el => el.DesignOption == null && !iterations.Contains(el.UniqueId) && !string.IsNullOrEmpty(el.LookupParameter("Sportify_Category")?.AsString()) && Ours(el))
                .ToList();
            if (candidates.Count < 2) return 0;

            var groups = new Dictionary<string, List<Element>>();
            foreach (var el in candidates)
            {
                var key = GroupKey(el);
                if (key == null) continue;
                if (!groups.TryGetValue(key, out var list)) groups[key] = list = new List<Element>();
                list.Add(el);
            }

            var toDelete = new List<ElementId>();
            foreach (var group in groups.Values)
            {
                if (group.Count < 2) continue;
                var keep = group.OrderByDescending(el => el.Id.Value).First();
                toDelete.AddRange(group.Where(el => el.Id != keep.Id).Select(el => el.Id));
            }
            if (toDelete.Count == 0) return 0;

            doc.Delete(toDelete);
            return toDelete.Count;
        }

        private static string? GroupKey(Element el)
        {
            var qualityKey = el.LookupParameter("Sportify_QualityKey")?.AsString();
            if (!string.IsNullOrEmpty(qualityKey)) return "qk:" + qualityKey;

            var bb = el.get_BoundingBox(null);
            if (bb == null) return null;
            var center = (bb.Min + bb.Max) * 0.5;
            var category = el.LookupParameter("Sportify_Category")?.AsString() ?? "";
            var variant = el.LookupParameter("Sportify_Variant")?.AsString() ?? "";
            var rx = System.Math.Round(center.X / LocationToleranceFt);
            var ry = System.Math.Round(center.Y / LocationToleranceFt);
            var rz = System.Math.Round(center.Z / LocationToleranceFt);
            return "loc:" + category + "|" + variant + "|" + rx + "|" + ry + "|" + rz;
        }
    }
}
