using System;
using System.Collections.Generic;

namespace Sportify.Simulation.Sun
{
    /// <summary>
    /// Where the louvre is hosted: overhead (blades in a horizontal plane, a pergola or canopy), or on a vertical plane (a screen on a railing or a wall) with
    /// the blades either horizontal slats stacked up the wall, tipped open together by one rod (Vertical), or full-height fins side by side, turning about
    /// vertical axes (VerticalFins).
    /// </summary>
    public enum LouvreHost { Horizontal, Vertical, VerticalFins }

    /// <summary>
    /// Kinetics: what an operable louvre (the "pergola" equipment type from SunShadeCore's catalogue -- the one piece of shading equipment the sun &amp; shade
    /// analysis already marks "louvres closed at midday" -- or a screen on a railing or a wall) does with the sun's own position. Sails, parasols and trees are
    /// static shading, not kinetic, and are left alone -- only a louvre has blades an actuator can turn.
    ///
    /// Unity-free (plain arithmetic on SunShadeCore's SunModel), like the rest of this folder: the Revit add-in calls it directly, Unity only renders the states
    /// it returns.
    /// </summary>
    public static class LouvreActuationModel
    {
        /// <summary>One point in the day: the sun's own position, and the blade opening that answers it.</summary>
        public sealed class ActuationState
        {
            public string Label;               // "morning" | "solar noon" | "afternoon" (overhead), "first sun" | "peak sun" | "last sun" (vertical)
            public double SolarTimeH;
            public double SunElevationDeg;
            /// <summary>0 = closed (a horizontal host: blades flat, roof-like; a vertical host: blades in the wall's plane), 90 = open (blades on edge, in the sun's own direction).</summary>
            public double LouvreOpenAngleDeg;
            /// <summary>The sun's elevation seen in the plane across the blade axis: what the blades actually face (equals the elevation only when the sun is square to the blades).</summary>
            public double ProfileAngleDeg;
            /// <summary>
            /// The horizontal unit vector (in the plan, x right, y down) that the blade's face turns toward as it opens: toward the sun for an overhead host, the wall's
            /// outward normal for a slat screen (the face tips up toward the sun), the direction along the wall on the sun's side for a fin screen (the face turns
            /// sideways toward the sun).
            /// </summary>
            public double LeanX, LeanY;
        }

        /// <summary>
        /// Solar-tracking control law for an OVERHEAD host: each blade is turned FACE-ON to the sun's rays (in the plane across the blade axis), the
        /// maximum-shade tracking mode. A blade at rest is flat (0 deg, closed -- a near-solid roof, the catalogue's "louvres closed at midday"); a blade at
        /// 90 deg stands on edge. The blade's face is square to a ray when it is tilted (90 - profile angle) from flat: open_deg = 90 - clamp(angle, 0, 90).
        /// So a sun overhead keeps the blades flat, and a low morning or evening sun stands them nearly upright, still face-on to it
        /// (LouvreMechanics.SunStoppedShare puts a number on what each angle stops). Al Bahar Towers' units follow the same idea, with a control per facade; a
        /// single shared angle for every unit on the roof is this model's own simplification. A screening choice, not a shading-optimisation result --
        /// PRELIMINARY, like the rest of the sun &amp; shade model.
        /// </summary>
        public static double OpenAngleDegForElevation(double elevationDeg) => 90.0 - Math.Max(0.0, Math.Min(90.0, elevationDeg));

        /// <summary>
        /// The same law for a VERTICAL host (blades stacked on a wall or railing, axes horizontal): a blade at rest hangs in the wall's plane, and to face the sun
        /// it tips (its face turns up toward the sun) by the sun's profile angle over the wall: open_deg = clamp(profile angle, 0, 90).
        /// </summary>
        public static double OpenAngleDegForProfileVertical(double profileDeg) => Math.Max(0.0, Math.Min(90.0, profileDeg));

        /// <summary>
        /// Three states across the summer design day (21 June, the sun &amp; shade analysis's own heat-window day) for an overhead louvre whose blades run square to
        /// the sun's path: two hours either side of the 11:00-16:00 heat window, and its middle -- open, closing toward midday, open again. Enough to show the
        /// mechanism actually move without re-running the whole roof sweep. (StatesFor does the same for a real blade axis.)
        /// </summary>
        public static List<ActuationState> DesignDayStates(double latitudeDeg)
        {
            var dayOfYear = SunModel.DayOfYear[0]; // 21 June, same design day the heat window (11:00-16:00) is defined on
            var hours = new[] { SunModel.WindowFromH - 2.0, (SunModel.WindowFromH + SunModel.WindowToH) / 2.0, SunModel.WindowToH + 2.0 };
            var labels = new[] { "morning", "solar noon", "afternoon" };

            var states = new List<ActuationState>();
            for (var i = 0; i < hours.Length; i++)
            {
                var sun = SunModel.SunAt(latitudeDeg, dayOfYear, hours[i]);
                var elevation = sun.Up ? sun.ElevationDeg : 0.0;
                states.Add(new ActuationState
                {
                    Label = labels[i],
                    SolarTimeH = hours[i],
                    SunElevationDeg = Math.Round(elevation, 1),
                    ProfileAngleDeg = Math.Round(elevation, 1),
                    LouvreOpenAngleDeg = Math.Round(OpenAngleDegForElevation(elevation), 1),
                });
            }
            return states;
        }

