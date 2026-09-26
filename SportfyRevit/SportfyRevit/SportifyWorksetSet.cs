using Autodesk.Revit.DB;

namespace SportfyRevit
{
    /// <summary>
    /// The Sportify workset set, organised like what an import brings: the existing roof base, the structure, the gardens, the sports, the furniture, and the
    /// dynamic furniture (what Kinetics places from the analyses). Worksets are found by name and made when they are missing, so a project that already has
    /// "Sportify Structure" (from Push to Sportify) reuses it. A project that is not workshared has none: everything stays on its one workset.
    /// </summary>
    internal static class SportifyWorksetSet
    {
        internal const string ExistingRoofBase = "Sportify Existing Roof Base";
        internal const string Structure = "Sportify Structure";
        internal const string Gardens = "Sportify Gardens";
        internal const string Sports = "Sportify Sports";
        internal const string Furniture = "Sportify Furniture";
        internal const string DynamicFurniture = "Sportify Dynamic Furniture";
        internal static readonly string[] All = { ExistingRoofBase, Structure, Gardens, Sports, Furniture, DynamicFurniture };

        /// <summary>The worksets by name, made when missing (inside a transaction on a workshared project); an unworkshared project gets InvalidWorksetId for every name.</summary>
        internal static Dictionary<string, WorksetId> Ensure(Document doc, IEnumerable<string>? names = null)
        {
            var wanted = (names ?? All).ToList();
            var result = new Dictionary<string, WorksetId>();
            if (!doc.IsWorkshared) { foreach (var n in wanted) result[n] = WorksetId.InvalidWorksetId; return result; }
            var existing = new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset).ToDictionary(w => w.Name, w => w.Id);
            foreach (var n in wanted)
                result[n] = existing.TryGetValue(n, out var id) ? id : Workset.Create(doc, n).Id;
            return result;
        }
    }
}
