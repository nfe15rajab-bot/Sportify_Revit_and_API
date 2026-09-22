using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using System.Text.Json;

namespace SportfyRevit
{
    /// <summary>
    /// The "Push to Sportify" commands: take a roof (or floor) of the model, extract what the scope asks for, and push it to RoofBoundaryServer so
    /// the frontend's Combine tab picks it up on its next poll. Read-only: never touches the model.
    ///
    /// The ribbon offers them as a drop-down (see SportfyRevitApp): everything at once, or one part of what the model knows about the roof
    /// (its structure, its entries, its openings ...), so a designer who only changed the stairs does not have to walk the whole model again and a slow
    /// read can be left out. Whatever the scope, the roof's outline and size always go with it: they fix the plan's frame. A push of the same roof
    /// with a smaller scope keeps what the earlier pushes brought (RoofPushMerge); a push of another roof starts again.
    ///
    /// Which roof: the roof or floor already selected in the model; failing that, for a push that is only part of the model's data, the roof pushed
    /// last; failing that, the designer is asked to pick one.
    /// </summary>
    public abstract class PushRoofCommandBase : IExternalCommand
    {
        /// <summary>What this command pushes (the roof's outline and size always go too).</summary>
        internal abstract RoofPushScope Scope { get; }

        // the roof pushed last (in this session, in this document): a partial push complements it without asking again
        private static long _lastRoofId;
        private static string? _lastDocumentTitle;

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var scope = Scope | RoofPushScope.Roof;
            var uidoc = commandData.Application.ActiveUIDocument;
            var doc = uidoc.Document;
            _selection = uidoc.Selection.GetElementIds();

