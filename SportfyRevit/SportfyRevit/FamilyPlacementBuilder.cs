using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;

namespace SportfyRevit
{
    /// <summary>
    /// Everything about turning one placement into a Revit element: matching
    /// it to a loaded FamilySymbol (or falling back to a placeholder box),
    /// placing it, and labeling it. Split out of SportifyLayoutBuilder so the
    /// family-implementation work (this file) and the sync/orchestration work
    /// (SportifyLayoutBuilder, AutoImportSync, RoofBoundaryServer) live in
    /// separate files — this is the one to extend when adding real parametric
    /// family support; the roof boundary/setback/circulation/entry-marker
    /// geometry and the sync pipeline that calls into this class stay in
    /// SportifyLayoutBuilder.cs and are not part of this file's job.
    /// </summary>
    internal static class FamilyPlacementBuilder
    {
        private const double ThicknessM = 0.1;

        /// <summary>
        /// Single entry point SportifyLayoutBuilder calls per placement: try a
        /// real family first, fall back to the placeholder box if nothing
        /// loaded matches well enough.
        /// </summary>
        public static void PlaceComponent(
            Document doc, PlacementDto p, BoundingBoxDto bb, bool isGarden,
            double originXFt, double originYFt, WorksetId worksetId, ElementId textTypeId, List<ElementId> createdIds)
        {
            var symbol = FindMatchingFamilySymbol(doc, p, GetPlacementKeywords(p));
            Element placed = symbol != null
                ? PlaceFamilyInstance(doc, p, bb, symbol, originXFt, originYFt, worksetId, textTypeId, createdIds)
                : PlacePlaceholderBox(doc, p, bb, isGarden, originXFt, originYFt, worksetId, textTypeId, createdIds);

            // Stamps the same unified parameter set on whatever got placed —
            // real family instance or placeholder box alike — so Revit's own
            // Properties panel/Schedules can already query category/quality/
            // material data today, without waiting on real "sportified"
            // families to exist. Defensive: EnsureBound returns false (and
            // SetValues is then a no-op) if the shared-parameter file/binding
            // fails for any reason — geometry placement above must never be
            // broken by this.
            if (SportifySharedParameters.EnsureBound(doc))
            {
                var (typeId, variant, norm, lengthM, widthM) = PlacementDataHelpers.GetUnifiedFields(p);
                SportifySharedParameters.SetValues(placed, p.Category, typeId, variant,
                    PlacementDataHelpers.GetQualityLevel(p), norm, lengthM, widthM,
                    PlacementDataHelpers.GetReferenceMaterialName(p), PlacementDataHelpers.GetReferenceProviderName(p),
                    p.Parameters?.QualityKey);
            }
        }

        /// <summary>
        /// Words split out of a placement's category/label/quality_key,
        /// used to score every loaded FamilySymbol's family name and pick
        /// the closest match. quality_key (e.g. "BASKETBALL_STANDARD_HIGH",
        /// "GARDEN_ROOF_TREES_JAPANESE_HIGH") is included because it's
        /// already the frontend's own designed-for-lookup identifier (see
        /// ParametersDto) — label alone is free text a human wrote for the
        /// sidebar, not a stable key.
        /// </summary>
        private static HashSet<string> GetPlacementKeywords(PlacementDto p)
        {
            var words = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            words.UnionWith(ExtractWords(p.Category));
            words.UnionWith(ExtractWords(p.Label));
            words.UnionWith(ExtractWords(p.Parameters?.QualityKey));
            return words;
        }

        private static readonly Regex WordSplitter = new(@"[^A-Za-z0-9]+", RegexOptions.Compiled);

        private static IEnumerable<string> ExtractWords(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) yield break;
            foreach (var word in WordSplitter.Split(text))
            {
                if (word.Length >= 2)
                    yield return word;
            }
        }

