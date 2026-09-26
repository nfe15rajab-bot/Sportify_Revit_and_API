using System;
using System.Collections.Generic;
using System.Globalization;

namespace Sportify.Simulation.Sun
{
    /// <summary>One moment of a sail on ground rails: how far its masts have run, the footprint that leaves, and how much of the shade the analysis wanted it still gives.</summary>
    public sealed class SailState
    {
        public string Label;
        public double SolarTimeH, SunElevationDeg;
        public double Scale;                              // how far the moving masts have run: 1 is the analysis's footprint
        public double WidthM, DepthM;                     // the footprint now (a rectangle's sides, a triangle's legs)
        public double AreaM2;
        public double TravelM;                            // how far the furthest mast has run from where it stands at scale 1, along its track
        public double ShadowShiftXM, ShadowShiftYM;       // where the shadow has moved against the middle of the heat window, in the sail's own frame
        public double ShadeHeldPercent;                   // the share of the shade the analysis wanted (the footprint at scale 1) that the shadow covers now
        public double ShadeHeldFixedPercent;              // the same for a sail that never moves
        public bool Storm;
    }

    public sealed class SailReport
    {
        public bool Triangle;
        public double WidthM, DepthM, HeightM, AreaM2;
        public int MastCount = 4, Tracks = 2;
        public double MinScale, MaxScale, AreaMinM2, AreaMaxM2, GardenFreedM2;
        public double RailLengthM;                        // each track, from end to end
        public double TravelM;                            // the run of a moving carriage between the smallest and the largest sail
        public double DesignPressurePa, UpliftKnTotal, UpliftKnPerMast;
        public double PretensionPullKnPerCorner;          // the pull of the fabric on one corner mast at the largest sail
        public double MastBaseMomentKnM;                  // pretension and the wind at the operating limit, per mast
        public double MastStressMpa, MastYieldMpa, MastUtilisationPercent;
        public bool MastOk;
        public double RecommendedMastDiameterM;           // the smallest standard-step tube that passes (the diameter in use when it does)
        public double CarriageForceKn, DriveSpeedMs, TravelSeconds, DrivePowerW;
        public double StormHeightM, StormSeconds;
        public int MastStages = 1;                        // 1 = a rigid tube
        public double MastStageM, MastCollapsedTopM;      // the length of one telescope stage, and the height of the top of the mast when it is fully in
        public double FabricMassKg, MastMassKgEach;
        public double ShadeFixedMinPercent, ShadeMovingMinPercent, ShadeFixedMeanPercent, ShadeMovingMeanPercent;
        public List<SailState> States = new List<SailState>();
        public List<string> Findings = new List<string>();
    }

    /// <summary>
    /// A tensile shade sail on MOVABLE pillars (the sun and shade analysis's own "sail", made kinetic): steel masts that stand on carriages and slide along ground rails,
    /// a fabric between their tops. The rails trace the shape on the ground: two parallel tracks for a rectangle (it grows and shrinks along them), an L of two tracks for
    /// a right triangle (it grows and shrinks in both axes, from the corner that stays put). The analysis places the sail so that its shadow covers the zone best at the
    /// middle of the heat window; the sun moves, and the shadow of a sail 3.5 m up moves H cot(elevation) across the ground. Running the masts out spreads the sail over
    /// the shadow's new place, so the zone stays covered; running them in draws the sail back from a garden that needs the sun. Plain arithmetic on the sun model; no Unity, no Revit.
    /// </summary>
    public static class SailMechanics
    {
        public const double Deg = Math.PI / 180.0;

        // ------------------------------------------------------------------ footprints and how much of the wanted shade they give

