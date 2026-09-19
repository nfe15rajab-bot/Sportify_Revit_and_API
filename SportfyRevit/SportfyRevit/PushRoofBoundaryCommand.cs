using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using System.Text.Json;

namespace SportfyRevit
{
    /// <summary>
    /// Ribbon command: pick a roof (or floor) in the active view, extract its
    /// footprint, and push it to RoofBoundaryServer so the frontend's
    /// Combine tab picks it up on its next poll. Read-only — never touches
    /// the model.
    /// </summary>
    [Transaction(TransactionMode.ReadOnly)]
    public class PushRoofBoundaryCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var uidoc = commandData.Application.ActiveUIDocument;
            var doc = uidoc.Document;

            Reference reference;
            try
            {
                reference = uidoc.Selection.PickObject(
                    ObjectType.Element,
                    new RoofOrFloorSelectionFilter(),
                    "Select a roof (or floor) to push to Sportify");
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return Result.Cancelled;
            }

            var element = doc.GetElement(reference);
            var bbox = element.get_BoundingBox(null);
            if (bbox == null)
            {
                message = "Selected element has no visible geometry.";
                return Result.Failed;
            }

            var topFace = FindTopFace(element, out double? topFaceZFt);
            var boundary = topFace != null ? OuterLoopPoints(topFace) : null;

            double ToMeters(double feet) => UnitUtils.ConvertFromInternalUnits(feet, UnitTypeId.Meters);
            double originXFt = bbox.Min.X, originYFt = bbox.Min.Y;

            // How high the roof stands above the ground: the wind analysis scales the roof's edge zones with it.
            // Prefer the model's topography under the roof, else the ground-floor level; the source travels with it.
            var roofTopFt = topFaceZFt ?? bbox.Max.Z;
            var roofTopM = ToMeters(roofTopFt);
            var heightAboveGround = RoofHeightAboveGround.Choose(
                roofTopM,
                TryTopographyElevationM(doc, bbox, roofTopFt),
                new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                    .Select(l => new RoofHeightAboveGround.LevelInfo(l.Name, ToMeters(l.Elevation))).ToList());

            // The structural grid and columns that lie under the roof, for the structural load analysis.
            var structure = TryCollectStructure(doc, bbox, out var structureNote);

            // Openings, entries (stairs, cores, doors), parapets and railings, drains, the slab and the levels: what the rules that
            // decide where things may stand need to know about the real roof.
            var features = TryCollectFeatures(doc, element, topFace, boundary, roofTopFt, bbox, heightAboveGround, out var featuresNote);