        /// <summary>
        /// Checks FamilyMatchRules.Lookup for an exact quality_key match
        /// first (empty today, so this is a no-op until Moamen fills it in —
        /// see that file's own TODO), then falls back to best-effort keyword
        /// scoring: scores every FamilySymbol currently loaded in the
        /// document by how many keywords its family name shares with this
        /// placement, and returns the top scorer (null if nothing shares
        /// even one word). Deliberately generic rather than a hardcoded
        /// sport-name table, since this project has no bundled family
        /// content — it has to work with whatever family names the user
        /// loads in, whatever they're called.
        /// </summary>
        private static FamilySymbol? FindMatchingFamilySymbol(Document doc, PlacementDto p, HashSet<string> keywords)
        {
            var qualityKey = p.Parameters?.QualityKey;
            if (!string.IsNullOrWhiteSpace(qualityKey) && FamilyMatchRules.Lookup.TryGetValue(qualityKey, out var exactFamilyName))
            {
                var exact = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
                    .FirstOrDefault(s => string.Equals(s.Family?.Name, exactFamilyName, StringComparison.OrdinalIgnoreCase));
                if (exact != null) return exact;
            }

            if (keywords.Count == 0) return null;

            FamilySymbol? best = null;
            int bestScore = 0;
            var symbols = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>();
            foreach (var symbol in symbols)
            {
                var familyName = symbol.Family?.Name;
                if (string.IsNullOrWhiteSpace(familyName)) continue;

                int score = ExtractWords(familyName).Count(w => keywords.Contains(w));
                if (score > bestScore)
                {
                    bestScore = score;
                    best = symbol;
                }
            }
            return best;
        }

        /// <summary>
        /// The real-family path: places an instance of a FamilySymbol the
        /// user already loaded, at the frontend's purpose-built center
        /// point (falling back to the bounding box's center for exports
        /// from before insertion_point existed), rotates it to match the
        /// Combine layout, and labels it with the family's own name — the
        /// "assign a family + name annotation" behavior this replaces the
        /// placeholder box with.
        /// </summary>
        private static Element PlaceFamilyInstance(
            Document doc, PlacementDto p, BoundingBoxDto bb, FamilySymbol symbol,
            double originXFt, double originYFt, WorksetId worksetId, ElementId textTypeId, List<ElementId> createdIds)
        {
            if (!symbol.IsActive)
            {
                symbol.Activate();
                doc.Regenerate();
            }

            double centerXFt, centerYFt;
            if (p.InsertionPoint != null)
            {
                centerXFt = originXFt + SportifyLayoutBuilder.FeetFromMeters(p.InsertionPoint.CenterXM);
                centerYFt = originYFt + SportifyLayoutBuilder.FeetFromMeters(p.InsertionPoint.CenterYM);
            }
            else
            {
                centerXFt = originXFt + SportifyLayoutBuilder.FeetFromMeters(bb.TopLeftXM + bb.WidthM / 2.0);
                centerYFt = originYFt + SportifyLayoutBuilder.FeetFromMeters(bb.TopLeftYM + bb.HeightM / 2.0);
            }
            var center = new XYZ(centerXFt, centerYFt, 0);

            var instance = doc.Create.NewFamilyInstance(center, symbol, StructuralType.NonStructural);
            SportifyLayoutBuilder.SetWorkset(instance, worksetId);
            createdIds.Add(instance.Id);

            double rotationDeg = p.Transform?.RotationDeg ?? 0;
            if (Math.Abs(rotationDeg) > 1e-6)
            {
                // Every other coordinate in this file maps x_m/y_m straight
                // onto Revit X/Y with no screen-to-world Y flip, so
                // rotation_deg is applied here the same unflipped way (CCW
                // about +Z for a positive angle). Untested against a real
                // rotated family in an actual Revit view — if a placed
                // instance comes in mirrored, negate rotationRad below.
                double rotationRad = rotationDeg * Math.PI / 180.0;
                var axis = Line.CreateBound(center, center + XYZ.BasisZ);
                ElementTransformUtils.RotateElement(doc, instance.Id, axis, rotationRad);
            }

            string familyName = symbol.Family?.Name ?? symbol.Name;
            var textOrigin = new XYZ(centerXFt, centerYFt, SportifyLayoutBuilder.FeetFromMeters(ThicknessM));
            CreateLabelText(doc, familyName, textOrigin, textTypeId, worksetId, createdIds);

            return instance;
        }

