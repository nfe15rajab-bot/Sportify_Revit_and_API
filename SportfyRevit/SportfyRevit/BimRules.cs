namespace SportfyRevit
{
    /// <summary>
    /// The rules of the BIM & Documentation panel that do not need Revit, so Tools/AddinCheck can test them: which workset a Sportify element belongs on, and which colour
    /// each kind of Sportify element or zone type gets in a view.
    /// </summary>
    internal static class BimRules
    {
        public const string SportsWorkset = "Sports";
        public const string GardensWorkset = "Gardens";
        public const string CombineWorkset = "Combine";
        public static readonly string[] WorksetNames = { SportsWorkset, GardensWorkset, CombineWorkset };

        public const string SportifyTypePrefix = "Sportify - ";

        public enum ElementKind { Floor, FamilyInstance, Other }

        /// <summary>
        /// The workset the import itself puts an element on (SportifyLayoutBuilder): ground (floors) and planting on Gardens, courts, activities and furniture on Sports, the
        /// roof outline, setback, circulation paths and entry markers (model lines, text) on Combine. So "Organize Multi-Worksets" puts an element back where the import put it.
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
                    return garden ? GardensWorkset : SportsWorkset;
                default:
                    return CombineWorkset;
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
