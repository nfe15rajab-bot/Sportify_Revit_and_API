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

        /// <summary>Only this roof's own elements (Sportify_RoofId) are compared, so one pushed roof's placements never delete another's.</summary>
        internal static int RemoveForRoof(Document doc, string roofId)
        {
            var filter = new LogicalOrFilter(Categories.Select(c => (ElementFilter)new ElementCategoryFilter(c)).ToList());
            var candidates = new FilteredElementCollector(doc).WhereElementIsNotElementType().WherePasses(filter)
                .Where(el => !string.IsNullOrEmpty(el.LookupParameter("Sportify_Category")?.AsString())
                             && (el.LookupParameter("Sportify_RoofId")?.AsString() ?? "") == roofId)
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
