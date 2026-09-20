using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;

namespace SportfyRevit
{
    /// <summary>
    /// The Revit calls that read the structure under the pushed roof: the model's straight grid lines and structural columns, the beams under
    /// the slab and the walls that reach it. Everything is handed to StructureGeometry (Revit-free, tested) as plain records in metres and model
    /// coordinates. Read-only and best-effort like the rest of the push; not tested in a live Revit session.
    ///
    /// Selection first: when the designer has selected grid lines, columns, beams or walls in Revit, only the selected ones of that kind are read (and a
    /// selected beam or wall needs no height rule: they picked it); with none selected of a kind, the model is searched as before.
    ///
    /// What is decided by a rule of thumb and not by a Revit class: a beam is "under the roof" when its top is within 2.5 m below the roof's top
    /// face (up to 0.5 m above it: an upstand beam); a wall "holds up the roof" when its top reaches the roof's top face (within 1 m below it,
    /// up to 0.15 m above) and its base is at least 1 m lower, so a parapet standing ON the roof is not one; whether it is load-bearing is
    /// Revit's own structural-usage setting of the wall.
    /// </summary>
    internal static class StructureCollector
    {
        internal sealed class Collected
        {
            public List<StructureGeometry.GridSegment> Grids = new();
            public List<StructureGeometry.ColumnPoint> Columns = new();
            public List<StructureGeometry.BeamRecord> Beams = new();
            public List<StructureGeometry.WallRecord> Walls = new();
            public int CurvedGrids, MultiSegmentGrids;
            public List<string> Notes = new();
        }

        /// <summary>Beams whose top is this far below the roof's top face (or less) are taken as holding the slab.</summary>
        const double BeamBelowM = 2.5, BeamAboveM = 0.5;

        /// <summary>A wall reaches the roof when its top is within this far below its top face (or up to WallAboveM above it).</summary>
        const double WallBelowM = 1.0, WallAboveM = 0.15, WallMinHeightM = 1.0;

        static readonly string[] SectionWidthNames = { "b", "Breite", "Width", "B", "bf" };
        static readonly string[] SectionDepthNames = { "h", "Höhe", "Hoehe", "Height", "Depth", "d", "H" };

        static double M(double feet) => UnitUtils.ConvertFromInternalUnits(feet, UnitTypeId.Meters);
        static double Ft(double metres) => UnitUtils.ConvertToInternalUnits(metres, UnitTypeId.Meters);

        /// <summary>The ids of the selected elements of a category, or null when the designer selected none of them.</summary>
        static HashSet<long>? Picked(Document doc, ICollection<ElementId>? selection, BuiltInCategory category)
        {
            if (selection == null || selection.Count == 0) return null;
            var ids = new HashSet<long>();
            foreach (var id in selection)
                if (doc.GetElement(id)?.Category?.Id.Value == (long)category) ids.Add(id.Value);
            return ids.Count > 0 ? ids : null;
        }

        public static Collected Collect(Document doc, RoofPushScope scope, double roofTopFt, BoundingBoxXYZ roofBox, ICollection<ElementId>? selection = null)
        {
            var c = new Collected();
            if (!scope.HasFlag(RoofPushScope.Structure)) return c;

            Try(c, "grid lines", () => ReadGrids(c, doc, Picked(doc, selection, BuiltInCategory.OST_Grids)));
            Try(c, "columns", () => ReadColumns(c, doc, Picked(doc, selection, BuiltInCategory.OST_StructuralColumns)));

            var box = new Outline(
                new XYZ(roofBox.Min.X - Ft(1.0), roofBox.Min.Y - Ft(1.0), roofTopFt - Ft(BeamBelowM + 2.0)),
                new XYZ(roofBox.Max.X + Ft(1.0), roofBox.Max.Y + Ft(1.0), roofTopFt + Ft(1.0)));
            var near = new BoundingBoxIntersectsFilter(box);
            Try(c, "beams", () => ReadBeams(c, doc, near, M(roofTopFt), Picked(doc, selection, BuiltInCategory.OST_StructuralFraming)));
            Try(c, "bearing walls", () => ReadWalls(c, doc, near, M(roofTopFt), Picked(doc, selection, BuiltInCategory.OST_Walls)));
            return c;
        }

        static void Try(Collected c, string what, Action read)
        {
            try { read(); }
            catch (Exception ex) { c.Notes.Add($"The {what} could not be read ({ex.Message})."); }
        }

        static void ReadGrids(Collected c, Document doc, HashSet<long>? picked)
        {
            foreach (var g in new FilteredElementCollector(doc).OfClass(typeof(Grid)).Cast<Grid>())
            {
                if (picked != null && !picked.Contains(g.Id.Value)) continue;
                if (g.Curve is Line line)
                {
                    var a = line.GetEndPoint(0);
                    var b = line.GetEndPoint(1);
                    c.Grids.Add(new StructureGeometry.GridSegment(g.Name, M(a.X), M(a.Y), M(b.X), M(b.Y)));
                }
                else c.CurvedGrids++;
            }
            c.MultiSegmentGrids = new FilteredElementCollector(doc).OfClass(typeof(MultiSegmentGrid)).GetElementCount();
        }

