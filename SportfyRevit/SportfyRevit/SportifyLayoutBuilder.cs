using Autodesk.Revit.DB;

namespace SportfyRevit
{
    internal record ImportSummary(int PieceCount, int PathCount, int EntryCount, List<ElementId> CreatedIds);

    /// <summary>
    /// The sync/orchestration half of "turn a Combine export into Revit
    /// geometry" — shared by the manual Import command and AutoImportSync's
    /// live-sync path so there's exactly one place that knows how a layout
    /// turns into elements: worksets/worksharing, the roof boundary/setback
    /// outline, circulation paths, and entry markers. Per-placement family
    /// matching/instantiation is deliberately NOT here — see
    /// FamilyPlacementBuilder, which this class only calls into — so the
    /// sync-pipeline work and the family-implementation work stay in
    /// separate files. Must be called from inside an already-open
    /// transaction — this class never starts or commits one itself, since
    /// the auto-sync path needs to delete last cycle's elements first, in
    /// the SAME transaction as the rebuild.
    /// </summary>
    internal static class SportifyLayoutBuilder
    {
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

        /// <summary>internal, not private: FamilyPlacementBuilder calls this too.</summary>
        internal static double FeetFromMeters(double m) => UnitUtils.ConvertToInternalUnits(m, UnitTypeId.Meters);

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

        /// <summary>internal, not private: FamilyPlacementBuilder calls this too.</summary>
        internal static void SetWorkset(Element el, WorksetId worksetId)
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
        /// Per component: figure out its category/workset, then hand off to
        /// FamilyPlacementBuilder for the actual family-matching/placeholder
        /// decision — this class doesn't know or care how a placement
        /// becomes an element, only that it does.
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

            FamilyPlacementBuilder.PlaceComponent(doc, p, bb, isGarden, originXFt, originYFt, worksetId, textTypeId, createdIds);
            return true;
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