        /// <summary>The sail's footprint in its own frame (x along the rails, y across them) at a scale, counter-clockwise.</summary>
        public static List<double[]> Footprint(bool triangle, double w, double d, double anchorSide, double scale)
        {
            var p = new List<double[]>();
            if (triangle) { p.Add(new[] { 0.0, 0.0 }); p.Add(new[] { scale * w, 0.0 }); p.Add(new[] { 0.0, scale * d }); return p; }
            double x1 = anchorSide > 0 ? 0 : w / 2 - scale * w / 2, x2 = anchorSide > 0 ? scale * w : w / 2 + scale * w / 2;
            p.Add(new[] { x1, 0.0 }); p.Add(new[] { x2, 0.0 }); p.Add(new[] { x2, d }); p.Add(new[] { x1, d });
            return p;
        }

        static double Area(List<double[]> poly)
        {
            double a = 0;
            for (var i = 0; i < poly.Count; i++) { var p = poly[i]; var q = poly[(i + 1) % poly.Count]; a += p[0] * q[1] - q[0] * p[1]; }
            return Math.Abs(a) / 2;
        }

        static List<double[]> Shift(List<double[]> poly, double dx, double dy)
        {
            var r = new List<double[]>();
            foreach (var p in poly) r.Add(new[] { p[0] + dx, p[1] + dy });
            return r;
        }

        /// <summary>The area two convex counter-clockwise polygons have in common (Sutherland-Hodgman: the first clipped by each edge of the second).</summary>
        public static double IntersectionArea(List<double[]> subject, List<double[]> clip)
        {
            var output = new List<double[]>(subject);
            for (var i = 0; i < clip.Count && output.Count > 0; i++)
            {
                var a = clip[i]; var b = clip[(i + 1) % clip.Count];
                var input = output; output = new List<double[]>();
                double Side(double[] p) => (b[0] - a[0]) * (p[1] - a[1]) - (b[1] - a[1]) * (p[0] - a[0]);          // >= 0: inside (left of the edge)
                for (var j = 0; j < input.Count; j++)
                {
                    var cur = input[j]; var prev = input[(j + input.Count - 1) % input.Count];
                    double sc = Side(cur), sp = Side(prev);
                    if (sc >= 0)
                    {
                        if (sp < 0) output.Add(Cross(prev, cur, sp, sc));
                        output.Add(cur);
                    }
                    else if (sp >= 0) output.Add(Cross(prev, cur, sp, sc));
                }
            }
            return output.Count < 3 ? 0 : Area(output);
        }

        static double[] Cross(double[] p, double[] q, double sp, double sc)
        {
            var t = sp / (sp - sc);
            return new[] { p[0] + (q[0] - p[0]) * t, p[1] + (q[1] - p[1]) * t };
        }

        /// <summary>The share (0 to 1) of the shade the analysis wanted, the footprint at scale 1, that a sail of this scale still covers when its shadow has moved by (shiftX, shiftY).</summary>
        public static double Held(bool triangle, double w, double d, double anchorSide, double scale, double shiftX, double shiftY)
        {
            var wanted = Footprint(triangle, w, d, anchorSide, 1.0);
            var shadow = Shift(Footprint(triangle, w, d, anchorSide, scale), shiftX, shiftY);
            return IntersectionArea(shadow, wanted) / Area(wanted);
        }

        // ------------------------------------------------------------------ the states