        /// <summary>
        /// The actuation states of a louvre with a REAL orientation, in the plan the analysis works in (x right, y down, the plan's top facing <paramref name="northDeg"/>).
        /// <paramref name="axisX"/>/<paramref name="axisY"/> is the blades' axis (a unit vector, horizontal); for a vertical host <paramref name="normalX"/>/<paramref name="normalY"/>
        /// is the wall's outward normal, the side the sun has to be on. An overhead host gets its three states across the design day; a vertical host gets
        /// the first, the highest and the last sun that is in front of the wall (none, for a wall the sun never reaches).
        /// </summary>
        public static List<ActuationState> StatesFor(LouvreHost host, double latitudeDeg, double northDeg, double axisX, double axisY, double normalX, double normalY)
        {
            var day = SunModel.DayOfYear[0];
            var states = new List<ActuationState>();
            var al = Math.Sqrt(axisX * axisX + axisY * axisY); if (al < 1e-9) { axisX = 1; axisY = 0; al = 1; }
            axisX /= al; axisY /= al;

            if (host == LouvreHost.Horizontal)
            {
                var hours = new[] { SunModel.WindowFromH - 2.0, (SunModel.WindowFromH + SunModel.WindowToH) / 2.0, SunModel.WindowToH + 2.0 };
                var labels = new[] { "morning", "solar noon", "afternoon" };
                for (var i = 0; i < hours.Length; i++)
                {
                    var sun = SunModel.SunAt(latitudeDeg, day, hours[i]);
                    var elevation = sun.Up ? sun.ElevationDeg : 0.0;
                    double tx, ty; SunModel.TowardSun(sun, northDeg, out tx, out ty);
                    var alongAxis = tx * axisX + ty * axisY;
                    var perpX = tx - alongAxis * axisX; var perpY = ty - alongAxis * axisY;
                    var perp = Math.Sqrt(perpX * perpX + perpY * perpY);          // |sin| of the angle between the sun's bearing and the blade axis
                    double profile;
                    if (perp < 1e-3) { perpX = -axisY; perpY = axisX; profile = 90.0; }     // the sun along the blades: seen from overhead
                    else { perpX /= perp; perpY /= perp; profile = Math.Atan2(Math.Sin(elevation * Math.PI / 180.0), Math.Cos(elevation * Math.PI / 180.0) * perp) * 180.0 / Math.PI; }
                    states.Add(new ActuationState
                    {
                        Label = labels[i], SolarTimeH = hours[i], SunElevationDeg = Math.Round(elevation, 1), ProfileAngleDeg = Math.Round(profile, 1),
                        LouvreOpenAngleDeg = Math.Round(OpenAngleDegForElevation(profile), 1), LeanX = perpX, LeanY = perpY,
                    });
                }
                return states;
            }

            var nl = Math.Sqrt(normalX * normalX + normalY * normalY); if (nl < 1e-9) { normalX = 0; normalY = 1; nl = 1; }
            normalX /= nl; normalY /= nl;
            var fins = host == LouvreHost.VerticalFins;
            ActuationState first = null, last = null, peak = null;
            for (var h = 5.0; h <= 21.0 + 1e-9; h += 0.25)
            {
                var sun = SunModel.SunAt(latitudeDeg, day, h);
                if (!sun.Up) continue;
                double tx, ty; SunModel.TowardSun(sun, northDeg, out tx, out ty);
                var front = tx * normalX + ty * normalY;                            // how squarely the sun faces the wall
                if (front < 0.05) continue;
                double profile, leanX = normalX, leanY = normalY;
                if (fins)
                {
                    // fins turn about vertical axes: what they face is the sun's direction in the horizontal plane, its angle from the wall's normal
                    var wallX = -normalY; var wallY = normalX;                       // along the wall
                    var side = tx * wallX + ty * wallY;                              // which way along the wall the sun leans
                    profile = Math.Atan2(Math.Abs(side), front) * 180.0 / Math.PI;
                    var sg = side >= 0 ? 1.0 : -1.0;
                    leanX = sg * wallX; leanY = sg * wallY;
                }
                else profile = Math.Atan2(Math.Sin(sun.ElevationDeg * Math.PI / 180.0), Math.Cos(sun.ElevationDeg * Math.PI / 180.0) * front) * 180.0 / Math.PI;
                var s = new ActuationState
                {
                    SolarTimeH = h, SunElevationDeg = Math.Round(sun.ElevationDeg, 1), ProfileAngleDeg = Math.Round(profile, 1),
                    LouvreOpenAngleDeg = Math.Round(OpenAngleDegForProfileVertical(profile), 1), LeanX = leanX, LeanY = leanY,
                };
                if (first == null) first = s;
                last = s;
                if (peak == null || s.SunElevationDeg > peak.SunElevationDeg) peak = s;
            }
            if (first == null) return states;
            if (ReferenceEquals(first, last)) { first.Label = "peak sun"; states.Add(first); return states; }
            first.Label = "first sun"; last.Label = "last sun";
            states.Add(first);
            if (!ReferenceEquals(peak, first) && !ReferenceEquals(peak, last)) { peak.Label = "peak sun"; states.Add(peak); }
            states.Add(last);
            return states;
        }
    }
}