            // Two ways to say what this scope should read, offered when the designer has not already selected something (a pre-selection is taken
            // as "manual" straight away, exactly as before — no dialog): pick the elements in Revit (only the kinds this item is about: entries
            // offers stairs and ramps, not doors, PushWorksets.PickableKinds), or read them from the Sportify worksets that mirror this drop-down
            // (see the Worksets command). Neither is possible (a plain "Everything" push with nothing to pick and no worksets): unchanged, falls
            // through to the roof/floor pick below and the collectors' own search near the roof.
            var usedWorksets = false;
            var worksetNames = PushWorksets.All.Where(w => scope.HasFlag(w.Scope)).Select(w => w.Name).ToList();
            if (_selection.Count == 0)
            {
                var worksetElements = doc.IsWorkshared ? PushWorksetAssigner.ElementsIn(doc, worksetNames) : new List<Element>();
                var pickableKinds = PushWorksets.PickableKinds(Scope);          // this button's own item (Scope), not `scope` (always includes Roof too)
                var canPickManually = pickableKinds.Count > 0;
                if (worksetElements.Count > 0 || canPickManually)
                {
                    var kindsText = string.Join(", ", pickableKinds.Distinct());
                    var ask = new TaskDialog("Sportify — Push to Sportify")
                    {
                        MainInstruction = $"How should {RoofPushScopes.Describe(scope)} be found?",
                        MainContent = worksetElements.Count > 0
                            ? $"{worksetElements.Count} element(s) already sit on the Sportify workset(s) for this ({string.Join(", ", worksetNames)})."
                            : "Nothing is selected, and no Sportify workset holds anything of this kind yet (see the Worksets command).",
                        CommonButtons = TaskDialogCommonButtons.Cancel,
                    };
                    ask.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Select manually",
                        canPickManually ? $"Pick the {kindsText} to push in the model." : "Continues to pick a roof or a floor, as usual.");
                    if (worksetElements.Count > 0)
                        ask.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "By workset", $"Reads the {worksetElements.Count} element(s) already on {string.Join(", ", worksetNames)}.");
                    ask.DefaultButton = worksetElements.Count > 0 ? TaskDialogResult.CommandLink2 : TaskDialogResult.CommandLink1;
                    var answer = ask.Show();
                    if (answer == TaskDialogResult.CommandLink2 && worksetElements.Count > 0)
                    {
                        _selection = worksetElements.Select(e => e.Id).ToList();
                        usedWorksets = true;
                    }
                    else if (answer == TaskDialogResult.CommandLink1 && canPickManually)
                    {
                        try
                        {
                            var picked = uidoc.Selection.PickObjects(ObjectType.Element, new PushWorksetAssigner.KindSelectionFilter(pickableKinds), $"Select the {kindsText} to push");
                            _selection = picked.Select(r => r.ElementId).ToList();
                        }
                        catch (Autodesk.Revit.Exceptions.OperationCanceledException) { return Result.Cancelled; }
                    }
                    else if (answer != TaskDialogResult.CommandLink1) return Result.Cancelled;
                    // CommandLink1 with !canPickManually: falls through to the roof/floor pick below, unchanged.
                }
            }

            Element? element = null;
            var reusedLast = false;
            foreach (var id in _selection)
            {
                var candidate = doc.GetElement(id);
                if (candidate != null && new RoofOrFloorSelectionFilter().AllowElement(candidate)) { element = candidate; break; }
            }
            if (element == null && !RoofPushScopes.IsEverything(scope) && _lastRoofId != 0 && _lastDocumentTitle == doc.Title)
            {
                element = doc.GetElement(new ElementId(_lastRoofId));
                reusedLast = element != null;
            }
            if (element == null)
            {
                try
                {
                    var reference = uidoc.Selection.PickObject(ObjectType.Element, new RoofOrFloorSelectionFilter(), "Select a roof (or floor) to push to Sportify");
                    element = doc.GetElement(reference);
                }
                catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                {
                    return Result.Cancelled;
                }
            }

            var bbox = element.get_BoundingBox(null);
            if (bbox == null)
            {
                message = "Selected element has no visible geometry.";
                return Result.Failed;
            }

            var topFace = FindTopFace(element, out double? topFaceZFt);
            var boundary = topFace != null ? OuterLoopPoints(topFace) : null;

            double ToMeters(double feet) => UnitUtils.ConvertFromInternalUnits(feet, UnitTypeId.Meters);

            // The plan follows the roof: a roof turned against the model's axes gets its own (x along the roof, y down from its top edge),
            // so pieces, grid, bays and wind zones are measured square to it instead of against a far bigger bounding box (see RoofFrame).
            // A roof square to the model gets the frame it always had: origin at the bounding box's minimum corner.
            var frame = RoofFrame.Fit(boundary?.Select(p => (ToMeters(p.X), ToMeters(p.Y))).ToList(),
                ToMeters(bbox.Min.X), ToMeters(bbox.Min.Y), ToMeters(bbox.Max.X), ToMeters(bbox.Max.Y));

            // How high the roof stands above the ground: the wind analysis scales the roof's edge zones with it.
            // Prefer the model's topography under the roof, else the ground-floor level; the source travels with it.
            var roofTopFt = topFaceZFt ?? bbox.Max.Z;
            var roofTopM = ToMeters(roofTopFt);
            var heightAboveGround = RoofHeightAboveGround.Choose(
                roofTopM,
                TryTopographyElevationM(doc, bbox, roofTopFt),
                new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                    .Select(l => new RoofHeightAboveGround.LevelInfo(l.Name, ToMeters(l.Elevation))).ToList());

            // The structure under the roof (grid lines, columns, beams, bearing walls), for the structural load and dynamic analyses.
            var structure = scope.HasFlag(RoofPushScope.Structure) ? TryCollectStructure(doc, frame, roofTopFt, bbox, out var structureNote) : null;
            var structureText = scope.HasFlag(RoofPushScope.Structure) ? _structureNote : "";

            // Openings, entries (stairs, cores, doors), parapets and railings, drains, equipment, the slab and the levels: what the rules that
            // decide where things may stand need to know about the real roof.
            var featureScope = scope & (RoofPushScope.Entries | RoofPushScope.Openings | RoofPushScope.Edge | RoofPushScope.Drains | RoofPushScope.Equipment | RoofPushScope.SlabLevels);
            var features = featureScope != RoofPushScope.None
                ? TryCollectFeatures(doc, element, topFace, boundary, roofTopFt, bbox, frame, heightAboveGround, featureScope, out var featuresNote)
                : null;
            var featuresText = featureScope != RoofPushScope.None ? _featuresNote : "";

            var payload = new
            {
                roof = new
                {
                    // Already in the canvas convention (roof-local x right, y down): see StructureGeometry.
                    structure,
                    // Same convention: see RoofFeaturesGeometry and ROOF_FEATURES.md.
                    features,
                    length_m = Math.Round(frame.Length, 2),
                    width_m = Math.Round(frame.Width, 2),
                    // How far the plan is turned from the model's X axis (counter-clockwise, degrees): 0 for a roof square to the model.
                    // The Revit import turns everything back by it; nothing else needs to know.
                    rotation_deg = Math.Round(frame.AngleDeg, 3),
                    // NOT flipped, unlike every other Y in this pipeline. The
                    // web app draws the boundary with its own flip built in
                    // (roofShapeSvg: "roof.width - p.y_m"), so this polygon
                    // alone travels in Revit's convention — Y up from the
                    // roof's minimum. Placements, circulation and entry points
                    // use the canvas convention instead (Y down from the top
                    // edge) and are flipped on import. Two conventions in one
                    // payload is a trap, but it is the app's existing contract.
                    // (In the roof's own axes when the plan is turned: RoofFrame.ToLocalUp.)
                    boundary_m = boundary?.Select(p =>
                    {
                        var (a, b) = frame.ToLocalUp(ToMeters(p.X), ToMeters(p.Y));
                        return new { x_m = Math.Round(a, 2), y_m = Math.Round(b, 2) };
                    }).ToArray(),
                    origin_x_m = Math.Round(frame.OriginX, 3),
                    origin_y_m = Math.Round(frame.OriginY, 3),
                    // The roof's actual height in the project. Without this every
                    // imported piece landed at Z=0 — on the ground, under the
                    // building, instead of on the roof it was designed for.
                    // Prefer the top face (what you'd stand on); fall back to the
                    // bounding box top for a shape with no flat upward face.
                    origin_z_m = Math.Round(ToMeters(topFaceZFt ?? bbox.Max.Z), 3),
                    source_element_name = element.Name,
                    // Null when no ground could be worked out; the web app then lets the designer type it.
                    height_above_ground_m = heightAboveGround?.HeightM,
                    height_source = heightAboveGround?.Source,
                },
            };

            // A push of only part of the data is laid onto the roof pushed before it (same roof), so the app always gets the roof whole.
            var merged = RoofPushMerge.Merge(RoofBoundaryServer.CurrentPayload, JsonSerializer.Serialize(payload), scope, out var keptEarlier);
            RoofBoundaryServer.SetPayload(merged);
            _lastRoofId = element.Id.Value;
            _lastDocumentTitle = doc.Title;

            // Pushed by workset: say what was flagged too — an element sitting on a Sportify workset that its own kind does not belong on (a
            // duct on Sportify Structure) is read as if it were selected like anything else there, but the mismatch is worth a look.
            var worksetNote = "";
            if (usedWorksets)
            {
                var misplaced = PushWorksetAssigner.Misplaced(doc, scope);
                worksetNote = $"\n\nFrom the Sportify workset(s): {string.Join(", ", worksetNames)}." +
                    (misplaced.Count > 0
                        ? $" {misplaced.Count} element(s) there do not match the kind their workset is for — check them (Worksets command): " +
                          string.Join(", ", misplaced.Take(5).Select(e => e.Name)) + (misplaced.Count > 5 ? $", and {misplaced.Count - 5} more." : ".")
                        : "");
            }

            TaskDialog.Show("Sportify",
                $"Pushed \"{element.Name}\" ({payload.roof.length_m} m x {payload.roof.width_m} m): {RoofPushScopes.Describe(scope)}." +
                (reusedLast ? "\nThe roof pushed before was used again; select another roof first to change it." : "") +
                (RoofPushScopes.IsEverything(scope) ? "" : keptEarlier
                    ? "\nWhat earlier pushes of this roof brought is kept."
                    : "\nThis is a different roof from the one pushed before (or the first): only what was pushed now is on it. Push the rest from the same drop-down.") +
                "\nSwitch to the Sportify Combine tab to see it." +
                (frame.IsTurned
                    ? $"\n\nThe roof is turned {frame.AngleDeg:0.#}° against the model's axes, so the plan was turned with it: pieces you place are square to the roof, and the import turns them back."
                    : "") +
                (heightAboveGround != null
                    ? $"\n\nRoof height above ground: {heightAboveGround.HeightM:0.#} m (from the {heightAboveGround.Source}). " +
                      "Check it: the wind analysis uses it, and the Site tab lets you override it."
                    : "\n\nCouldn't work out the roof's height above ground (no topography or ground-floor level found): enter it in the Site tab.") +
                structureText + featuresText + worksetNote);

            return Result.Succeeded;
        }

        // What the two collectors say for the dialog (kept beside the return values because their signatures carry the DTOs).
        private static string _structureNote = "";
        private static string _featuresNote = "";

        /// <summary>What the designer has selected in Revit right now (set at the start of Execute): the collectors read the selected grid lines, columns, beams, walls, stairs, lifts, doors and ramps instead of everything.</summary>
        private static ICollection<ElementId>? _selection;

        /// <summary>
        /// Everything else the model says about the roof, in the roof's own plan coordinates (see RoofFeatureCollector for what is read and how),
        /// or null when nothing was found. Best-effort: a failure only means the app has no features to show.
        /// </summary>
        private static RoofFeaturesDto? TryCollectFeatures(Document doc, Element roofElement, PlanarFace? topFace, List<XYZ>? outline, double roofTopFt,
            BoundingBoxXYZ bbox, RoofFrame frame, RoofHeightAboveGround.Result? height, RoofPushScope scope, out string note)
        {
            note = "";
            _featuresNote = "";
            try
            {
                double M(double feet) => UnitUtils.ConvertFromInternalUnits(feet, UnitTypeId.Meters);
                var c = RoofFeatureCollector.Collect(doc, roofElement, topFace, outline, roofTopFt, bbox, scope, _selection);
                var dto = RoofFeaturesGeometry.Build(
                    frame,
                    c.Outline, c.Openings, c.Entries, c.EdgeElements, c.Drains, c.Slab, c.Levels, M(roofTopFt), height?.GroundM, c.RoofLevelName, c.Notes, c.Obstacles, c.Equipment);

                var parts = new List<string>();
                if (scope.HasFlag(RoofPushScope.Openings)) parts.Add($"{dto.Openings.Count} opening(s)");
                if (scope.HasFlag(RoofPushScope.Entries)) parts.Add($"{dto.Entries.Count} entr{(dto.Entries.Count == 1 ? "y" : "ies")} (stairs, cores, doors)");
                if (scope.HasFlag(RoofPushScope.Drains)) parts.Add($"{dto.Drains.Count} drain(s)");
                if (scope.HasFlag(RoofPushScope.Equipment)) parts.Add($"{dto.Equipment.Count} piece(s) of equipment ({dto.Equipment.Count(e => e.WeightKn.HasValue)} with a weight)");
                if (scope.HasFlag(RoofPushScope.SlabLevels)) parts.Add($"{dto.Levels.Count} level(s)" + (dto.Slab != null ? $", slab {dto.Slab.ThicknessM * 1000:0} mm ({dto.Slab.StructuralThicknessM * 1000:0} mm structure)" : ", no slab build-up read"));
                if (scope.HasFlag(RoofPushScope.Edge))
                {
                    var edgeSummary = dto.Edges.Count == 0 ? "" : $", edge: {dto.Edges.Count(e => e.Kind == "parapet")} parapet / {dto.Edges.Count(e => e.Kind == "railing")} railing / {dto.Edges.Count(e => e.Kind is "open" or "partial")} open stretch(es)";
                    parts.Add($"{dto.Obstacles.Count} wall(s) that shade the roof{edgeSummary}");
                }
                _featuresNote = $"\n\nRoof features: {string.Join(", ", parts)}." +
                                (dto.Notes.Count > 0 ? "\n" + string.Join(" ", dto.Notes) : "") +
                                (scope.HasFlag(RoofPushScope.Drains) || scope.HasFlag(RoofPushScope.Edge) || scope.HasFlag(RoofPushScope.Equipment)
                                    ? "\nDrains are found by their family names, parapets by their height and equipment by its category and height; check them in the app (Site tab, Roof features)." : "");
                note = _featuresNote;
                return dto;
            }
            catch (Exception ex)
            {
                note = $"\n\nRoof features: couldn't be read ({ex.Message}).";
                _featuresNote = note;
                return null;
            }
        }

        /// <summary>
        /// The structure under the roof, in the roof's own plan coordinates: the model's straight grid lines and structural columns, the beams under
        /// the slab and the walls that reach it (see StructureCollector). Null when there is none of them. Best-effort like the ground lookup: a
        /// failure only means the structural analysis assumes a regular grid. Curved and multi-segment grids are not read; the note says so.
        /// </summary>
        private static StructureDto? TryCollectStructure(Document doc, RoofFrame frame, double roofTopFt, BoundingBoxXYZ bbox, out string note)
        {
            note = "";
            _structureNote = "";
            try
            {
                var c = StructureCollector.Collect(doc, RoofPushScope.Structure, roofTopFt, bbox, _selection);
                var dto = StructureGeometry.ToRoofLocal(c.Grids, c.Columns, frame, c.Beams, c.Walls);

                var skipped = c.CurvedGrids + c.MultiSegmentGrids;
                var notes = c.Notes.Count > 0 ? "\n" + string.Join(" ", c.Notes) : "";
                if ((dto.GridLines?.Count ?? 0) == 0 && (dto.Columns?.Count ?? 0) == 0 && (dto.Beams?.Count ?? 0) == 0 && (dto.Walls?.Count ?? 0) == 0)
                {
                    note = "\n\nStructure: no grid lines, columns, beams or walls found under this roof, so the structural analysis will assume a regular grid." +
                           (skipped > 0 ? $" ({skipped} curved or multi-segment grid(s) are not read.)" : "") + notes;
                    _structureNote = note;
                    return null;
                }

                note = $"\n\nStructure: {dto.GridLines!.Count} grid line(s), {dto.Columns!.Count} column(s), {dto.Beams?.Count ?? 0} beam segment(s) and {dto.Walls?.Count ?? 0} wall(s) under the roof " +
                       $"({dto.Walls?.Count(w => w.Bearing) ?? 0} load-bearing)." +
                       (skipped > 0 ? $" {skipped} curved or multi-segment grid(s) were not read." : "") +
                       "\nBeams and walls are recognised by their height against the roof's top face: check them in the app." + notes;
                _structureNote = note;
                return dto;
            }
            catch (Exception ex)
            {
                note = $"\n\nStructure: couldn't read the model ({ex.Message}); the structural analysis will assume a regular grid.";
                _structureNote = note;
                return null;
            }
        }

        /// <summary>
        /// The ground under the roof from the model's topography (a ray down from the roof at its centre and four points
        /// inset from its corners, averaged), or null when the model has none or the ray finds nothing. Best-effort: any
        /// failure just means the ground-floor level is used instead.
        /// </summary>
        private static double? TryTopographyElevationM(Document doc, BoundingBoxXYZ bbox, double roofTopFt)
        {
            try
            {
                var view3d = new FilteredElementCollector(doc).OfClass(typeof(View3D)).Cast<View3D>().FirstOrDefault(v => !v.IsTemplate);
                if (view3d == null) return null;

                var categories = new List<BuiltInCategory> { BuiltInCategory.OST_Topography, BuiltInCategory.OST_Toposolid };
                var intersector = new ReferenceIntersector(new ElementMulticategoryFilter(categories), FindReferenceTarget.Element, view3d);

                double dx = bbox.Max.X - bbox.Min.X, dy = bbox.Max.Y - bbox.Min.Y;
                double cx = (bbox.Min.X + bbox.Max.X) / 2, cy = (bbox.Min.Y + bbox.Max.Y) / 2;
                var samples = new[]
                {
                    (cx, cy),
                    (bbox.Min.X + dx * 0.15, bbox.Min.Y + dy * 0.15), (bbox.Max.X - dx * 0.15, bbox.Min.Y + dy * 0.15),
                    (bbox.Min.X + dx * 0.15, bbox.Max.Y - dy * 0.15), (bbox.Max.X - dx * 0.15, bbox.Max.Y - dy * 0.15),
                };

                var grounds = new List<double>();
                foreach (var (x, y) in samples)
                {
                    var hit = intersector.FindNearest(new XYZ(x, y, roofTopFt), XYZ.BasisZ.Negate());
                    if (hit != null) grounds.Add(roofTopFt - hit.Proximity);
                }

                return grounds.Count == 0 ? null : UnitUtils.ConvertFromInternalUnits(grounds.Average(), UnitTypeId.Meters);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// Finds the highest upward-facing planar face on the element and
        /// returns its outer boundary loop's vertices, projected flat. Using
        /// the actual face geometry (rather than just the sketch, which
        /// isn't available in a version-stable way across all host types)
        /// works the same for a FootPrintRoof, a Floor, or anything else
        /// with a roughly horizontal top face.
        /// </summary>
        /// <summary>
        /// Also reports the elevation of the face it found (topFaceZFt), since
        /// that's the height an imported layout has to sit at — it was already
        /// being computed here to pick the face and then discarded.
        /// </summary>
        private static PlanarFace? FindTopFace(Element element, out double? topFaceZFt)
        {
            topFaceZFt = null;
            var options = new Options { ComputeReferences = false, DetailLevel = ViewDetailLevel.Fine };
            var geomElem = element.get_Geometry(options);
            if (geomElem == null) return null;

            PlanarFace? topFace = null;
            double maxZ = double.MinValue;

            foreach (var geomObj in geomElem)
            {
                if (geomObj is not Solid solid || solid.Volume <= 0) continue;
                foreach (Face face in solid.Faces)
                {
                    if (face is not PlanarFace planarFace) continue;
                    if (planarFace.FaceNormal.Z <= 0.9) continue; // only upward-facing, roughly horizontal
                    if (planarFace.Origin.Z <= maxZ) continue;
                    maxZ = planarFace.Origin.Z;
                    topFace = planarFace;
                }
            }
            if (topFace == null) return null;
            topFaceZFt = maxZ;
            return topFace;
        }

        /// <summary>The vertices of the outer loop of a face (the loop enclosing the largest area).</summary>
        private static List<XYZ>? OuterLoopPoints(PlanarFace topFace)
        {
            var loops = topFace.GetEdgesAsCurveLoops();
            if (loops == null || loops.Count == 0) return null;

            // The outer boundary is the loop enclosing the largest area —
            // any other loops on the same face are holes (skylights, etc.).
            CurveLoop? outerLoop = null;
            double bestArea = -1;
            foreach (var loop in loops)
            {
                double area = Math.Abs(ExactCurveLoop.SignedArea(loop));
                if (area > bestArea) { bestArea = area; outerLoop = loop; }
            }
            if (outerLoop == null) return null;

            var points = new List<XYZ>();
            foreach (var curve in outerLoop) points.Add(curve.GetEndPoint(0));
            return points;
        }
    }

    /// <summary>Shoelace-formula area of a CurveLoop's vertices, used only to tell an outer boundary from an inner hole loop — sign/exact curvature doesn't matter, just relative magnitude.</summary>
    internal static class ExactCurveLoop
    {
        public static double SignedArea(CurveLoop loop)
        {
            var pts = new List<XYZ>();
            foreach (var curve in loop) pts.Add(curve.GetEndPoint(0));
            double sum = 0;
            for (int i = 0; i < pts.Count; i++)
            {
                var a = pts[i];
                var b = pts[(i + 1) % pts.Count];
                sum += a.X * b.Y - b.X * a.Y;
            }
            return sum / 2.0;
        }
    }

    internal class RoofOrFloorSelectionFilter : ISelectionFilter
    {
        public bool AllowElement(Element elem) =>
            elem.Category != null &&
            (elem.Category.Id.Value == (long)BuiltInCategory.OST_Roofs ||
             elem.Category.Id.Value == (long)BuiltInCategory.OST_Floors);

        public bool AllowReference(Reference reference, XYZ position) => true;
    }
}
