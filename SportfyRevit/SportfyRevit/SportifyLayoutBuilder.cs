using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;

namespace SportfyRevit
{
    internal record ImportSummary(int PieceCount, int PathCount, int EntryCount, List<ElementId> CreatedIds);

    /// <summary>
    /// The actual "turn a Combine export into Revit geometry" logic, shared
    /// by the manual Import command and AutoImportSync's live-sync path so
    /// there's exactly one place that knows how a placement/boundary/path/
    /// entry turns into elements. Must be called from inside an already-open
    /// transaction — this class never starts or commits one itself, since
    /// the auto-sync path needs to delete last cycle's elements first, in
    /// the SAME transaction as the rebuild.
    /// </summary>
    internal static class SportifyLayoutBuilder
    {
        private const double ThicknessM = 0.1;
        private const double EntryMarkerRadiusM = 0.4;

        public static ImportSummary BuildGeometry(Document doc, SportifyLayout layout)
        {
            var createdIds = new List<ElementId>();

            EnsureWorksharing(doc);
            var worksets = EnsureWorksets(doc);
            var textTypeId = GetDefaultTextNoteTypeId(doc);

            double originXFt = FeetFromMeters(layout.RoofContext?.WorldOriginXM ?? 0);
            double originYFt = FeetFromMeters(layout.RoofContext?.WorldOriginYM ?? 0);

            int pieceCount = 0;
            if (layout.Placements != null)
            {
                foreach (var p in layout.Placements)
                {
                    if (CreatePlacementGeometry(doc, p, originXFt, originYFt, worksets, textTypeId, createdIds))
                        pieceCount++;
                }
            }

            CreateRoofBoundary(doc, layout, originXFt, originYFt, worksets["Combine"], createdIds);
            CreateSetbackBoundary(doc, layout, originXFt, originYFt, worksets["Combine"], createdIds);
            int pathCount = CreateCirculationPaths(doc, layout, originXFt, originYFt, worksets["Combine"], createdIds);
            int entryCount = CreateEntryMarkers(doc, layout, originXFt, originYFt, worksets["Combine"], createdIds);

            return new ImportSummary(pieceCount, pathCount, entryCount, createdIds);
        }

        private static double FeetFromMeters(double m) => UnitUtils.ConvertToInternalUnits(m, UnitTypeId.Meters);

        private static void EnsureWorksharing(Document doc)
        {
            if (!doc.IsWorkshared)
                doc.EnableWorksharing("Sports", "Combine");
        }

        private static Dictionary<string, WorksetId> EnsureWorksets(Document doc)
        {
            var names = new[] { "Sports", "Gardens", "Combine" };
            var existing = new FilteredWorksetCollector(doc)
                .OfKind(WorksetKind.UserWorkset)
                .ToDictionary(w => w.Name, w => w.Id);

            var result = new Dictionary<string, WorksetId>();
            foreach (var name in names)
            {
                if (existing.TryGetValue(name, out var id))
                {
                    result[name] = id;
                }
                else
                {
                    var created = Workset.Create(doc, name);
                    result[name] = created.Id;
                }
            }
            return result;
        }

        private static void SetWorkset(Element el, WorksetId worksetId)
        {
            var p = el.get_Parameter(BuiltInParameter.ELEM_PARTITION_PARAM);
            if (p != null && !p.IsReadOnly)
                p.Set(worksetId.IntegerValue);
        }

        /// <summary>
        /// Real 3D "ModelText" elements (FamilyItemFactory.NewModelText) only
        /// exist inside family-editor documents — not creatable via the
        /// public API in a normal project document, confirmed by reflecting
        /// on RevitAPI.dll (ModelText exposes no static Create; its only
        /// factory lives on Document.FamilyCreate, which is null/unusable
        /// outside a family document). TextNote is the project-document
        /// equivalent: a real, 3D-positioned label, just screen-facing
        /// rather than extruded — the closest available stand-in for the
        /// "3D text" placeholder labels asked for here.
        /// </summary>
        private static ElementId GetDefaultTextNoteTypeId(Document doc)
        {
            var existing = new FilteredElementCollector(doc)
                .OfClass(typeof(TextNoteType))
                .FirstOrDefault();
            if (existing == null)
                throw new InvalidOperationException("No TextNoteType found in this project — every default template ships with one.");
            return existing.Id;
        }

