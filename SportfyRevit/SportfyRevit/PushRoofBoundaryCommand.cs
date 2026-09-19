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

            var boundary = ExtractTopFaceBoundary(element, out double? topFaceZFt);

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

            var payload = new
            {
                roof = new
                {
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
                    : "\n\nCouldn't work out the roof's height above ground (no topography or ground-floor level found): enter it in the Site tab."));

            return Result.Succeeded;
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
        private static List<XYZ>? ExtractTopFaceBoundary(Element element, out double? topFaceZFt)
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