        /// <summary>
        /// bounding_box.width_m/height_m from the frontend already reflect
        /// the item's final on-roof orientation (Combine's getFootprint()
        /// swaps width/height itself for a 90°-rotated item before export)
        /// — so a plain axis-aligned rectangle built straight from those
        /// dimensions is already correctly oriented. Applying transform.
        /// rotation_deg on top would rotate it a second time, so — unlike
        /// PlaceFamilyInstance, which needs it — that field stays unused
        /// here. This is the fallback for whenever PlaceComponent has no
        /// loaded family to match: still just a labeled box standing in
        /// for a real, non-square family with its own "front".
        /// </summary>
        private static Element PlacePlaceholderBox(
            Document doc, PlacementDto p, BoundingBoxDto bb, bool isGarden,
            double originXFt, double originYFt, WorksetId worksetId, ElementId textTypeId, List<ElementId> createdIds)
        {
            double xFt = originXFt + SportifyLayoutBuilder.FeetFromMeters(bb.TopLeftXM);
            double yFt = originYFt + SportifyLayoutBuilder.FeetFromMeters(bb.TopLeftYM);
            double wFt = SportifyLayoutBuilder.FeetFromMeters(bb.WidthM);
            double hFt = SportifyLayoutBuilder.FeetFromMeters(bb.HeightM);
            double thickFt = SportifyLayoutBuilder.FeetFromMeters(ThicknessM);

            var loop = new CurveLoop();
            var c0 = new XYZ(xFt, yFt, 0);
            var c1 = new XYZ(xFt + wFt, yFt, 0);
            var c2 = new XYZ(xFt + wFt, yFt + hFt, 0);
            var c3 = new XYZ(xFt, yFt + hFt, 0);
            loop.Append(Line.CreateBound(c0, c1));
            loop.Append(Line.CreateBound(c1, c2));
            loop.Append(Line.CreateBound(c2, c3));
            loop.Append(Line.CreateBound(c3, c0));

            var solid = GeometryCreationUtilities.CreateExtrusionGeometry(new List<CurveLoop> { loop }, XYZ.BasisZ, thickFt);

            var categoryId = new ElementId(isGarden ? BuiltInCategory.OST_Planting : BuiltInCategory.OST_GenericModel);

            var ds = DirectShape.CreateElement(doc, categoryId);
            ds.SetShape(new GeometryObject[] { solid });

            string label = BuildLabel(p.Label ?? p.Category ?? "item", bb.WidthM, bb.HeightM);
            ds.Name = label;

            SportifyLayoutBuilder.SetWorkset(ds, worksetId);
            createdIds.Add(ds.Id);

            var textOrigin = new XYZ(xFt + wFt / 2.0, yFt + hFt / 2.0, thickFt);
            CreateLabelText(doc, label, textOrigin, textTypeId, worksetId, createdIds);

            return ds;
        }

        private static string BuildLabel(string name, double widthM, double heightM)
        {
            var safe = name.Trim().Replace(' ', '_');
            return $"{safe}_{widthM:0.#}x{heightM:0.#}";
        }

        private static void CreateLabelText(Document doc, string text, XYZ origin, ElementId textTypeId, WorksetId worksetId, List<ElementId> createdIds)
        {
            var textNote = TextNote.Create(doc, doc.ActiveView.Id, origin, text, textTypeId);
            SportifyLayoutBuilder.SetWorkset(textNote, worksetId);
            createdIds.Add(textNote.Id);
        }
    }
}
