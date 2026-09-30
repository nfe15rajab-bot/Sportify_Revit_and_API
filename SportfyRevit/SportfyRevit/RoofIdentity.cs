using Autodesk.Revit.DB;

namespace SportfyRevit
{
    /// <summary>
    /// Which roof of the building a Sportify element, a click or a layout belongs to (the rules are RoofMatch's, Revit-free). A roof here is one of
    /// the building's own roofs or floors (an IFC slab's DirectShape counts), never one of Sportify's floors: those are rebuilt by every import,
    /// so an id taken from one of them names nothing by the next import.
    /// </summary>
    internal static class RoofIdentity
    {
        /// <summary>Sportify made it: a floor of a "Sportify - …" type, anything carrying Sportify_Category or Sportify_RoofId.</summary>
        internal static bool IsSportifys(Document doc, Element? el)
        {
            if (el == null) return false;
            if (!string.IsNullOrWhiteSpace(el.LookupParameter("Sportify_Category")?.AsString())) return true;
            if (!string.IsNullOrWhiteSpace(el.LookupParameter("Sportify_RoofId")?.AsString())) return true;
            return el is Floor && doc.GetElement(el.GetTypeId()) is ElementType t && BimRules.IsSportifyTypeName(t.Name);
        }

        /// <summary>The building's roofs and floors (not Sportify's, not in a design option) — the candidates a Sportify element can stand on.</summary>
        internal static List<Element> BuildingRoofs(Document doc) =>
            new FilteredElementCollector(doc).WhereElementIsNotElementType()
                .WherePasses(new LogicalOrFilter(new ElementCategoryFilter(BuiltInCategory.OST_Roofs), new ElementCategoryFilter(BuiltInCategory.OST_Floors)))
                .Where(e => e.DesignOption == null && !IsSportifys(doc, e) && e.get_BoundingBox(null) != null)
                .ToList();

        /// <summary>
        /// The roof to push for what was clicked or selected: itself, or — when it is one of Sportify's own floors (the gravel ballast or a green roof
        /// floor is what a click in 3D lands on first) — the building's roof or floor it lies on. `note` says what was swapped, for the log and the
        /// person; null when nothing was.
        /// </summary>
        internal static Element Resolve(Document doc, Element picked, out string? note)
        {
            note = null;
            if (!IsSportifys(doc, picked)) return picked;
            var bb = picked.get_BoundingBox(null);
            if (bb == null) return picked;
            Element? best = null;
            double bestTop = double.MinValue;
            foreach (var roof in BuildingRoofs(doc))
            {
                var rb = roof.get_BoundingBox(null);
                if (!RoofMatch.LiesOn(bb.Min.X, bb.Min.Y, bb.Max.X, bb.Max.Y, bb.Min.Z, rb.Min.X, rb.Min.Y, rb.Max.X, rb.Max.Y, rb.Max.Z)) continue;
                if (rb.Max.Z > bestTop) { best = roof; bestTop = rb.Max.Z; }
            }
            if (best == null)
            {
                note = $"\"{picked.Name}\" is one of Sportify's own floors and no roof of the building was found under it: it was pushed as it is";
                return picked;
            }
            note = $"\"{picked.Name}\" is one of Sportify's own floors: the roof under it ({best.Name}, id {best.Id.Value}) was pushed instead";
            return best;
        }

        /// <summary>
        /// The building roof a layout was laid on, from the layout alone (its world origin and size): for an import with no roof pushed in this Revit
        /// session, which used to be recorded as roof "0" and then never replaced. Null when none matches.
        /// </summary>
        internal static Element? FromLayout(Document doc, SportifyLayout layout) => FromLayout(doc, layout, out _);

