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
        /// The roof's own plan frame for the import in progress (metres; see RoofFrame): where the plan's origin is in the model and how far
        /// the plan is turned from the model's axes. For a roof square to the model this is the old convention exactly.
        /// </summary>
        private static RoofFrame CurrentFrame = RoofFrame.Axis(0, 0, 0, 0);

        /// <summary>
        /// Plan coordinates (metres, y DOWN from the roof's top edge, as the web canvas measures them) to a model point in feet.
        /// Replaces the formula "origin + x, WorldYFt(origin, y)" at every place the import places something, so a roof turned against
        /// the model's axes is turned back in ONE place.
        /// </summary>
        internal static (double X, double Y) PlanToWorldFt(double planXM, double planYM)
        {
            var (mx, my) = CurrentFrame.ToModel(planXM, planYM);
            return (FeetFromMeters(mx), FeetFromMeters(my));
        }

        /// <summary>The same for the roof's boundary polygon, which alone keeps y UP (see PushRoofBoundaryCommand).</summary>
        internal static (double X, double Y) LocalUpToWorldFt(double aM, double bM)
        {
            var (mx, my) = CurrentFrame.FromLocalUp(aM, bM);
            return (FeetFromMeters(mx), FeetFromMeters(my));
        }

        /// <summary>How far the plan is turned from the model's X axis (radians, counter-clockwise), for the rotation of placed families.</summary>
        internal static double CurrentAngleRad => CurrentFrame.AngleRad;

        internal static XYZ PlanPointFt(double planXM, double planYM, double zFt)
        {
            var (x, y) = PlanToWorldFt(planXM, planYM);
            return new XYZ(x, y, zFt);
        }

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

        /// <summary>
        /// `singleWorksetName`: ImportIterationsAsOptionsCommand's way of putting one whole iteration's geometry on ONE workset of its own (e.g.
        /// "Sportify Iteration 2") instead of the normal Sports/Gardens/Combine split — every per-element worksets["Gardens"|"Sports"|"Combine"]
        /// lookup below resolves to the same WorksetId, so nothing else in this method needs to know iterations exist.
        /// </summary>
        public static ImportSummary BuildGeometry(Document doc, SportifyLayout layout, PreparedFamilies prepared, bool useWorksets, string? singleWorksetName = null)
        {
            var createdIds = new List<ElementId>();

            // Worksharing is settled by the caller (LayoutImporter asks the person, and turns it on if they agree) BEFORE the transaction this
            // runs in: Document.EnableWorksharing throws inside an open transaction. In a project without worksets (the person said no, or a
            // caller that must not change the project) everything is simply left on the project's default workset.
            var worksets = EnsureWorksets(doc, useWorksets, singleWorksetName);
            var textTypeId = GetDefaultTextNoteTypeId(doc);

            double originXFt = FeetFromMeters(layout.RoofContext?.WorldOriginXM ?? 0);
            double originYFt = FeetFromMeters(layout.RoofContext?.WorldOriginYM ?? 0);
            // Height of the roof this layout was designed on. Everything built
            // below sits at this elevation instead of Z=0, which put the whole
            // layout on the ground under the building.
            double originZFt = FeetFromMeters(layout.RoofContext?.WorldOriginZM ?? 0);
            SportifyLayoutBuilder.CurrentOriginZFt = originZFt;
            CurrentRoofWidthFt = FeetFromMeters(layout.RoofContext?.WidthM ?? 0);
            CurrentFrame = new RoofFrame(layout.RoofContext?.WorldOriginXM ?? 0, layout.RoofContext?.WorldOriginYM ?? 0,
                (layout.RoofContext?.RotationDeg ?? 0) * Math.PI / 180.0, layout.RoofContext?.LengthM ?? 0, layout.RoofContext?.WidthM ?? 0);

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
                    if (CreatePlacementGeometry(doc, p, originXFt, originYFt, worksets, textTypeId, createdIds, fireSafetyDistancesM, prepared))
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
                // A roof typed in by hand (no Revit push) has no outline polygon, only a length and a width: it is that rectangle, from the roof's origin corner. Before, no
                // finish was ever drawn for such a roof (found by the live Revit import).
                var roof = layout.RoofContext;
                if (roof == null || roof.LengthM <= 0 || roof.WidthM <= 0)
                {
                    ImportDiagnostics.FloorFailed("Roof finish", "this export carries neither a roof boundary polygon nor a roof size");
                    return;
                }
                boundary = new List<PointDto>
                {
                    new() { XM = 0, YM = 0 }, new() { XM = roof.LengthM, YM = 0 }, new() { XM = roof.LengthM, YM = roof.WidthM }, new() { XM = 0, YM = roof.WidthM },
                };
                ImportDiagnostics.Note($"the roof finish follows the roof's size ({roof.LengthM:0.#} x {roof.WidthM:0.#} m): this export has no outline polygon (a roof typed in by hand)");
            }

            var pts = boundary
                .Select(p => new XYZ(originXFt + FeetFromMeters(p.XM), originYFt + FeetFromMeters(p.YM), 0))
                .ToList();

            // The holes are the web app's zones and courts and the openings the Revit model already has in the roof (roof_context.features.openings).
            var revitOpenings = layout.RoofContext?.Features?.Openings ?? new List<RoofOpeningDto>();
            var floor = SportifyFloorTypeBuilder.CreateRoofFinish(
                doc, floorType, pts, (finish.Openings ?? new List<OpeningDto>()).Where(o => o.Source != "revit_opening"),
                originXFt, originYFt, CurrentOriginZFt, out var failure, revitOpenings);

            if (floor == null)
            {
                ImportDiagnostics.FloorFailed($"Roof finish ({assembly.SystemName})", failure ?? "unknown reason");
                return;
            }
            SetWorkset(floor, worksetId);
            createdIds.Add(floor.Id);
            // The area Revit computes for the floor with its holes is the honest one: the web app's figure (finish.NetAreaM2) is roof less zones and pieces (and less Revit's openings,
            // in a current export), before any hole was refused.
            double areaM2 = finish.NetAreaM2;
            try
            {
                var computed = floor.get_Parameter(BuiltInParameter.HOST_AREA_COMPUTED)?.AsDouble();
                if (computed is > 0) areaM2 = UnitUtils.ConvertFromInternalUnits(computed.Value, UnitTypeId.SquareMeters);
            }
            catch (Exception) { /* the export's figure stands */ }
            int revitHoles = revitOpenings.Count;
            ImportDiagnostics.FloorCreated(
                $"Roof finish ({(finish.Openings?.Count ?? 0)} opening(s) from the layout, {revitHoles} from the Revit model"
                    + (failure != null ? $"; {failure}" : "") + ")",
                floorType.Name, areaM2);
            if (Math.Abs(areaM2 - finish.NetAreaM2) > Math.Max(1.0, finish.NetAreaM2 * 0.02))
                ImportDiagnostics.Note($"the roof finish measures {areaM2:0.#} m2 in Revit (with its holes); the web app's figure was {finish.NetAreaM2:0.#} m2");
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
                    // Three different causes used to arrive as one message.
                    // Which one it is decides who can fix it.
                    bool unresolved = layout.UnresolvedAssemblies?.Contains(zone.AssemblyKey ?? "") == true;
                    ImportDiagnostics.FloorFailed(label,
                        zone.AssemblyKey == null ? "no build-up system was chosen for it"
                        : unresolved ? $"build-up \"{zone.AssemblyKey}\" was not in the export — the web app's catalog was not loaded when it was exported (start Sportify.Api and re-export)"
                        : $"the floor type for \"{zone.AssemblyKey}\" could not be built");
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

                // CreateRoofFinish reports a partial success through the same out
                // parameter, so it can be null on the failure path too.
                if (floor == null) { ImportDiagnostics.FloorFailed(label, failure ?? "Revit gave no reason"); continue; }

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
        private static Dictionary<string, WorksetId> EnsureWorksets(Document doc, bool useWorksets, string? singleWorksetName = null)
        {
            var names = new[] { "Sports", "Gardens", "Combine" };
            if (!useWorksets || !doc.IsWorkshared)
                return names.ToDictionary(n => n, n => WorksetId.InvalidWorksetId);

            if (singleWorksetName != null)
            {
                var one = EnsureOneWorkset(doc, singleWorksetName);
                return names.ToDictionary(n => n, n => one);
            }

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

        private static WorksetId EnsureOneWorkset(Document doc, string name)
        {
            var existing = new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset).FirstOrDefault(w => w.Name == name);
            return existing != null ? existing.Id : Workset.Create(doc, name).Id;
        }

        /// <summary>internal, not private: FamilyPlacementBuilder calls this too.</summary>
        internal static void SetWorkset(Element el, WorksetId worksetId)
        {
            if (worksetId == WorksetId.InvalidWorksetId) return;      // a project without worksets: the default workset stays
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
        private static ElementId? GetDefaultTextNoteTypeId(Document doc)
        {
            // A text note lives in a VIEW. It used to be created blindly in whichever view was active, and in a 3D view (or a schedule) Revit throws:
            // one label rolled the whole import back. Now the labels are simply skipped there, and the report says so.
            var view = doc.ActiveView;
            bool holdsText = view != null && view.ViewType is ViewType.FloorPlan or ViewType.CeilingPlan or ViewType.EngineeringPlan or ViewType.AreaPlan
                                                          or ViewType.Elevation or ViewType.Section or ViewType.Detail or ViewType.DraftingView or ViewType.Legend
                                                          or ViewType.DrawingSheet;
            if (!holdsText)
            {
                ImportDiagnostics.Note("labels were not created: the active view (" + (view?.ViewType.ToString() ?? "none") + ") cannot hold text notes; import from a plan view to get them");
                return null;
            }
            var existing = new FilteredElementCollector(doc).OfClass(typeof(TextNoteType)).FirstOrDefault();
            if (existing == null)
            {
                ImportDiagnostics.Note("labels were not created: this project has no text note type");
                return null;
            }
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
            Dictionary<string, WorksetId> worksets, ElementId? textTypeId, List<ElementId> createdIds,
            Dictionary<string, double> fireSafetyDistancesM, PreparedFamilies prepared)
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

            FamilyPlacementBuilder.PlaceComponent(doc, p, bb, isGarden, originXFt, originYFt, worksetId, textTypeId, createdIds, fireSafetyDistanceM, prepared.For(p));
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
            createdIds.Add(sketchPlane.Id);
            var line = doc.Create.NewModelCurve(Line.CreateBound(a, b), sketchPlane);
            SetWorkset(line, worksetId);
            createdIds.Add(line.Id);
        }

        /// <summary>The roof's bounding rectangle in its own axes (a turned roof: a turned rectangle), corner by corner, y up.</summary>
        private static List<XYZ> RoofRectangleFt(SportifyLayout layout, double originXFt, double originYFt)
        {
            double length = layout.RoofContext?.LengthM ?? 0;
            double width = layout.RoofContext?.WidthM ?? 0;
            return new List<XYZ>
            {
                LocalUpPointFt(0, 0), LocalUpPointFt(length, 0), LocalUpPointFt(length, width), LocalUpPointFt(0, width),
            };
        }

        private static XYZ LocalUpPointFt(double aM, double bM)
        {
            var (x, y) = LocalUpToWorldFt(aM, bM);
            return new XYZ(x, y, CurrentOriginZFt);
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
                pts = poly.Select(pt => LocalUpPointFt(pt.XM, pt.YM)).ToList();
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

            // inset in the roof's own axes, so a turned roof gets a turned rectangle
            double setback = layout.DesignRules?.BoundarySetbackM ?? 0;
            double length = layout.RoofContext?.LengthM ?? 0;
            double width = layout.RoofContext?.WidthM ?? 0;
            double minX = setback, minY = setback, maxX = length - setback, maxY = width - setback;
            if (maxX <= minX || maxY <= minY) return;

            var pts = new List<XYZ>
            {
                LocalUpPointFt(minX, minY), LocalUpPointFt(maxX, minY), LocalUpPointFt(maxX, maxY), LocalUpPointFt(minX, maxY),
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

                var pts = points.Select(pt => PlanPointFt(pt.XM, pt.YM, CurrentOriginZFt)).ToList();
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
                var center = PlanPointFt(ep.XM, ep.YM, CurrentOriginZFt);
                var plane = Plane.CreateByNormalAndOrigin(XYZ.BasisZ, center);
                var sketchPlane = SketchPlane.Create(doc, plane);
                createdIds.Add(sketchPlane.Id);
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