        /// <summary>
        /// Per component: place a real FamilySymbol already loaded in this
        /// project if one matches well enough (PlaceFamilyInstance), else
        /// fall back to the placeholder DirectShape box (PlacePlaceholderBox)
        /// — this repo ships no .rfa content of its own, so "assign a
        /// family" can only mean matching against whatever the user has
        /// already loaded into their Revit template. Either path also drops
        /// a TextNote naming what was actually placed.
        /// </summary>
        private static bool CreatePlacementGeometry(
            Document doc, PlacementDto p, double originXFt, double originYFt,
            Dictionary<string, WorksetId> worksets, ElementId textTypeId, List<ElementId> createdIds)
        {
            var bb = p.BoundingBox;
            if (bb == null || bb.WidthM <= 0 || bb.HeightM <= 0)
                return false;

            bool isGarden = string.Equals(p.Category, "garden", StringComparison.OrdinalIgnoreCase);
            var worksetId = worksets[isGarden ? "Gardens" : "Sports"];

            var symbol = FindMatchingFamilySymbol(doc, GetPlacementKeywords(p));
            if (symbol != null)
                PlaceFamilyInstance(doc, p, bb, symbol, originXFt, originYFt, worksetId, textTypeId, createdIds);
            else
                PlacePlaceholderBox(doc, p, bb, isGarden, originXFt, originYFt, worksetId, textTypeId, createdIds);

            return true;
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
        /// Best-effort match, not an exact lookup: scores every FamilySymbol
        /// currently loaded in the document by how many keywords its
        /// family name shares with this placement, and returns the top
        /// scorer (null if nothing shares even one word). Deliberately
        /// generic rather than a hardcoded sport-name table, since this
        /// project has no bundled family content — it has to work with
        /// whatever family names the user loads in, whatever they're called.
        /// </summary>
        private static FamilySymbol? FindMatchingFamilySymbol(Document doc, HashSet<string> keywords)
        {
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
        private static void PlaceFamilyInstance(
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
                centerXFt = originXFt + FeetFromMeters(p.InsertionPoint.CenterXM);
                centerYFt = originYFt + FeetFromMeters(p.InsertionPoint.CenterYM);
            }
            else
            {
                centerXFt = originXFt + FeetFromMeters(bb.TopLeftXM + bb.WidthM / 2.0);
                centerYFt = originYFt + FeetFromMeters(bb.TopLeftYM + bb.HeightM / 2.0);
            }
            var center = new XYZ(centerXFt, centerYFt, 0);

            var instance = doc.Create.NewFamilyInstance(center, symbol, StructuralType.NonStructural);
            SetWorkset(instance, worksetId);
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
            var textOrigin = new XYZ(centerXFt, centerYFt, FeetFromMeters(ThicknessM));
            CreateLabelText(doc, familyName, textOrigin, textTypeId, worksetId, createdIds);
        }

        /// <summary>
        /// bounding_box.width_m/height_m from the frontend already reflect
        /// the item's final on-roof orientation (Combine's getFootprint()
        /// swaps width/height itself for a 90°-rotated item before export)
        /// — so a plain axis-aligned rectangle built straight from those
        /// dimensions is already correctly oriented. Applying transform.
        /// rotation_deg on top would rotate it a second time, so — unlike
        /// PlaceFamilyInstance, which needs it — that field stays unused
        /// here. This is the fallback for whatever PlaceFamilyInstance has
        /// no loaded family to match: still just a labeled box standing in
        /// for a real, non-square family with its own "front".
        /// </summary>
        private static void PlacePlaceholderBox(
            Document doc, PlacementDto p, BoundingBoxDto bb, bool isGarden,
            double originXFt, double originYFt, WorksetId worksetId, ElementId textTypeId, List<ElementId> createdIds)
        {
            double xFt = originXFt + FeetFromMeters(bb.TopLeftXM);
            double yFt = originYFt + FeetFromMeters(bb.TopLeftYM);
            double wFt = FeetFromMeters(bb.WidthM);
            double hFt = FeetFromMeters(bb.HeightM);
            double thickFt = FeetFromMeters(ThicknessM);

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

            SetWorkset(ds, worksetId);
            createdIds.Add(ds.Id);

            var textOrigin = new XYZ(xFt + wFt / 2.0, yFt + hFt / 2.0, thickFt);
            CreateLabelText(doc, label, textOrigin, textTypeId, worksetId, createdIds);
        }

        private static string BuildLabel(string name, double widthM, double heightM)
        {
            var safe = name.Trim().Replace(' ', '_');
            return $"{safe}_{widthM:0.#}x{heightM:0.#}";
        }

        private static void CreateLabelText(Document doc, string text, XYZ origin, ElementId textTypeId, WorksetId worksetId, List<ElementId> createdIds)
        {
            var textNote = TextNote.Create(doc, doc.ActiveView.Id, origin, text, textTypeId);
            SetWorkset(textNote, worksetId);
            createdIds.Add(textNote.Id);
        }

        private static void CreateModelLine(Document doc, XYZ a, XYZ b, WorksetId worksetId, List<ElementId> createdIds)
        {
            if (a.DistanceTo(b) < 1e-6) return;
            var plane = Plane.CreateByThreePoints(a, b, a + XYZ.BasisZ);
            var sketchPlane = SketchPlane.Create(doc, plane);
            var line = doc.Create.NewModelCurve(Line.CreateBound(a, b), sketchPlane);
            SetWorkset(line, worksetId);
            createdIds.Add(line.Id);
        }

        private static List<XYZ> RoofRectangleFt(SportifyLayout layout, double originXFt, double originYFt)
        {
            double lengthFt = FeetFromMeters(layout.RoofContext?.LengthM ?? 0);
            double widthFt = FeetFromMeters(layout.RoofContext?.WidthM ?? 0);
            return new List<XYZ>
            {
                new XYZ(originXFt, originYFt, 0),
                new XYZ(originXFt + lengthFt, originYFt, 0),
                new XYZ(originXFt + lengthFt, originYFt + widthFt, 0),
                new XYZ(originXFt, originYFt + widthFt, 0),
            };
        }

        private static void CreateRoofBoundary(Document doc, SportifyLayout layout, double originXFt, double originYFt, WorksetId worksetId, List<ElementId> createdIds)
        {
            List<XYZ> pts;
            var poly = layout.RoofContext?.SourceBoundaryPolygon;
            if (poly != null && poly.Count >= 3)
            {
                pts = poly.Select(pt => new XYZ(originXFt + FeetFromMeters(pt.XM), originYFt + FeetFromMeters(pt.YM), 0)).ToList();
            }
            else
            {
                pts = RoofRectangleFt(layout, originXFt, originYFt);
            }

            for (int i = 0; i < pts.Count; i++)
                CreateModelLine(doc, pts[i], pts[(i + 1) % pts.Count], worksetId, createdIds);
        }

        /// <summary>
        /// Same simple-rectangle-inset approximation the frontend's own
        /// setbackGuideSvg() uses for arbitrary roof polygons, kept
        /// consistent rather than implementing true polygon offsetting here.
        /// </summary>
        private static void CreateSetbackBoundary(Document doc, SportifyLayout layout, double originXFt, double originYFt, WorksetId worksetId, List<ElementId> createdIds)
        {
            double setbackFt = FeetFromMeters(layout.DesignRules?.BoundarySetbackM ?? 0);
            if (setbackFt <= 0) return;

            var outer = RoofRectangleFt(layout, originXFt, originYFt);
            double minX = outer.Min(p => p.X) + setbackFt;
            double minY = outer.Min(p => p.Y) + setbackFt;
            double maxX = outer.Max(p => p.X) - setbackFt;
            double maxY = outer.Max(p => p.Y) - setbackFt;
            if (maxX <= minX || maxY <= minY) return;

            var pts = new List<XYZ>
            {
                new XYZ(minX, minY, 0),
                new XYZ(maxX, minY, 0),
                new XYZ(maxX, maxY, 0),
                new XYZ(minX, maxY, 0),
            };
            for (int i = 0; i < pts.Count; i++)
                CreateModelLine(doc, pts[i], pts[(i + 1) % pts.Count], worksetId, createdIds);
        }

        private static int CreateCirculationPaths(Document doc, SportifyLayout layout, double originXFt, double originYFt, WorksetId worksetId, List<ElementId> createdIds)
        {
            int count = 0;
            if (layout.CirculationPaths == null) return count;

            foreach (var path in layout.CirculationPaths)
            {
                var points = path.PointsM;
                if (points == null || points.Count < 2) continue;

                var pts = points.Select(pt => new XYZ(originXFt + FeetFromMeters(pt.XM), originYFt + FeetFromMeters(pt.YM), 0)).ToList();
                for (int i = 0; i < pts.Count - 1; i++)
                    CreateModelLine(doc, pts[i], pts[i + 1], worksetId, createdIds);
                count++;
            }
            return count;
        }

        private static int CreateEntryMarkers(Document doc, SportifyLayout layout, double originXFt, double originYFt, WorksetId worksetId, List<ElementId> createdIds)
        {
            int count = 0;
            if (layout.EntryPoints == null) return count;

            double rFt = FeetFromMeters(EntryMarkerRadiusM);
            foreach (var ep in layout.EntryPoints)
            {
                var center = new XYZ(originXFt + FeetFromMeters(ep.XM), originYFt + FeetFromMeters(ep.YM), 0);
                var plane = Plane.CreateByNormalAndOrigin(XYZ.BasisZ, center);
                var sketchPlane = SketchPlane.Create(doc, plane);
                var circle = Arc.Create(plane, rFt, 0, 2 * Math.PI);
                var curve = doc.Create.NewModelCurve(circle, sketchPlane);
                SetWorkset(curve, worksetId);
                createdIds.Add(curve.Id);
                count++;
            }
            return count;
        }
    }
}
