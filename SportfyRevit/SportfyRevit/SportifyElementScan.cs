using Autodesk.Revit.DB;

namespace SportfyRevit
{
    /// <summary>
    /// Which elements of a project are Sportify's. The reliable answer is the import ledger (what the last import created, by UniqueId, saved in the project itself). Only when
    /// there is none (a project imported before the ledger existed, or a copy) does it fall back to what marks a Sportify element on its own: a family instance whose
    /// Sportify_Category is filled, a floor of a "Sportify - ..." type. It never goes by the Comments field: the import does not write it.
    /// </summary>
    internal static class SportifyElementScan
    {
        internal sealed record Found(List<Element> Elements, string Source)
        {
            public bool IsEmpty => Elements.Count == 0;
        }

        public static Found Find(Document doc)
        {
            var ledger = ImportLedger.ReadElements(doc)
                .Where(e => e is not ElementType && e is not Autodesk.Revit.DB.ExtensibleStorage.DataStorage)
                .ToList();
            if (ledger.Count > 0) return new Found(ledger, "the last Sportify import (its ledger)");

            var byMark = new List<Element>();
            foreach (var fi in new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>())
            {
                var p = fi.LookupParameter("Sportify_Category");
                if (p != null && p.HasValue && !string.IsNullOrWhiteSpace(p.AsString())) byMark.Add(fi);
            }
            foreach (var floor in new FilteredElementCollector(doc).OfClass(typeof(Floor)).Cast<Floor>())
            {
                if (doc.GetElement(floor.GetTypeId()) is ElementType type && BimRules.IsSportifyTypeName(type.Name)) byMark.Add(floor);
            }
            return new Found(byMark, byMark.Count > 0 ? "elements marked as Sportify's (Sportify_Category, \"Sportify - \" floor types)" : "none found");
        }

        /// <summary>The value of Sportify_Category on an element, or null.</summary>
        public static string? CategoryOf(Element el)
        {
            var p = el.LookupParameter("Sportify_Category");
            return p != null && p.HasValue ? p.AsString() : null;
        }

        public static BimRules.ElementKind KindOf(Element el) =>
            el is Floor ? BimRules.ElementKind.Floor : el is FamilyInstance ? BimRules.ElementKind.FamilyInstance : BimRules.ElementKind.Other;

        public static bool IsPlanting(Element el) => el.Category != null && el.Category.Id.Value == (long)BuiltInCategory.OST_Planting;
    }
}
