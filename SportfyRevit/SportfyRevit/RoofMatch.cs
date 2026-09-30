namespace SportfyRevit
{
    /// <summary>
    /// The Revit-free rules behind "which roof is this Sportify element on" and "which saved iteration belongs in this design option" (RoofIdentity,
    /// ImportLedger, DuplicateCleanup and the iterations import use them; Tools/AddinCheck tests them). All lengths in feet, Revit's internal unit.
    ///
    /// Why a roof is decided by where things stand and not only by the id an import was recorded with: a push used to accept Sportify's OWN floors
    /// as "the roof" (the gravel ballast or a green roof floor is what a click in 3D lands on first), and every import recreates those floors, so
    /// each push could key the next import to a new id. Each import then only replaced the elements recorded under its own id, and the earlier
    /// imports on the same slab stayed underneath (user, 2026-09-29). An import with no roof pushed in that Revit session was recorded as roof "0"
    /// and was never replaced by anything.
    /// </summary>
    internal static class RoofMatch
    {
        /// <summary>How far an element's underside may sit from the roof's top and still stand on it: trays lift a build-up ~0.2 m, a gravel layer
        /// pushed as "the roof" adds a few cm. The Goldbeck model's stacked slabs are 1.375 m apart, so this never reaches the next one.</summary>
        internal const double StandToleranceFt = 0.6 / 0.3048;

        /// <summary>How far outside the roof's outline box an element's middle may be (a piece on the parapet line).</summary>
        internal const double EdgeToleranceFt = 0.5 / 0.3048;

        /// <summary>Whether an element (its box: underside z, middle x/y) stands on a roof (its box in plan, its top z).</summary>
        internal static bool StandsOn(double elementMinZ, double elementCenterX, double elementCenterY,
                                      double roofMinX, double roofMinY, double roofMaxX, double roofMaxY, double roofTopZ)
        {
            if (Math.Abs(elementMinZ - roofTopZ) > StandToleranceFt) return false;
            return InPlan(elementCenterX, elementCenterY, roofMinX, roofMinY, roofMaxX, roofMaxY);
        }

        /// <summary>Whether a point (an element's middle) lies in a roof's outline box in plan, at any height.</summary>
        internal static bool InPlan(double x, double y, double roofMinX, double roofMinY, double roofMaxX, double roofMaxY) =>
            x >= roofMinX - EdgeToleranceFt && x <= roofMaxX + EdgeToleranceFt && y >= roofMinY - EdgeToleranceFt && y <= roofMaxY + EdgeToleranceFt;

        /// <summary>Whether a layout's roof (its outline's box in plan, model feet) is this building roof's (its box): each side within the edge
        /// tolerance. What names the roof when the layout's height names none (an older export with the roof at height 0).</summary>
        internal static bool SamePlanBox(double aMinX, double aMinY, double aMaxX, double aMaxY, double bMinX, double bMinY, double bMaxX, double bMaxY) =>
            Math.Abs(aMinX - bMinX) <= EdgeToleranceFt && Math.Abs(aMinY - bMinY) <= EdgeToleranceFt
            && Math.Abs(aMaxX - bMaxX) <= EdgeToleranceFt && Math.Abs(aMaxY - bMaxY) <= EdgeToleranceFt;

        /// <summary>
        /// The roof (index into `roofs`: their boxes in plan and tops, model feet) a layout whose height names none was made for, by its outline's box
        /// (SamePlanBox); -1 for none. Storeys can repeat an outline (the Goldbeck model's E9 and E10 share theirs, 1.375 m apart), so between several
        /// the layout's height above the ground decides (its top nearest it: the push measures it from the level nearest the project's zero), and
        /// without that height nothing is guessed.
        /// </summary>
        internal static int PickByOutline(double minX, double minY, double maxX, double maxY, double heightAboveGroundFt,
                                          IReadOnlyList<(double MinX, double MinY, double MaxX, double MaxY, double TopZ)> roofs)
        {
            var same = Enumerable.Range(0, roofs.Count)
                .Where(i => SamePlanBox(minX, minY, maxX, maxY, roofs[i].MinX, roofs[i].MinY, roofs[i].MaxX, roofs[i].MaxY)).ToList();
            if (same.Count <= 1) return same.Count == 1 ? same[0] : -1;
            if (heightAboveGroundFt <= 0) return -1;
            return same.OrderBy(i => Math.Abs(roofs[i].TopZ - heightAboveGroundFt)).First();
        }

        /// <summary>Whether a Sportify floor (its box) lies on a candidate building roof (its box): the roof's top within the tolerance under the
        /// floor's underside, and the two overlapping over at least half of the floor's own plan area.</summary>
        internal static bool LiesOn(double floorMinX, double floorMinY, double floorMaxX, double floorMaxY, double floorMinZ,
                                    double roofMinX, double roofMinY, double roofMaxX, double roofMaxY, double roofTopZ)
        {
            if (floorMinZ < roofTopZ - 0.15 / 0.3048 || floorMinZ - roofTopZ > StandToleranceFt) return false;
            var ox = Math.Min(floorMaxX, roofMaxX) - Math.Max(floorMinX, roofMinX);
            var oy = Math.Min(floorMaxY, roofMaxY) - Math.Max(floorMinY, roofMinY);
            if (ox <= 0 || oy <= 0) return false;
            var floorArea = Math.Max(1e-9, (floorMaxX - floorMinX) * (floorMaxY - floorMinY));
            return ox * oy >= 0.5 * floorArea;
        }

        /// <summary>A ledger key that names no roof: never pushed ("" before roofs were tracked, "0" when no roof was pushed that session) or not a number.
        /// A design option's own import ("option:…") is never an orphan: it is replaced only by importing into that option again.</summary>
        internal static bool IsUnnamedRoofKey(string? key) =>
            string.IsNullOrEmpty(key) || key == "0" || (!key.StartsWith(OptionKeyPrefix, StringComparison.Ordinal) && !long.TryParse(key, out _));

        internal const string OptionKeyPrefix = "option:";

        /// <summary>The ledger key of what was imported into a design option: replaced only by the next import into that same option.</summary>
        internal static string OptionKey(long designOptionId) => OptionKeyPrefix + designOptionId;

        // ---------------------------------------------------------------- design options <-> saved iterations

        /// <summary>
        /// Which saved iteration a design option is for, by name: "Option 1 planter" finds "iteration 1 planted", "Option 3 social" finds
        /// "interation 2 social" (the words after "option" and its number, compared on their first five letters, so planter/planted and
        /// garden/gardens meet). -1 when no iteration or more than one matches: then the person picks.
        /// </summary>
        internal static int IterationForOption(string? optionName, IReadOnlyList<string?> iterationNames)
        {
            var words = Words(optionName).Where(w => w != "option" && w != "primary" && w != "iteration" && w != "interation" && !w.All(char.IsDigit)).ToList();
            if (words.Count == 0) return -1;
            var hits = new List<int>();
            for (var i = 0; i < iterationNames.Count; i++)
            {
                var theirs = Words(iterationNames[i]).ToList();
                if (words.Any(w => theirs.Any(t => Stem(t) == Stem(w)))) hits.Add(i);
            }
            return hits.Count == 1 ? hits[0] : -1;
        }

        static string Stem(string w) => w.Length <= 5 ? w : w.Substring(0, 5);

        static IEnumerable<string> Words(string? text) =>
            new string((text ?? "").ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : ' ').ToArray())
                .Split(' ', StringSplitOptions.RemoveEmptyEntries);

        // ---------------------------------------------------------------- Revit's names

        /// <summary>The characters Revit refuses in the name of a type, a view or a design option ("name cannot include prohibited characters").</summary>
        internal static readonly char[] ProhibitedInNames = { '\\', ':', '{', '}', '[', ']', '|', ';', '<', '>', '?', '`', '~' };

        internal static bool IsValidRevitName(string? name) => !string.IsNullOrWhiteSpace(name) && name.IndexOfAny(ProhibitedInNames) < 0;
    }
}