        /// <summary>
        /// The building roof a layout was made for: the one its origin stands on, else — `byOutline` true — the one whose box in plan is the layout's
        /// outline (RoofMatch.PickByOutline: between storeys that repeat it, the layout's height above the ground). The second is for a layout whose height names no roof: an
        /// older export can carry the roof at height 0 (found 2026-09-30, the team's "sportify_combined_revit (6)"), and was then built on the ground
        /// under the building, recorded as roof "0", while the next sync of the same layout went onto the real roof beside it. The caller builds a
        /// `byOutline` layout at the roof's own height.
        /// </summary>
        internal static Element? FromLayout(Document doc, SportifyLayout layout, out bool byOutline)
        {
            byOutline = false;
            var rc = layout.RoofContext;
            if (rc == null || rc.LengthM <= 0 || rc.WidthM <= 0) return null;
            var frame = new RoofFrame(rc.WorldOriginXM, rc.WorldOriginYM, rc.RotationDeg * Math.PI / 180.0, rc.LengthM, rc.WidthM);
            var (cx, cy) = frame.ToModel(rc.LengthM * 0.5, rc.WidthM * 0.5);
            double x = SportifyLayoutBuilder.FeetFromMeters(cx), y = SportifyLayoutBuilder.FeetFromMeters(cy), z = SportifyLayoutBuilder.FeetFromMeters(rc.WorldOriginZM);
            Element? best = null;
            double bestGap = double.MaxValue;
            foreach (var roof in BuildingRoofs(doc))
            {
                var rb = roof.get_BoundingBox(null);
                if (!RoofMatch.StandsOn(z, x, y, rb.Min.X, rb.Min.Y, rb.Max.X, rb.Max.Y, rb.Max.Z)) continue;
                var gap = Math.Abs(rb.Max.Z - z);
                if (gap < bestGap) { best = roof; bestGap = gap; }
            }
            if (best != null) return best;

            var corners = new[] { frame.ToModel(0, 0), frame.ToModel(rc.LengthM, 0), frame.ToModel(0, rc.WidthM), frame.ToModel(rc.LengthM, rc.WidthM) };
            double minX = SportifyLayoutBuilder.FeetFromMeters(corners.Min(c => c.X)), maxX = SportifyLayoutBuilder.FeetFromMeters(corners.Max(c => c.X));
            double minY = SportifyLayoutBuilder.FeetFromMeters(corners.Min(c => c.Y)), maxY = SportifyLayoutBuilder.FeetFromMeters(corners.Max(c => c.Y));
            var roofs = BuildingRoofs(doc);
            var boxes = roofs.Select(r => r.get_BoundingBox(null)).Select(b => (b.Min.X, b.Min.Y, b.Max.X, b.Max.Y, b.Max.Z)).ToList();
            var pick = RoofMatch.PickByOutline(minX, minY, maxX, maxY, SportifyLayoutBuilder.FeetFromMeters(rc.HeightAboveGroundM), boxes);
            if (pick < 0) return null;
            byOutline = true;
            return roofs[pick];
        }

        /// <summary>The roof element a ledger key names, when it is a live building roof (not a Sportify floor, not deleted); else null.</summary>
        internal static Element? RoofOfKey(Document doc, string? key)
        {
            if (RoofMatch.IsUnnamedRoofKey(key) || key!.StartsWith(RoofMatch.OptionKeyPrefix, StringComparison.Ordinal)) return null;
            if (!long.TryParse(key, out var id)) return null;
            var el = doc.GetElement(new ElementId(id));
            return el != null && !IsSportifys(doc, el) ? el : null;
        }

        /// <summary>Whether an element stands on the roof (RoofMatch.StandsOn with their boxes).</summary>
        internal static bool StandsOn(Element el, BoundingBoxXYZ roofBox)
        {
            var bb = el.get_BoundingBox(null);
            if (bb == null) return false;
            var c = (bb.Min + bb.Max) * 0.5;
            return RoofMatch.StandsOn(bb.Min.Z, c.X, c.Y, roofBox.Min.X, roofBox.Min.Y, roofBox.Max.X, roofBox.Max.Y, roofBox.Max.Z);
        }

        /// <summary>Whether most of a group of elements (an earlier import) lie in the roof's outline box in plan, at any height.</summary>
        internal static bool MostlyInPlan(IReadOnlyCollection<Element> elements, BoundingBoxXYZ roofBox)
        {
            var boxes = elements.Select(e => e.get_BoundingBox(null)).Where(b => b != null).ToList();
            if (boxes.Count == 0) return false;
            return boxes.Count(b => { var c = (b!.Min + b.Max) * 0.5; return RoofMatch.InPlan(c.X, c.Y, roofBox.Min.X, roofBox.Min.Y, roofBox.Max.X, roofBox.Max.Y); }) * 2 > boxes.Count;
        }

        /// <summary>Whether most of a group of elements (an earlier import) stand on the roof.</summary>
        internal static bool MostlyOn(IReadOnlyCollection<Element> elements, BoundingBoxXYZ roofBox)
        {
            var placed = elements.Where(e => e.get_BoundingBox(null) != null).ToList();
            if (placed.Count == 0) return false;
            return placed.Count(e => StandsOn(e, roofBox)) * 2 > placed.Count;
        }
    }
}