        /// <summary>
        /// Five states across the summer design day: before the heat window (the sail run in: a garden nearby gets the sun), its start, its middle (the reference: the sail as the
        /// analysis placed it), its end, and a storm (run in and the masts lowered). The shadow's shift against the reference is computed in the sail's own frame; the scale of the
        /// start and the end is the smallest at which the sail still covers <c>sail_cover_target_percent</c> of the shade the analysis wanted (the most it can, when the tracks end first).
        /// </summary>
        /// <param name="anchorSide">&gt; 0: the masts at x = 0 stay put and the others run out toward +x; 0: a rectangle grows from its middle. (A triangle always grows from its corner at the origin.)</param>
        /// <param name="xPlanX">The plan direction (x right, y down) of the sail's own x axis, and of its y axis (<paramref name="yPlanX"/>, <paramref name="yPlanY"/>): unit vectors.</param>
        public static List<SailState> States(LouvreDesign d, double latitudeDeg, double northDeg, bool triangle, double widthM, double depthM, double heightM, double anchorSide,
                                             double xPlanX, double xPlanY, double yPlanX, double yPlanY)
        {
            var day = SunModel.DayOfYear[0];
            var minScale = Math.Min(1.0, d["sail_min_scale"]); var maxScale = Math.Max(1.0, d["sail_max_scale"]);
            var target = Math.Max(0.05, Math.Min(1.0, d["sail_cover_target_percent"] / 100.0));
            var hours = new[] { SunModel.WindowFromH - 2.0, SunModel.WindowFromH, (SunModel.WindowFromH + SunModel.WindowToH) / 2.0, SunModel.WindowToH };
            var labels = new[] { "before the heat window (sail run in)", "start of the heat window", "middle of the heat window", "end of the heat window" };

            void Shadow(double hour, out double sx, out double sy, out double elevation)
            {
                var sun = SunModel.SunAt(latitudeDeg, day, hour);
                double tx, ty; SunModel.TowardSun(sun, northDeg, out tx, out ty);
                var cot = 1.0 / Math.Tan(Math.Max(sun.ElevationDeg, 3.0) * Deg);
                sx = heightM * cot * tx; sy = heightM * cot * ty;
                elevation = sun.Up ? sun.ElevationDeg : 0.0;
            }
            double refX, refY, refE; Shadow(hours[2], out refX, out refY, out refE);

            var list = new List<SailState>();
            for (var i = 0; i < hours.Length; i++)
            {
                double px, py, elev; Shadow(hours[i], out px, out py, out elev);
                // the shadow has moved away from the sun by the difference in H cot(elevation) (the sail's own place is unchanged): in the sail's frame
                double dx = -(px - refX), dy = -(py - refY);
                double lx = dx * xPlanX + dy * xPlanY, ly = dx * yPlanX + dy * yPlanY;
                var fixedHeld = Held(triangle, widthM, depthM, anchorSide, 1.0, lx, ly);
                double scale = i == 0 ? minScale : 1.0, held = Held(triangle, widthM, depthM, anchorSide, scale, lx, ly);
                if (i == 1 || i == 3)
                {
                    // the smallest scale from 1 to the tracks' end that covers the target; the best it can do when none does
                    double bestScale = 1.0, bestHeld = fixedHeld;
                    scale = 0; held = 0;
                    for (var s = 1.0; s <= maxScale + 1e-9; s += 0.01)
                    {
                        var h = Held(triangle, widthM, depthM, anchorSide, s, lx, ly);
                        if (h > bestHeld + 1e-9) { bestHeld = h; bestScale = s; }
                        if (h >= target) { scale = s; held = h; break; }
                    }
                    if (scale == 0) { scale = bestScale; held = bestHeld; }
                }
                list.Add(MakeState(labels[i], hours[i], elev, triangle, widthM, depthM, anchorSide, scale, lx, ly, held, fixedHeld, false));
            }
            list.Add(MakeState("storm (sail run in, masts lowered)", hours[2], Math.Round(refE, 1), triangle, widthM, depthM, anchorSide, minScale, 0, 0, Held(triangle, widthM, depthM, anchorSide, minScale, 0, 0), 1.0, true));
            return list;
        }

        static SailState MakeState(string label, double hour, double elevation, bool triangle, double w, double d, double anchorSide, double scale, double lx, double ly, double held, double fixedHeld, bool storm)
        {
            var width = triangle ? scale * w : scale * w;
            var depth = triangle ? scale * d : d;
            var area = triangle ? width * depth / 2 : width * depth;
            var travel = Math.Abs(scale - 1.0) * (triangle ? Math.Max(w, d) : (anchorSide > 0 ? w : w / 2));
            return new SailState
            {
                Label = label, SolarTimeH = Math.Round(hour, 2), SunElevationDeg = Math.Round(elevation, 1), Scale = Math.Round(scale, 2),
                WidthM = width, DepthM = depth, AreaM2 = area, TravelM = travel, ShadowShiftXM = lx, ShadowShiftYM = ly,
                ShadeHeldPercent = Math.Round(100 * held, 1), ShadeHeldFixedPercent = Math.Round(100 * fixedHeld, 1), Storm = storm,
            };
        }

