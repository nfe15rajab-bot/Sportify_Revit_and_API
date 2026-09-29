using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;

namespace SportfyRevit
{
    /// <summary>
    /// The indoor zone's walls and door, built as Revit walls (user, 2026-09-29: "build walls and door in that place where there is extrusion and slab"):
    /// the web app draws a real wall round the indoor zone (the locker and bathroom modules) with a door onto the paths, and until now it never reached
    /// Revit. IndoorWallPlan decides the walls; this makes them - a basic wall type "Sportify - Indoor Zone Wall [30 cm]" (made from the project's first
    /// basic wall when it is not there yet), on the level at or below the roof, standing on the roof's top, IndoorWallHeightM high - and places the
    /// door (SportifyDoorFamily, settled before the import) in the wall that holds it. Runs inside the import's transaction; everything it makes goes on
    /// the Sports workset and into the import's ledger, so the next import replaces it.
    /// </summary>
    internal static class IndoorWallBuilder
    {
        /// <summary>The walls' height above the roof: a room for the modules, not a structural storey. The assistant's figure, awaiting the team's review.</summary>
        internal const double IndoorWallHeightM = 3.0;

        internal static int Build(Document doc, SportifyLayout layout, WorksetId worksetId, List<ElementId> createdIds, FamilySymbol? door, string doorNote)
        {
            var sets = layout.IndoorWalls ?? new List<IndoorWallDto>();
            if (sets.Count == 0) return 0;

            double zFt = SportifyLayoutBuilder.CurrentOriginZFt;
            var level = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                            .Where(l => l.Elevation <= zFt + 0.01).OrderByDescending(l => l.Elevation).FirstOrDefault()
                        ?? new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation).FirstOrDefault();
            if (level == null) { ImportDiagnostics.Note("the indoor zone's walls were not built: the project has no level"); return 0; }
            double offsetFt = zFt - level.Elevation;
            double heightFt = SportifyLayoutBuilder.FeetFromMeters(IndoorWallHeightM);

            int walls = 0;
            foreach (var set in sets)
            {
                var rects = (set.RectsM ?? new List<List<double>>()).Where(r => r.Count >= 4).Select(r => r.ToArray()).ToList();
                double[]? opening = set.Door == null ? null : new[] { set.Door.X0, set.Door.Y0, set.Door.X1, set.Door.Y1 };
                var plan = IndoorWallPlan.Make(rects, opening, set.Door?.Side, set.ThicknessM, door != null);
                if (plan.Walls.Count == 0) continue;

                Wall? host = null;
                foreach (var seg in plan.Walls)
                {
                    // one type per thickness: a strip at the roof's edge is only half as thick (the web app keeps the wall on the roof)
                    var type = WallTypeFor(doc, seg.ThicknessM);
                    if (type == null) { ImportDiagnostics.Note("the indoor zone's walls were not built: the project has no basic wall type to make one from"); return walls; }
                    var (ax, ay) = SportifyLayoutBuilder.PlanToWorldFt(seg.X0, seg.Y0);
                    var (bx, by) = SportifyLayoutBuilder.PlanToWorldFt(seg.X1, seg.Y1);
                    var line = Line.CreateBound(new XYZ(ax, ay, level.Elevation), new XYZ(bx, by, level.Elevation));
                    var wall = Wall.Create(doc, line, type.Id, level.Id, heightFt, offsetFt, false, false);
                    SportifyLayoutBuilder.SetWorkset(wall, worksetId);
                    createdIds.Add(wall.Id);
                    walls++;
                    if (seg.HostsDoor) host = wall;
                }

                string doorText;
                if (plan.Door != null && door != null && host != null)
                {
                    doc.Regenerate();
                    var (dx, dy) = SportifyLayoutBuilder.PlanToWorldFt(plan.Door.X, plan.Door.Y);
                    var instance = doc.Create.NewFamilyInstance(new XYZ(dx, dy, level.Elevation + offsetFt), door, host, level, StructuralType.NonStructural);
                    // a hosted door sits on its level: lift its sill to the roof, where the wall stands
                    instance.get_Parameter(BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM)?.Set(offsetFt);
                    SportifyLayoutBuilder.SetWorkset(instance, worksetId);
                    createdIds.Add(instance.Id);
                    doorText = $"door \"{door.Family?.Name}\" on the {SideWord(plan.Door.Side)} side" + (doorNote.Length > 0 ? $" ({doorNote})" : "");
                }
                else doorText = set.Door == null ? "no door in the layout" : "the door's opening left open" + (doorNote.Length > 0 ? $": {doorNote}" : "");

                ImportDiagnostics.IndoorWallsBuilt(plan.Walls.Count, plan.Walls.Max(w => w.ThicknessM), IndoorWallHeightM, level.Name, doorText);
            }
            return walls;
        }

        static string SideWord(string side) => side switch { "N" => "north (top)", "S" => "south (bottom)", "W" => "west (left)", "E" => "east (right)", _ => side };

        /// <summary>"Sportify - Indoor Zone Wall [30 cm]": found, or made from the project's first basic wall type with one layer of that thickness.</summary>
        static WallType? WallTypeFor(Document doc, double thicknessM)
        {
            int cm = (int)Math.Round(thicknessM * 100);
            string name = $"Sportify - Indoor Zone Wall [{cm} cm]";
            var types = new FilteredElementCollector(doc).OfClass(typeof(WallType)).Cast<WallType>().ToList();
            var found = types.FirstOrDefault(t => t.Name == name);
            if (found != null) return found;
            var basic = types.FirstOrDefault(t => t.Kind == WallKind.Basic);
            if (basic == null) return null;
            var made = (WallType)basic.Duplicate(name);
            made.SetCompoundStructure(CompoundStructure.CreateSingleLayerCompoundStructure(MaterialFunctionAssignment.Structure,
                SportifyLayoutBuilder.FeetFromMeters(Math.Max(0.05, thicknessM)), ElementId.InvalidElementId));
            return made;
        }
    }
}
