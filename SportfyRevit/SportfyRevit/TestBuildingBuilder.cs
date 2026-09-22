using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Structure;

namespace SportfyRevit
{
    /// <summary>
    /// TEST-ONLY scaffolding (like SPORTIFY_INSPECT_TEMPLATES or SPORTIFY_BUILD_TEMPLATE): builds a small building — two levels, a rectangle of four
    /// bearing walls, a flat roof over them, a 2x2 grid and a door — entirely from native Revit types, so it needs no family loaded beyond what a
    /// fresh project already has. Exists so the Worksets command and "Push to Sportify by workset" can be live-tested against real Revit elements
    /// without hand-drawing a building first. Never called from the ribbon; only from SPORTIFY_BUILD_TEST_BUILDING (SportfyRevitApp.cs).
    /// </summary>
    internal static class TestBuildingBuilder
    {
        internal sealed record Result(List<string> Made, List<string> Notes);

        /// <summary>Builds inside a transaction the caller starts and commits. Feet throughout, Revit's own unit.</summary>
        public static Result Build(Document doc)
        {
            var made = new List<string>();
            var notes = new List<string>();

            var l1 = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation).First();
            var roofElevationFt = l1.Elevation + UnitUtils.ConvertToInternalUnits(4.0, UnitTypeId.Meters);
            var roofLevel = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().FirstOrDefault(l => Math.Abs(l.Elevation - roofElevationFt) < 0.1)
                ?? Level.Create(doc, roofElevationFt);
            if (roofLevel.Name == "Level 2" || string.IsNullOrEmpty(roofLevel.Name)) { try { roofLevel.Name = "Sportify Test Roof"; } catch (Exception) { } }
            made.Add("Level: " + roofLevel.Name);

            double w = UnitUtils.ConvertToInternalUnits(10.0, UnitTypeId.Meters), d = UnitUtils.ConvertToInternalUnits(8.0, UnitTypeId.Meters);
            var p00 = new XYZ(0, 0, 0); var p10 = new XYZ(w, 0, 0); var p11 = new XYZ(w, d, 0); var p01 = new XYZ(0, d, 0);

            // ---- grid: a 2x2 grid over the footprint (native, no family needed)
            try
            {
                Grid.Create(doc, Line.CreateBound(new XYZ(0, -3, 0), new XYZ(0, d + 3, 0)));
                Grid.Create(doc, Line.CreateBound(new XYZ(w, -3, 0), new XYZ(w, d + 3, 0)));
                Grid.Create(doc, Line.CreateBound(new XYZ(-3, 0, 0), new XYZ(w + 3, 0, 0)));
                Grid.Create(doc, Line.CreateBound(new XYZ(-3, d, 0), new XYZ(w + 3, d, 0)));
                made.Add("Grid: 2 x 2 (4 lines)");
            }
            catch (Exception ex) { notes.Add("Grid: " + ex.Message); }

            // ---- four bearing walls around the footprint, L1 to the roof level
            var wallTypeId = new FilteredElementCollector(doc).OfClass(typeof(WallType)).Cast<WallType>().FirstOrDefault(t => t.Kind == WallKind.Basic)?.Id;
            var walls = new List<Wall>();
            if (wallTypeId != null)
            {
                foreach (var (a, b) in new[] { (p00, p10), (p10, p11), (p11, p01), (p01, p00) })
                {
                    try
                    {
                        var wall = Wall.Create(doc, Line.CreateBound(a, b), wallTypeId, l1.Id, roofElevationFt - l1.Elevation, 0, false, true);
                        try { wall.get_Parameter(BuiltInParameter.WALL_STRUCTURAL_SIGNIFICANT)?.Set(1); } catch (Exception) { }
                        walls.Add(wall);
                    }
                    catch (Exception ex) { notes.Add("Wall: " + ex.Message); }
                }
                if (walls.Count > 0) made.Add($"Walls: {walls.Count} bearing wall(s)");
            }
            else notes.Add("No basic wall type in this project: no walls, roof or door.");

            // ---- a door in the first wall, if the project has a door family loaded (the default template usually does)
            if (walls.Count > 0)
            {
                try
                {
                    var doorSymbol = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).OfCategory(BuiltInCategory.OST_Doors).Cast<FamilySymbol>().FirstOrDefault();
                    if (doorSymbol != null)
                    {
                        if (!doorSymbol.IsActive) doorSymbol.Activate();
                        var mid = new XYZ((p00.X + p10.X) / 2, p00.Y, l1.Elevation);
                        doc.Create.NewFamilyInstance(mid, doorSymbol, walls[0], l1, StructuralType.NonStructural);
                        made.Add("Door: 1 (" + doorSymbol.Family.Name + ")");
                    }
                    else notes.Add("No door family loaded in this project: no door made.");
                }
                catch (Exception ex) { notes.Add("Door: " + ex.Message); }
            }

            // ---- a flat roof by footprint over the walls, at the roof level
            if (walls.Count == 4)
            {
                try
                {
                    var roofType = new FilteredElementCollector(doc).OfClass(typeof(RoofType)).Cast<RoofType>().FirstOrDefault();
                    if (roofType != null)
                    {
                        var footprint = new CurveArray();
                        foreach (var (a, b) in new[] { (p00, p10), (p10, p11), (p11, p01), (p01, p00) }) footprint.Append(Line.CreateBound(a, b));
#pragma warning disable CS0618 // NewFootPrintRoof: the simplest reliable way to make a flat test roof; a real Sportify project never calls this
                        var roof = doc.Create.NewFootPrintRoof(footprint, roofLevel, roofType, out var mca);
#pragma warning restore CS0618
                        foreach (ModelCurve mc in mca) { try { roof.set_DefinesSlope(mc, false); } catch (Exception) { } }     // flat, not hipped
                        made.Add("Roof: 1 (flat, " + roofType.Name + ")");
                    }
                    else notes.Add("No roof type in this project: no roof made.");
                }
                catch (Exception ex) { notes.Add("Roof: " + ex.Message); }
            }

            SportifyLog.Info("test-building", $"built: {string.Join("; ", made)}" + (notes.Count > 0 ? $"; notes: {string.Join("; ", notes)}" : ""));
            return new Result(made, notes);
        }
    }
}