        static double SectionModulusM3(double outerM, double wallM)
        {
            var inner = Math.Max(0, outerM - 2 * wallM);
            return Math.PI / 32.0 * (Math.Pow(outerM, 4) - Math.Pow(inner, 4)) / outerM;
        }

        static double AreaM2(double outerM, double wallM)
        {
            var inner = Math.Max(0, outerM - 2 * wallM);
            return Math.PI / 4.0 * (outerM * outerM - inner * inner);
        }

        /// <param name="widthM">The sail's footprint from the analysis, along the rails (a triangle's leg along x); depth across them (a triangle's leg along y).</param>
        /// <param name="heightM">The mast height (the analysis's own height for the piece).</param>
        /// <param name="designPressurePa">The peak wind pressure at the roof.</param>
        /// <param name="upliftKn">The wind uplift on the sail's anchors from the sun and shade analysis, for the whole sail at its analysed size (0 to compute it from the pressure).</param>
        public static SailReport Analyse(LouvreDesign d, bool triangle, double widthM, double depthM, double heightM, double anchorSide, double designPressurePa, double upliftKn, IList<SailState> states)
        {
            var r = new SailReport { Triangle = triangle, MastCount = triangle ? 3 : 4, WidthM = widthM, DepthM = depthM, HeightM = heightM, DesignPressurePa = Math.Max(0, designPressurePa) };
            var sf = d["safety_factor"];
            var dia = d["mast_diameter_m"]; var wall = d["mast_wall_m"];
            var rho = d["air_density_kg_m3"];
            r.MinScale = Math.Min(1.0, d["sail_min_scale"]); r.MaxScale = Math.Max(1.0, d["sail_max_scale"]);
            double AreaAt(double s) => triangle ? s * widthM * s * depthM / 2 : s * widthM * depthM;
            r.AreaM2 = AreaAt(1); r.AreaMinM2 = AreaAt(r.MinScale); r.AreaMaxM2 = AreaAt(r.MaxScale);
            r.GardenFreedM2 = r.AreaM2 - r.AreaMinM2;
            r.Tracks = 2;
            r.RailLengthM = (triangle ? Math.Max(widthM, depthM) : widthM) * r.MaxScale + 0.5;
            r.TravelM = (triangle ? Math.Max(widthM, depthM) : (anchorSide > 0 ? widthM : widthM / 2)) * (r.MaxScale - r.MinScale);

            var operatingQ = 0.5 * rho * d["stow_wind_speed_ms"] * d["stow_wind_speed_ms"];
            r.UpliftKnTotal = upliftKn > 0 ? upliftKn : d["sail_wind_coeff"] * r.DesignPressurePa * r.AreaM2 * 1e-3;
            r.UpliftKnPerMast = r.UpliftKnTotal / r.MastCount;
            var maxW = widthM * r.MaxScale; var maxD = triangle ? depthM * r.MaxScale : depthM;
            r.PretensionPullKnPerCorner = d["sail_pretension_kn_m"] * (maxW + maxD) / 2.0 * 0.7071;
            // the masts telescope: the lowest a sail can go in a storm is the collapsed mast, whatever height was asked for
            r.MastStages = Math.Max(1, (int)Math.Round(d["mast_stages"]));
            var carriageTop = KineticUnits.CarriageTopM(d["sail_rail_size_m"]);
            var topMax = heightM + (triangle ? 0 : d["sail_twist_ratio"] * Math.Min(widthM, depthM) / 2);
            r.MastStageM = KineticUnits.TelescopeStageM(topMax - carriageTop, r.MastStages, d["mast_overlap_m"]);
            r.MastCollapsedTopM = carriageTop + r.MastStageM;
            r.StormHeightM = r.MastStages > 1 ? Math.Max(d["sail_storm_height_m"], Math.Round(r.MastCollapsedTopM + 0.02, 2)) : d["sail_storm_height_m"];
            r.FabricMassKg = d["fabric_mass_kg_m2"] * r.AreaMaxM2;
            if (r.MastStages > 1) { r.MastMassKgEach = 0; for (var i = 0; i < r.MastStages; i++) r.MastMassKgEach += 7850 * AreaM2(Math.Max(0.03, dia - i * KineticUnits.TelescopeStepM), wall) * r.MastStageM; }
            else r.MastMassKgEach = 7850 * AreaM2(dia, wall) * heightM;

            // a mast is a cantilever from its carriage: the fabric pulls its top inward, the wind at the operating limit pushes the whole sail (the drag on the largest sail shared by the masts)
            var windPerMastKn = d["sail_wind_coeff"] * operatingQ * r.AreaMaxM2 * 1e-3 / r.MastCount;
            r.MastBaseMomentKnM = (r.PretensionPullKnPerCorner + windPerMastKn) * heightM;
            var upliftOpKn = d["sail_wind_coeff"] * operatingQ * r.AreaMaxM2 * 1e-3 / r.MastCount;
            double Stress(double diameter) => (r.MastBaseMomentKnM * 1e3 / SectionModulusM3(diameter, wall) + upliftOpKn * 1e3 / AreaM2(diameter, wall)) / 1e6;
            r.MastStressMpa = Stress(dia);
            r.MastYieldMpa = d["steel_yield_pa"] / 1e6;
            r.MastUtilisationPercent = 100 * r.MastStressMpa * sf / r.MastYieldMpa;
            r.MastOk = r.MastUtilisationPercent <= 100;
            r.RecommendedMastDiameterM = dia;
            for (var trial = dia; !r.MastOk && trial < 0.6; trial += 0.0127)          // the next tube in half-inch steps of the outer diameter that passes
            {
                r.RecommendedMastDiameterM = trial;
                if (Stress(trial) * sf <= r.MastYieldMpa) break;
            }

            // the drive: a carriage is pulled along its track against the fabric's pull along it (0.707 of the corner pull), the wind on its share of the sail, and the rolling friction under the mast
            var mastKn = r.MastMassKgEach * 9.81 * 1e-3;
            var pullAlongKn = r.PretensionPullKnPerCorner * 0.7071;
            var drag = 0.5 * windPerMastKn;
            r.DriveSpeedMs = d["sail_drive_speed_ms"];
            r.CarriageForceKn = sf * (pullAlongKn + drag + d["sail_rolling_friction"] * (mastKn + upliftOpKn)) / d["linkage_efficiency"];
            r.TravelSeconds = r.DriveSpeedMs > 0 ? r.TravelM / r.DriveSpeedMs : 0;
            r.DrivePowerW = r.CarriageForceKn * 1e3 * r.DriveSpeedMs;
            r.StormSeconds = r.TravelSeconds + 20.0;                                    // the masts telescope down for another 20 s

            if (states != null)
            {
                foreach (var s in states) r.States.Add(s);
                double f = 0, m = 0, fmin = 100, mmin = 100; var n = 0;
                foreach (var s in states)
                {
                    if (s.Storm || s.Label.StartsWith("before")) continue;         // the heat window itself
                    f += s.ShadeHeldFixedPercent; m += s.ShadeHeldPercent; fmin = Math.Min(fmin, s.ShadeHeldFixedPercent); mmin = Math.Min(mmin, s.ShadeHeldPercent); n++;
                }
                if (n > 0)
                {
                    r.ShadeFixedMeanPercent = Math.Round(f / n, 1); r.ShadeMovingMeanPercent = Math.Round(m / n, 1);
                    r.ShadeFixedMinPercent = fmin; r.ShadeMovingMinPercent = mmin;
                }
            }

            var inv = CultureInfo.InvariantCulture;
            if (r.MastStages > 1)
                r.Findings.Add(string.Format(inv, "Each mast is a telescope of {0} stages of {1:0.00} m (each joint keeps {2:0.00} m inserted at full extension): fully in it is {3:0.00} m tall, so the sail cannot go lower in a storm than {4:0.00} m{5}. The bending check below treats the mast as ONE tube of the base diameter: the thinner upper stages and the joints are not checked (a mechanical engineer has to).",
                    r.MastStages, r.MastStageM, d["mast_overlap_m"], r.MastCollapsedTopM, r.StormHeightM, d["sail_storm_height_m"] + 1e-6 < r.StormHeightM ? " (the " + d["sail_storm_height_m"].ToString("0.0#", inv) + " m entered is below what the masts can collapse to)" : ""));
            r.Findings.Add(string.Format(inv, "{0} steel masts of {1:0} x {2:0.0} mm, {3:0.0} m high, {4:0} kg each, on carriages that run on {5} ground tracks {6:0.0} m long ({7}); the fabric between their tops covers {8:0.0} m2 at the analysed size, {9:0.0} m2 with the masts run in and {10:0.0} m2 run out ({11:0} kg of it).",
                r.MastCount, dia * 1000, wall * 1000, heightM, r.MastMassKgEach, r.Tracks, r.RailLengthM,
                triangle ? "an L: one track along x, one along y, the corner where they meet stays put" : anchorSide > 0 ? "two parallel tracks; the masts at one end stay put, the others run out" : "two parallel tracks; the masts run out from the middle",
                r.AreaM2, r.AreaMinM2, r.AreaMaxM2, r.FabricMassKg));
            if (r.States.Count > 0)
                r.Findings.Add(string.Format(inv, "Following the sun through the heat window the masts run out to {0:0.00} of the analysed size: a sail that does not move gives {1:0}% of the wanted shade on average ({2:0}% at worst), with the masts running {3:0}% ({4:0}% at worst). Run in ({5:0.00}), it frees {6:0.0} m2 for the garden.",
                    MaxScale(r.States), r.ShadeFixedMeanPercent, r.ShadeFixedMinPercent, r.ShadeMovingMeanPercent, r.ShadeMovingMinPercent, r.MinScale, r.GardenFreedM2));
            r.Findings.Add(r.MastOk
                ? string.Format(inv, "Mast bending at the largest sail in wind up to {0:0} m/s: {1:0.0} kN m at the base, {2:0} MPa against {3:0} MPa yield: {4:0}% used with the safety factor.", d["stow_wind_speed_ms"], r.MastBaseMomentKnM, r.MastStressMpa, r.MastYieldMpa, r.MastUtilisationPercent)
                : string.Format(inv, "Mast bending at the largest sail in wind up to {0:0} m/s: {1:0.0} kN m at the base, {2:0} MPa against {3:0} MPa yield, {4:0}% used: too much. A tube of about {5:0} mm outer diameter passes, or a back-stay that takes the pretension, or a smaller largest size.", d["stow_wind_speed_ms"], r.MastBaseMomentKnM, r.MastStressMpa, r.MastYieldMpa, r.MastUtilisationPercent, r.RecommendedMastDiameterM * 1000));
            r.Findings.Add(string.Format(inv, "Drive: {0:0.00} kN at each moving carriage (the fabric's pull, the wind and the rolling friction, with the safety factor), {1:0.0} cm/s: {2:0} s for the full run of {3:0.0} m, about {4:0} W a motor. In a storm (design peak {5:0} Pa, {6:0.0} kN of uplift on the sail) the masts run in, telescope down to {7:0.0} m and the sail is taken in: about {8:0} s.",
                r.CarriageForceKn, r.DriveSpeedMs * 100, r.TravelSeconds, r.TravelM, r.DrivePowerW, r.DesignPressurePa, r.UpliftKnTotal, r.StormHeightM, r.StormSeconds));
            return r;
        }

        static double MaxScale(IList<SailState> states) { double m = 0; foreach (var s in states) if (!s.Storm) m = Math.Max(m, s.Scale); return m; }
    }
}