        static void ReadColumns(Collected c, Document doc, HashSet<long>? picked)
        {
            foreach (var fi in new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_StructuralColumns).WhereElementIsNotElementType().OfType<FamilyInstance>())
            {
                if (picked != null && !picked.Contains(fi.Id.Value)) continue;
                XYZ? p = fi.Location switch
                {
                    LocationPoint lp => lp.Point,
                    LocationCurve lc => lc.Curve.GetEndPoint(0),
                    _ => null,
                };
                if (p == null) continue;
                c.Columns.Add(new StructureGeometry.ColumnPoint(fi.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString() ?? "", M(p.X), M(p.Y)));
            }
        }

        static double? SectionValue(FamilyInstance fi, string[] names)
        {
            foreach (var n in names)
            {
                var p = fi.LookupParameter(n) ?? fi.Symbol?.LookupParameter(n);
                if (p != null && p.HasValue && p.StorageType == StorageType.Double)
                {
                    var v = M(p.AsDouble());
                    if (v > 0.02 && v < 3.0) return v;
                }
            }
            return null;
        }

        static void ReadBeams(Collected c, Document doc, ElementFilter near, double roofTopM, HashSet<long>? picked)
        {
            foreach (var fi in new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_StructuralFraming).WhereElementIsNotElementType().WherePasses(near).OfType<FamilyInstance>())
            {
                if (picked != null && !picked.Contains(fi.Id.Value)) continue;
                if (fi.StructuralType != StructuralType.Beam || fi.Location is not LocationCurve lc) continue;
                var bb = fi.get_BoundingBox(null);
                if (bb == null) continue;
                var top = M(bb.Max.Z);
                if (picked == null && (top < roofTopM - BeamBelowM || top > roofTopM + BeamAboveM)) continue;

                // the section: the family's own width and depth when it has them, else what its box says (depth = the box's height)
                var depth = SectionValue(fi, SectionDepthNames) ?? Math.Max(0.0, M(bb.Max.Z) - M(bb.Min.Z));
                var width = SectionValue(fi, SectionWidthNames) ?? 0.0;
                var name = fi.Symbol != null ? fi.Symbol.FamilyName + " " + fi.Symbol.Name : fi.Name;

                var curve = lc.Curve;
                if (curve is Line)
                {
                    var a = curve.GetEndPoint(0); var b = curve.GetEndPoint(1);
                    c.Beams.Add(new StructureGeometry.BeamRecord(name, M(a.X), M(a.Y), M(b.X), M(b.Y), width, depth, top));
                }
                else
                {
                    var t = curve.Tessellate();
                    for (var i = 0; i < t.Count - 1; i++)
                        c.Beams.Add(new StructureGeometry.BeamRecord(name, M(t[i].X), M(t[i].Y), M(t[i + 1].X), M(t[i + 1].Y), width, depth, top));
                }
            }
        }

        static void ReadWalls(Collected c, Document doc, ElementFilter near, double roofTopM, HashSet<long>? picked)
        {
            foreach (var wall in new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Walls).WhereElementIsNotElementType().WherePasses(near).OfType<Wall>())
            {
                if (picked != null && !picked.Contains(wall.Id.Value)) continue;
                var bb = wall.get_BoundingBox(null);
                if (bb == null || wall.Location is not LocationCurve lc) continue;
                var top = M(bb.Max.Z);
                var height = top - M(bb.Min.Z);
                // its top meets the roof and it comes from below: a parapet standing on the roof is not a wall under it (a wall the designer selected is taken as it is)
                if (picked == null && (top < roofTopM - WallBelowM || top > roofTopM + WallAboveM || height < WallMinHeightM || M(bb.Min.Z) > roofTopM - WallMinHeightM)) continue;

                var bearing = wall.StructuralUsage != StructuralWallUsage.NonBearing;
                var thickness = M(wall.Width);
                var curve = lc.Curve;
                if (curve is Line)
                {
                    var a = curve.GetEndPoint(0); var b = curve.GetEndPoint(1);
                    c.Walls.Add(new StructureGeometry.WallRecord(wall.Name, M(a.X), M(a.Y), M(b.X), M(b.Y), thickness, height, bearing));
                }
                else
                {
                    var t = curve.Tessellate();
                    for (var i = 0; i < t.Count - 1; i++)
                        c.Walls.Add(new StructureGeometry.WallRecord(wall.Name, M(t[i].X), M(t[i].Y), M(t[i + 1].X), M(t[i + 1].Y), thickness, height, bearing));
                }
            }
        }
    }
}
