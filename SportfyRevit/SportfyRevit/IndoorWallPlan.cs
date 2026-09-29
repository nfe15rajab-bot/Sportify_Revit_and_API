namespace SportfyRevit
{
    /// <summary>
    /// Which walls the indoor zone becomes in Revit, and where its door goes (Revit-free, so Tools/AddinCheck checks it).
    ///
    /// The web app sends the indoor zone's wall as it draws it (algoPlacementCore.js buildIndoorWall): full-thickness strips, with the side that has the
    /// door already cut in two round the door's opening. A Revit door needs a wall to sit in, so when there is a door family those two strips and the
    /// opening between them become ONE wall, and the door is placed in it; when there is none, the two strips stay apart and the opening stays open.
    /// Every strip becomes a wall along its long side, on its centre line, as thick as the strip is.
    /// </summary>
    internal static class IndoorWallPlan
    {
        /// <summary>The wall type's name, "Sportify - Indoor Zone Wall (30 cm)". Not "[30 cm]" like the family names: Revit refuses brackets in a type
        /// name, and the walls were never built in live Revit while it had them (2026-09-29, "name cannot include prohibited characters").</summary>
        internal static string WallTypeName(double thicknessM) => $"Sportify - Indoor Zone Wall ({(int)Math.Round(thicknessM * 100)} cm)";

        internal sealed record Segment(double X0, double Y0, double X1, double Y1, double ThicknessM, bool HostsDoor);
        internal sealed record DoorAt(double X, double Y, double WidthM, string Side);
        internal sealed record Plan(List<Segment> Walls, DoorAt? Door);

        const double Eps = 1e-6;

        /// <param name="rects">[x0, y0, x1, y1] strips, plan metres</param>
        /// <param name="door">the opening [x0, y0, x1, y1] and its side, or null</param>
        /// <param name="withDoor">whether a door family is at hand: the door's side is built whole and the door is placed in it</param>
        internal static Plan Make(IReadOnlyList<double[]> rects, double[]? door, string? side, double thicknessM, bool withDoor)
        {
            var strips = rects.Where(r => r.Length >= 4 && r[2] - r[0] > Eps && r[3] - r[1] > Eps).Select(r => (double[])r.Clone()).ToList();
            DoorAt? doorAt = null;
            var hostIndex = -1;

            if (door != null && door.Length >= 4 && withDoor)
            {
                bool horizontal = door[2] - door[0] >= door[3] - door[1];
                // the strips on the door's own band that touch its opening: the two halves of the side it was cut out of
                bool sameBand(double[] r) => horizontal ? Math.Abs(r[1] - door[1]) < 1e-3 && Math.Abs(r[3] - door[3]) < 1e-3
                                                        : Math.Abs(r[0] - door[0]) < 1e-3 && Math.Abs(r[2] - door[2]) < 1e-3;
                bool touches(double[] r) => horizontal ? Math.Abs(r[2] - door[0]) < 1e-3 || Math.Abs(r[0] - door[2]) < 1e-3
                                                       : Math.Abs(r[3] - door[1]) < 1e-3 || Math.Abs(r[1] - door[3]) < 1e-3;
                var halves = strips.Where(r => sameBand(r) && touches(r)).ToList();
                var merged = new[]
                {
                    halves.Select(r => r[0]).Append(door[0]).Min(), halves.Select(r => r[1]).Append(door[1]).Min(),
                    halves.Select(r => r[2]).Append(door[2]).Max(), halves.Select(r => r[3]).Append(door[3]).Max(),
                };
                foreach (var h in halves) strips.Remove(h);
                strips.Add(merged);
                hostIndex = strips.Count - 1;
                doorAt = new DoorAt((door[0] + door[2]) / 2, (door[1] + door[3]) / 2, horizontal ? door[2] - door[0] : door[3] - door[1], side ?? "");
            }

            var walls = strips.Select((r, i) =>
            {
                double w = r[2] - r[0], h = r[3] - r[1];
                bool alongX = w >= h;
                double cx = (r[0] + r[2]) / 2, cy = (r[1] + r[3]) / 2;
                double t = Math.Round(thicknessM > 0 ? Math.Min(thicknessM, alongX ? h : w) : (alongX ? h : w), 3);
                return alongX ? new Segment(r[0], cy, r[2], cy, t, i == hostIndex) : new Segment(cx, r[1], cx, r[3], t, i == hostIndex);
            }).ToList();
            return new Plan(walls, doorAt);
        }
    }
}