            var payload = new
            {
                roof = new
                {
                    // Already in the canvas convention (roof-local x right, y down): see StructureGeometry.
                    structure,
                    // Same convention: see RoofFeaturesGeometry and ROOF_FEATURES.md.
                    features,
                    length_m = Math.Round(ToMeters(bbox.Max.X - bbox.Min.X), 2),
                    width_m = Math.Round(ToMeters(bbox.Max.Y - bbox.Min.Y), 2),
                    boundary_m = boundary?.Select(p => new
                    {
                        x_m = Math.Round(ToMeters(p.X - originXFt), 2),
                        // NOT flipped, unlike every other Y in this pipeline. The
                        // web app draws the boundary with its own flip built in
                        // (roofShapeSvg: "roof.width - p.y_m"), so this polygon
                        // alone travels in Revit's convention — Y up from the
                        // roof's minimum. Placements, circulation and entry points
                        // use the canvas convention instead (Y down from the top
                        // edge) and are flipped on import. Two conventions in one
                        // payload is a trap, but it is the app's existing contract.
                        y_m = Math.Round(ToMeters(p.Y - originYFt), 2),
                    }).ToArray(),
                    origin_x_m = Math.Round(ToMeters(originXFt), 2),
                    origin_y_m = Math.Round(ToMeters(originYFt), 2),
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

            RoofBoundaryServer.SetPayload(JsonSerializer.Serialize(payload));

            TaskDialog.Show("Sportify",
                $"Pushed \"{element.Name}\" ({payload.roof.length_m} m x {payload.roof.width_m} m) — " +
                "switch to the Sportify Combine tab to see it." +
                (heightAboveGround != null
                    ? $"\n\nRoof height above ground: {heightAboveGround.HeightM:0.#} m (from the {heightAboveGround.Source}). " +
                      "Check it: the wind analysis uses it, and the Site tab lets you override it."
                    : "\n\nCouldn't work out the roof's height above ground (no topography or ground-floor level found): enter it in the Site tab.") +
                structureNote + featuresNote);

            return Result.Succeeded;
        }

        /// <summary>
        /// Everything else the model says about the roof, in the roof's own plan coordinates (see RoofFeatureCollector for what is read and how),
        /// or null when nothing was found. Best-effort: a failure only means the app has no features to show.
        /// </summary>
        private static RoofFeaturesDto? TryCollectFeatures(Document doc, Element roofElement, PlanarFace? topFace, List<XYZ>? outline, double roofTopFt,
            BoundingBoxXYZ bbox, RoofHeightAboveGround.Result? height, out string note)
        {
            note = "";
            try
            {
                double M(double feet) => UnitUtils.ConvertFromInternalUnits(feet, UnitTypeId.Meters);
                var c = RoofFeatureCollector.Collect(doc, roofElement, topFace, outline, roofTopFt, bbox);
                var dto = RoofFeaturesGeometry.Build(
                    new StructureGeometry.RoofRect(M(bbox.Min.X), M(bbox.Min.Y), M(bbox.Max.X), M(bbox.Max.Y)),
                    c.Outline, c.Openings, c.Entries, c.EdgeElements, c.Drains, c.Slab, c.Levels, M(roofTopFt), height?.GroundM, c.RoofLevelName, c.Notes);

                var edgeSummary = dto.Edges.Count == 0 ? "" : $", edge: {dto.Edges.Count(e => e.Kind == "parapet")} parapet / {dto.Edges.Count(e => e.Kind == "railing")} railing / {dto.Edges.Count(e => e.Kind is "open" or "partial")} open stretch(es)";
                note = $"\n\nRoof features: {dto.Openings.Count} opening(s), {dto.Entries.Count} entr{(dto.Entries.Count == 1 ? "y" : "ies")} (stairs, cores, doors), {dto.Drains.Count} drain(s), " +
                       $"{dto.Levels.Count} level(s){edgeSummary}" +
                       (dto.Slab != null ? $", slab {dto.Slab.ThicknessM * 1000:0} mm ({dto.Slab.StructuralThicknessM * 1000:0} mm structure)" : "") + "." +
                       (dto.Notes.Count > 0 ? "\n" + string.Join(" ", dto.Notes) : "") +
                       "\nDrains are found by their family names and parapets by their height; check them in the app (Site tab, Roof features).";
                return dto;
            }
            catch (Exception ex)
            {
                note = $"\n\nRoof features: couldn't be read ({ex.Message}).";
                return null;
            }
        }

        /// <summary>
        /// The model's straight grid lines and structural columns that lie under the roof, in the roof's own plan coordinates, or null when
        /// there are none. Best-effort like the ground lookup: a failure only means the structural analysis assumes a regular grid.
        /// Curved and multi-segment grids and bearing walls are not read; the note says so.
        /// </summary>
        private static StructureDto? TryCollectStructure(Document doc, BoundingBoxXYZ bbox, out string note)
        {
            note = "";
            try
            {
                double M(double feet) => UnitUtils.ConvertFromInternalUnits(feet, UnitTypeId.Meters);

                var grids = new List<StructureGeometry.GridSegment>();
                var curved = 0;
                foreach (var g in new FilteredElementCollector(doc).OfClass(typeof(Grid)).Cast<Grid>())
                {
                    if (g.Curve is Line line)
                    {
                        var a = line.GetEndPoint(0);
                        var b = line.GetEndPoint(1);
                        grids.Add(new StructureGeometry.GridSegment(g.Name, M(a.X), M(a.Y), M(b.X), M(b.Y)));
                    }
                    else curved++;
                }
                var multiSegment = new FilteredElementCollector(doc).OfClass(typeof(MultiSegmentGrid)).GetElementCount();

                var columns = new List<StructureGeometry.ColumnPoint>();
                foreach (var fi in new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_StructuralColumns).WhereElementIsNotElementType().OfType<FamilyInstance>())
                {
                    XYZ? p = fi.Location switch
                    {
                        LocationPoint lp => lp.Point,
                        LocationCurve lc => lc.Curve.GetEndPoint(0),
                        _ => null,
                    };
                    if (p == null) continue;
                    columns.Add(new StructureGeometry.ColumnPoint(fi.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString() ?? "", M(p.X), M(p.Y)));
                }

                var dto = StructureGeometry.ToRoofLocal(grids, columns,
                    new StructureGeometry.RoofRect(M(bbox.Min.X), M(bbox.Min.Y), M(bbox.Max.X), M(bbox.Max.Y)));

                var skipped = curved + multiSegment;
                if ((dto.GridLines?.Count ?? 0) == 0 && (dto.Columns?.Count ?? 0) == 0)
                {
                    note = "\n\nStructure: no grid lines or structural columns found under this roof, so the structural analysis will assume a regular grid." +
                           (skipped > 0 ? $" ({skipped} curved or multi-segment grid(s) are not read.)" : "");
                    return null;
                }

                note = $"\n\nStructure: {dto.GridLines!.Count} grid line(s) and {dto.Columns!.Count} column(s) under the roof pulled for the structural load analysis." +
                       (skipped > 0 ? $" {skipped} curved or multi-segment grid(s) were not read." : "") +
                       " Bearing walls are not read.";
                return dto;
            }
            catch (Exception ex)
            {
                note = $"\n\nStructure: couldn't read the grids and columns ({ex.Message}); the structural analysis will assume a regular grid.";
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
