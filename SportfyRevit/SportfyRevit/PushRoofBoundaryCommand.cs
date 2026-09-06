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

            var boundary = ExtractTopFaceBoundary(element);

            double ToMeters(double feet) => UnitUtils.ConvertFromInternalUnits(feet, UnitTypeId.Meters);
            double originXFt = bbox.Min.X, originYFt = bbox.Min.Y;

            var payload = new
            {
                roof = new
                {
                    length_m = Math.Round(ToMeters(bbox.Max.X - bbox.Min.X), 2),
                    width_m = Math.Round(ToMeters(bbox.Max.Y - bbox.Min.Y), 2),
                    boundary_m = boundary?.Select(p => new
                    {
                        x_m = Math.Round(ToMeters(p.X - originXFt), 2),
                        y_m = Math.Round(ToMeters(p.Y - originYFt), 2),
                    }).ToArray(),
                    origin_x_m = Math.Round(ToMeters(originXFt), 2),
                    origin_y_m = Math.Round(ToMeters(originYFt), 2),
                    source_element_name = element.Name,
                },
            };

            RoofBoundaryServer.SetPayload(JsonSerializer.Serialize(payload));

            TaskDialog.Show("Sportify",
                $"Pushed \"{element.Name}\" ({payload.roof.length_m} m x {payload.roof.width_m} m) — " +
                "switch to the Sportify Combine tab to see it.");

            return Result.Succeeded;
        }

        /// <summary>
        /// Finds the highest upward-facing planar face on the element and
        /// returns its outer boundary loop's vertices, projected flat. Using
        /// the actual face geometry (rather than just the sketch, which
        /// isn't available in a version-stable way across all host types)
        /// works the same for a FootPrintRoof, a Floor, or anything else
        /// with a roughly horizontal top face.
        /// </summary>
        private static List<XYZ>? ExtractTopFaceBoundary(Element element)
        {
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
