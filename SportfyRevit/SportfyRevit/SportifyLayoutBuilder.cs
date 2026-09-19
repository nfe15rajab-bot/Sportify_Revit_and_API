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

        /// <summary>
        /// Elevation (feet) everything in the current import is built at — set
        /// once per BuildGeometry call from roof_context.world_origin_z_m.
        /// A field rather than another parameter on eight private helpers:
        /// Revit API work is single-threaded inside one transaction, and both
        /// import paths set it before building anything.
        /// </summary>
        internal static double CurrentOriginZFt { get; private set; }

        /// <summary>
        /// Roof width (feet) for the import in progress — the mirror line for
        /// the Y flip below.
        /// </summary>
        private static double CurrentRoofWidthFt;

        /// <summary>
        /// Floor types built for this import, by assembly key — so a parcel can
        /// find the type its build-up produced without searching the document
        /// again for every placement.
        /// </summary>
        private static readonly Dictionary<string, FloorType> CurrentFloorTypes = new();

        /// <summary>
        /// Converts a web-canvas Y (meters, measured DOWN from the roof's top
        /// edge, as SVG does) into a Revit world Y (feet, measured UP).
        ///
        /// Without this every piece lands mirrored about the roof's horizontal
        /// centre line: a court drawn near the top of the canvas appears at the
        /// bottom of the roof in Revit. The two coordinate systems simply
        /// disagree about which way Y grows, and nothing else in the pipeline
        /// reconciles them.
        /// </summary>
        internal static double WorldYFt(double originYFt, double webYM)
        {
            return originYFt + CurrentRoofWidthFt - FeetFromMeters(webYM);
        }

        public static ImportSummary BuildGeometry(Document doc, SportifyLayout layout)
        {
            var createdIds = new List<ElementId>();

            // EnsureWorksharing is NOT called here on purpose — Document.EnableWorksharing
            // throws ("Operation is not permitted when there is any open sub-transaction,
            // transaction, or transaction group") if called while a transaction is open, and
            // this method must be called from inside one (see the class doc comment above).
            // Callers (ImportSportifyLayoutCommand, AutoImportSync) call EnsureWorksharing
            // themselves, before opening their transaction.
            var worksets = EnsureWorksets(doc);
            var textTypeId = GetDefaultTextNoteTypeId(doc);

            double originXFt = FeetFromMeters(layout.RoofContext?.WorldOriginXM ?? 0);
            double originYFt = FeetFromMeters(layout.RoofContext?.WorldOriginYM ?? 0);
            // Height of the roof this layout was designed on. Everything built
            // below sits at this elevation instead of Z=0, which put the whole
            // layout on the ground under the building.
            double originZFt = FeetFromMeters(layout.RoofContext?.WorldOriginZM ?? 0);
            SportifyLayoutBuilder.CurrentOriginZFt = originZFt;
            CurrentRoofWidthFt = FeetFromMeters(layout.RoofContext?.WidthM ?? 0);

            // Computed once for the whole layout (a BFS pass, not a per-placement
            // lookup) and threaded down to FamilyPlacementBuilder, which stamps
            // Revit's own freshly-computed distance onto each instance rather than
            // trusting the JSON's own web-app estimate — same reasoning
            // AnalyzeFireSafetyCommand already applies at the layout-summary level.
            var (fireSafetyDistancesM, _) = CirculationEngine.ComputeTravelDistances(layout);

            // Build-up systems first: a floor type must exist before anything
            // can reference it, and creating them once per import (rather than
            // once per parcel) is the whole reason the export sends them at the
            // top level.
            CreateAssemblyFloorTypes(doc, layout);

            // Ground zones become real floors — the build-up the designer chose,
            // at the area they drew. Done before the pieces so a court sits
            // visually on top of the ground rather than under it.
            int zoneCount = CreateZoneFloors(doc, layout, originXFt, originYFt, worksets["Gardens"], createdIds);

            // The leftover surface, before the pieces, so a court reads as
            // sitting in the finish rather than on top of it.
            CreateRoofFinishFloor(doc, layout, originXFt, originYFt, worksets["Gardens"], createdIds);

            int pieceCount = 0;
            if (layout.Placements != null)
            {
                foreach (var p in layout.Placements)
                {
                    if (CreatePlacementGeometry(doc, p, originXFt, originYFt, worksets, textTypeId, createdIds, fireSafetyDistancesM))
                        pieceCount++;
                }
            }

            CreateRoofBoundary(doc, layout, originXFt, originYFt, worksets["Combine"], createdIds);
            CreateSetbackBoundary(doc, layout, originXFt, originYFt, worksets["Combine"], createdIds);
            int pathCount = CreateCirculationPaths(doc, layout, originXFt, originYFt, worksets["Combine"], createdIds);
            int entryCount = CreateEntryMarkers(doc, layout, originXFt, originYFt, worksets["Combine"], createdIds);

            return new ImportSummary(pieceCount, pathCount, entryCount, createdIds);
        }

        /// <summary>
        /// Turns every provider build-up in the layout into a real Revit floor
        /// type. Failures are recorded rather than thrown: a missing floor type
        /// must not stop the courts and boundaries from being built.
        /// </summary>
        private static void CreateAssemblyFloorTypes(Document doc, SportifyLayout layout)
        {
            CurrentFloorTypes.Clear();
            if (layout.Assemblies == null) return;
            foreach (var assembly in layout.Assemblies)
            {
                try
                {
                    var ft = SportifyFloorTypeBuilder.GetOrCreate(doc, assembly);
                    if (ft != null && assembly.Key != null) CurrentFloorTypes[assembly.Key] = ft;
                }
                catch (Exception ex)
                {
                    ImportDiagnostics.FloorTypeFailed(
                        assembly.RevitTypeName ?? assembly.SystemName ?? "(unnamed)",
                        $"{ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Draws a Floor for every ground zone, using the floor type built from
        /// its assembly. A zone whose build-up produced no type is reported and
        /// skipped rather than drawn as something the designer did not choose.
        /// </summary>
        /// <summary>
        /// One floor for everything the courts and the zones do not cover, with
        /// each of them punched through it as an opening.
        ///
        /// The boundary comes from the same polygon the roof was pushed with,
        /// and deliberately is NOT Y-flipped: the web app already flips it when
        /// it draws, so flipping again here would mirror the roof back. Items
        /// and openings do get flipped, because those coordinates are the app's
        /// own — the same asymmetry the rest of this importer carries.
        /// </summary>
        private static void CreateRoofFinishFloor(Document doc, SportifyLayout layout,
            double originXFt, double originYFt, WorksetId worksetId, List<ElementId> createdIds)
        {
            var finish = layout.RoofFinish;
            if (finish == null || string.IsNullOrWhiteSpace(finish.AssemblyKey)) return;

            var assembly = layout.Assemblies?.FirstOrDefault(a => a.Key == finish.AssemblyKey);
            if (assembly == null)
            {
                ImportDiagnostics.FloorFailed("Roof finish",
                    $"build-up \"{finish.AssemblyKey}\" was not in the export's assembly list");
                return;
            }

            var floorType = SportifyFloorTypeBuilder.GetOrCreate(doc, assembly);
            if (floorType == null) return;

            var boundary = layout.RoofContext?.SourceBoundaryPolygon;
            if (boundary == null || boundary.Count < 3)
            {
                ImportDiagnostics.FloorFailed("Roof finish", "this export carries no roof boundary polygon");
                return;
            }

            var pts = boundary
                .Select(p => new XYZ(originXFt + FeetFromMeters(p.XM), originYFt + FeetFromMeters(p.YM), 0))
                .ToList();

            var floor = SportifyFloorTypeBuilder.CreateRoofFinish(
                doc, floorType, pts, finish.Openings ?? new List<OpeningDto>(),
                originXFt, originYFt, CurrentOriginZFt, out var failure);

            if (floor == null)
            {
                ImportDiagnostics.FloorFailed($"Roof finish ({assembly.SystemName})", failure ?? "unknown reason");
                return;
            }
            SetWorkset(floor, worksetId);
            createdIds.Add(floor.Id);
            ImportDiagnostics.FloorCreated(
                $"Roof finish ({(finish.Openings?.Count ?? 0)} opening(s)"
                    + (failure != null ? $"; {failure}" : "") + ")",
                floorType.Name, finish.NetAreaM2);
        }

        private static int CreateZoneFloors(Document doc, SportifyLayout layout,
                                            double originXFt, double originYFt,
                                            WorksetId worksetId, List<ElementId> createdIds)
        {
            if (layout.Zones == null) return 0;
            int drawn = 0;

            foreach (var zone in layout.Zones)
            {
                var bb = zone.BoundingBox;
                if (bb == null || bb.WidthM <= 0 || bb.HeightM <= 0) continue;

                var label = zone.Label ?? zone.Kind ?? "(zone)";
                if (zone.AssemblyKey == null || !CurrentFloorTypes.TryGetValue(zone.AssemblyKey, out var floorType))
                {
                    ImportDiagnostics.FloorFailed(label, "no floor type was built for its build-up system");
                    continue;
                }

                // Sketch the outline the designer actually drew. Only an export
                // from before zones had movable corners falls back to the box,
                // which for those is the same shape anyway.
                var floor = (zone.Points != null && zone.Points.Count >= 3)
                    ? SportifyFloorTypeBuilder.CreateFloorFromPoints(
                        doc, floorType, zone.Points, originXFt, originYFt, CurrentOriginZFt, out string failure)
                    : SportifyFloorTypeBuilder.CreateFloor(
                        doc, floorType, bb, originXFt, originYFt, CurrentOriginZFt, out failure);

                if (floor == null) { ImportDiagnostics.FloorFailed(label, failure); continue; }

                SetWorkset(floor, worksetId);
                createdIds.Add(floor.Id);
                ImportDiagnostics.FloorCreated(label, floorType.Name, zone.AreaM2);
                drawn++;
            }
            return drawn;
        }

        /// <summary>internal, not private: FamilyPlacementBuilder calls this too.</summary>
        internal static double FeetFromMeters(double m) => UnitUtils.ConvertToInternalUnits(m, UnitTypeId.Meters);

        /// <summary>
        /// internal, not private: must be called by ImportSportifyLayoutCommand/AutoImportSync
        /// themselves, BEFORE they open their transaction — EnableWorksharing manages its own
        /// transaction internally and throws if called while one is already open.
        /// </summary>
        internal static void EnsureWorksharing(Document doc)
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
            Dictionary<string, WorksetId> worksets, ElementId textTypeId, List<ElementId> createdIds,
            Dictionary<string, double> fireSafetyDistancesM)
        {
            var bb = p.BoundingBox;
            if (bb == null || bb.WidthM <= 0 || bb.HeightM <= 0)
                return false;

            // Planting goes to the Gardens workset alongside the ground it stands in,
            // not to Sports with the courts.
            bool isGarden = string.Equals(p.Category, "garden", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(p.Category, "vegetation", StringComparison.OrdinalIgnoreCase);
            var worksetId = worksets[isGarden ? "Gardens" : "Sports"];

            double? fireSafetyDistanceM = (p.Id != null && fireSafetyDistancesM.TryGetValue(p.Id, out var dist)) ? dist : (double?)null;

            // A parcel built from a provider system is a Floor, not a family:
            // that is what gives it real layers, real depth and quantities a
            // schedule can total. Only a parcel with no build-up falls through
            // to the family/placeholder path below.
            if (TryCreateAssemblyFloor(doc, p, bb, originXFt, originYFt, worksetId, createdIds))
                return true;

            FamilyPlacementBuilder.PlaceComponent(doc, p, bb, isGarden, originXFt, originYFt, worksetId, textTypeId, createdIds, fireSafetyDistanceM);
            return true;
        }

        /// <summary>
        /// Draws the Floor for a placement whose parameters name a provider
        /// build-up. Returns false when there is nothing to draw — no assembly,
        /// or no floor type was built for it — so the caller falls back rather
        /// than the parcel disappearing.
        /// </summary>
        private static bool TryCreateAssemblyFloor(Document doc, PlacementDto p, BoundingBoxDto bb,
                                                   double originXFt, double originYFt,
                                                   WorksetId worksetId, List<ElementId> createdIds)
        {
            var assembly = p.Parameters?.Garden?.Assembly;
            if (assembly?.Key == null) return false;
            if (!CurrentFloorTypes.TryGetValue(assembly.Key, out var floorType)) return false;

            var floor = SportifyFloorTypeBuilder.CreateFloor(
                doc, floorType, bb, originXFt, originYFt, CurrentOriginZFt, out string failure);

            if (floor == null)
            {
                ImportDiagnostics.FloorFailed(p.Label ?? p.Id ?? "(parcel)", failure);
                return false;
            }

            SetWorkset(floor, worksetId);
            createdIds.Add(floor.Id);
            ImportDiagnostics.FloorCreated(p.Label ?? p.Id ?? "(parcel)", floorType.Name, bb.WidthM * bb.HeightM);
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
                new XYZ(originXFt, originYFt, CurrentOriginZFt),
                new XYZ(originXFt + lengthFt, originYFt, CurrentOriginZFt),
                new XYZ(originXFt + lengthFt, originYFt + widthFt, CurrentOriginZFt),
                new XYZ(originXFt, originYFt + widthFt, CurrentOriginZFt),
            };
        }

        private static void CreateRoofBoundary(Document doc, SportifyLayout layout, double originXFt, double originYFt, WorksetId worksetId, List<ElementId> createdIds)
        {
            List<XYZ> pts;
            var poly = layout.RoofContext?.SourceBoundaryPolygon;
            if (poly != null && poly.Count >= 3)
            {
                // The one Y in this payload that is NOT flipped: the web app's
                // roofShapeSvg draws this polygon with its own flip, so it is
                // stored in Revit's convention already. Flipping it here mirrored
                // the roof outline while every placement landed correctly — the
                // two conventions have to be honored separately.
                pts = poly.Select(pt => new XYZ(originXFt + FeetFromMeters(pt.XM), originYFt + FeetFromMeters(pt.YM), CurrentOriginZFt)).ToList();
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
                new XYZ(minX, minY, CurrentOriginZFt),
                new XYZ(maxX, minY, CurrentOriginZFt),
                new XYZ(maxX, maxY, CurrentOriginZFt),
                new XYZ(minX, maxY, CurrentOriginZFt),
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

                var pts = points.Select(pt => new XYZ(originXFt + FeetFromMeters(pt.XM), WorldYFt(originYFt, pt.YM), CurrentOriginZFt)).ToList();
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
                var center = new XYZ(originXFt + FeetFromMeters(ep.XM), WorldYFt(originYFt, ep.YM), CurrentOriginZFt);
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
