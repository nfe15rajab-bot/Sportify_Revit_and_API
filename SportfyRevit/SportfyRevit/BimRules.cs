namespace SportfyRevit
{
    /// <summary>
    /// The rules of the BIM & Documentation panel that do not need Revit, so Tools/AddinCheck can test them: which workset a Sportify element belongs on, and which colour
    /// each kind of Sportify element or zone type gets in a view.
    /// </summary>
    internal static class BimRules
    {
        // Short keys into SportifyLayoutBuilder.EnsureWorksets' dictionary — not the real Revit workset names (those are
        // SportifyWorksetSet's "Sportify X" names; EnsureWorksets maps these short keys onto them, unless an iteration
        // override is in play, in which case they become the category token an iteration's own name is built from).
        // Only the 4 the main placement path itself writes to; Analysis and Kinetic Furniture are populated by other
        // commands (AnalysisHatchViews.cs, KineticsCommands.cs) but EnsureWorksets still creates all 6 up front (via
        // SportifyWorksetSet) so the full set exists consistently regardless of which command ran first.
        public const string SportsWorkset = "Sports";
        public const string GardensWorkset = "Gardens";
        public const string FurnitureWorkset = "Furniture";
        public const string AnnotationsAndTagsWorkset = "AnnotationsAndTags";
        public static readonly string[] WorksetNames = { SportsWorkset, GardensWorkset, FurnitureWorkset, AnnotationsAndTagsWorkset };

        public const string SportifyTypePrefix = "Sportify - ";

        /// <summary>Lines subcategories SportifyLayoutBuilder tags circulation-path and entry-marker model lines with, so the functional-diagram view can style them distinctly from the (untagged) roof outline — see GenerateFunctionalDiagramsCommand.StyleCirculationDiagram.</summary>
        public const string CirculationLineStyle = "Sportify Circulation";
        public const string EntryLineStyle = "Sportify Entry";
        public const string CirculationNodeLineStyle = "Sportify Circulation Node";
        /// <summary>The setback boundary (SportifyLayoutBuilder.CreateSetbackBoundary) used to be left with no line style at all — Revit's own
        /// default "Lines" category appearance for an unstyled model line (a stray-looking colour in most templates, and no way to find or hide it
        /// afterward). Its own named style now, hidden by default in Sportify's own views (StyleCirculationDiagram) the same way circulation/entry
        /// lines are found and styled there — it is a design-rule reference, not something a reader needs to see by default.</summary>
        public const string SetbackLineStyle = "Sportify Setback";

        public enum ElementKind { Floor, FamilyInstance, Other }

        /// <summary>
        /// The workset the import itself puts an element on (SportifyLayoutBuilder): ground (floors) and planting on Gardens, courts and activities on Sports, furniture
        /// pieces on Furniture, the roof outline, setback, circulation paths and entry markers (model lines, text) on Annotations and tags. So "Organize Multi-Worksets"
        /// puts an element back where the import put it.
        /// </summary>
        public static string WorksetFor(ElementKind kind, string? sportifyCategory, bool isPlanting)
        {
            switch (kind)
            {
                case ElementKind.Floor:
                    return GardensWorkset;
                case ElementKind.FamilyInstance:
                    var garden = isPlanting
                                 || string.Equals(sportifyCategory, "garden", StringComparison.OrdinalIgnoreCase)
                                 || string.Equals(sportifyCategory, "vegetation", StringComparison.OrdinalIgnoreCase);
                    if (garden) return GardensWorkset;
                    return string.Equals(sportifyCategory, "furniture", StringComparison.OrdinalIgnoreCase) ? FurnitureWorkset : SportsWorkset;
                default:
                    return AnnotationsAndTagsWorkset;
            }
        }

        /// <summary>A Sportify floor type: its name starts with "Sportify - " (SportifyFloorTypeBuilder names them so).</summary>
        public static bool IsSportifyTypeName(string? name) => name != null && name.StartsWith(SportifyTypePrefix, StringComparison.Ordinal);

        // ---------------------------------------------------------------- colours (R, G, B)

        /// <summary>Colours of the Sportify categories a piece can have (Sportify_Category), the same family of colours the web app draws them in. Unknown categories get a neutral grey.</summary>
        public static (byte R, byte G, byte B) ColorForCategory(string? category)
        {
            switch ((category ?? "").Trim().ToLowerInvariant())
            {
                case "field": case "sport": case "sports": return (52, 120, 190);
                case "activity": return (232, 140, 40);
                case "garden": return (96, 170, 84);
                case "vegetation": return (30, 110, 62);
                case "furniture": return (140, 120, 100);
                default: return (150, 150, 150);
            }
        }

        private static readonly (byte R, byte G, byte B)[] ZonePalette =
        {
            (110, 170, 90), (200, 170, 80), (90, 150, 170), (170, 110, 90), (130, 130, 190), (180, 140, 170), (120, 120, 100), (90, 170, 140),
        };

        /// <summary>The colour of the n-th zone type (build-up) in a project, in name order: the palette repeats after eight.</summary>
        public static (byte R, byte G, byte B) ColorForZoneType(int index) => ZonePalette[((index % ZonePalette.Length) + ZonePalette.Length) % ZonePalette.Length];

        public static int ZonePaletteSize => ZonePalette.Length;

        public const string ViewMarker = " - Sportify ";

        /// <summary>
        /// The name of the duplicate view that carries one group of Sportify filters: "Level 1" and "Zone Types" give "Level 1 - Sportify Zone Types". If the source is already one of these
        /// duplicates ("Level 1 - Sportify Piece Kinds") the marker and what follows are dropped first, so duplicates are never chained ("Level 1 - Sportify Piece Kinds - Sportify Zone Types").
        /// Revit does not accept { } [ ] | ; &lt; &gt; ? ` ~ in a view name, and the default 3D view is called "{3D}": the name loses them ("3D - Sportify Zone Types").
        /// </summary>
        public static string SportifyViewName(string sourceName, string group)
        {
            var root = sourceName;
            var at = root.IndexOf(ViewMarker, StringComparison.Ordinal);
            if (at > 0) root = root.Substring(0, at);
            return SafeName(root) + ViewMarker + group;
        }

        /// <summary>A name for a filter or schedule that Revit accepts (no { } [ ] | ; &lt; &gt; ? ` ~ \ : characters) and that stays readable.</summary>
        public static string SafeName(string name)
        {
            var bad = new HashSet<char>("{}[]|;<>?`~\\:");
            var chars = name.Select(c => bad.Contains(c) ? ' ' : c).ToArray();
            return string.Join(" ", new string(chars).Split(' ', StringSplitOptions.RemoveEmptyEntries));
        }
    }
}
