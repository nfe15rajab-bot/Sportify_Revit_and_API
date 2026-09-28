using Autodesk.Revit.DB;

namespace SportfyRevit
{
    /// <summary>
    /// The 6 canonical Sportify worksets — organised by what a person actually wants to isolate/hide: Sports, Gardens,
    /// Annotations and tags (circulation paths, entry markers, the roof outline/setback reference lines, text labels
    /// and Sportify's own tags — nothing here is "content", all of it is drawing-communication), Analysis (the
    /// AnalysisHatchViews.cs FilledRegions and their labels), Furniture, and Kinetic furniture (what Kinetics places
    /// from the analyses — this absorbs the kinetic units' own frame/bar/host elements too, previously a separate
    /// "Structure" workset: the frame is part of the kinetic assembly, not a distinct discipline in this scheme).
    /// Worksets are found by name and made when missing, so a project that already has "Sportify Sports" (from Push
    /// to Sportify) reuses it. A project that is not workshared has none: everything stays on its one workset.
    /// </summary>
    internal static class SportifyWorksetSet
    {
        internal const string Sports = "Sportify Sports";
        internal const string Gardens = "Sportify Gardens";
        internal const string AnnotationsAndTags = "Sportify Annotations and Tags";
        internal const string Analysis = "Sportify Analysis";
        internal const string Furniture = "Sportify Furniture";
        internal const string KineticFurniture = "Sportify Kinetic Furniture";
        internal static readonly string[] All = { Sports, Gardens, AnnotationsAndTags, Analysis, Furniture, KineticFurniture };

        /// <summary>Pre-existing (Goldbeck/IFC) content Sportify did not create — classified separately (IfcWorksetRules.cs/IfcWorksetService.cs, which creates it ad hoc, not through Ensure/All below). Not one of the 6 canonical worksets above: a different concern (what the model already had) from this set (what Sportify itself organises).</summary>
        internal const string ExistingRoofBase = "Sportify Existing Roof Base";

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
