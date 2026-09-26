using System;
using System.Collections.Generic;
using System.Globalization;

namespace Sportify.Simulation.Sun
{
    public sealed class FenceReport
    {
        public string Edge;
        public double LengthM, HeightM;                  // the recommended height of the curtain when it is in play
        public int Bays, Rails;
        public double BaySpacingM;
        public double CurtainMassKg, BottomBarMassKg;
        public double RailSizeM, RailWallM, RecommendedRailSizeM;
        public double OperatingPressurePa, DesignPressurePa;
        public double WindMomentKnM, ImpactMomentKnM;    // at the foot of a guide rail (a cantilever)
        public double RailStressMpa, RailYieldMpa, RailUtilisationPercent;
        public double RailDeflectionMm, RailDeflectionLimitMm;
        public bool RailOk;
        public double ImpactEnergyJ, ImpactForceKn;
        public double MotorForceN, MotorTorqueNm, MotorPowerW;
        public double DeploySeconds;
        public bool StormRetract;
        public double StoppedPercentOfExits;             // what the ball analysis says this fence stops
        public List<string> Findings = new List<string>();
    }

    /// <summary>
    /// A ROLLER FENCE: a curtain on a roller at the foot of the fence, its bottom bar running up two or more vertical guide rails (the Z axis) when the fence is
    /// needed and rolling back down when it is not. The ball analysis gives where it stands (an edge of the roof, a stretch of it) and how high it has to be;
    /// this checks that it holds: the guide rails stand as cantilevers from the roof, loaded by the wind on the net (in wind up to the operating limit: above it the
    /// fence stows) and by a ball hitting the curtain (its energy over the way the net gives); and it sizes the roller motor that lifts the bar. Plain arithmetic;
    /// no Unity, no Revit.
    /// </summary>
    public static class RollerFenceMechanics
    {
        static double Inertia(double s, double t) { var i = Math.Max(0, s - 2 * t); return (Math.Pow(s, 4) - Math.Pow(i, 4)) / 12.0; }

        public static FenceReport Analyse(LouvreDesign d, string edge, double lengthM, double heightM, double stoppedPercent, double designPressurePa)
        {
            var r = new FenceReport { Edge = edge, LengthM = lengthM, HeightM = heightM, StoppedPercentOfExits = stoppedPercent, DesignPressurePa = Math.Max(0, designPressurePa) };
            var sf = d["safety_factor"]; var eta = d["linkage_efficiency"];
            var rho = d["air_density_kg_m3"]; var g = 9.81;
            var steelE = d["steel_youngs_pa"]; var steelFy = d["steel_yield_pa"];

            r.Bays = Math.Max(1, (int)Math.Ceiling(lengthM / d["fence_max_bay_m"] - 1e-9));
            r.Rails = r.Bays + 1;
            r.BaySpacingM = lengthM / r.Bays;
            r.OperatingPressurePa = 0.5 * rho * d["stow_wind_speed_ms"] * d["stow_wind_speed_ms"];
            r.StormRetract = Math.Sqrt(2 * r.DesignPressurePa / rho) > d["stow_wind_speed_ms"];

            r.CurtainMassKg = d["fence_curtain_kg_m2"] * lengthM * heightM;
            r.BottomBarMassKg = 7850 * 0.05 * 0.05 * 0.6 * lengthM;                       // a 50 x 50 mm bar, mostly hollow (say 0.6 of its section)
            r.RailWallM = d["fence_rail_wall_m"];

            // wind on the deployed curtain: the rail at the middle of the run takes a bay's width of it; the rail is a cantilever from the roof
            var wPerM = r.OperatingPressurePa * d["fence_wind_coeff"] * d["fence_solidity"] * r.BaySpacingM;          // N per metre of rail height
            r.WindMomentKnM = wPerM * heightM * heightM / 2.0 / 1000.0;

            // a ball hits the curtain: its energy over the way the net gives is an average force; the two rails beside the hit share it, at the worst height (the top)
            r.ImpactEnergyJ = 0.5 * d["ball_mass_kg"] * d["ball_speed_ms"] * d["ball_speed_ms"];
            r.ImpactForceKn = r.ImpactEnergyJ / d["net_stretch_m"] / 1000.0;
            r.ImpactMomentKnM = 0.5 * r.ImpactForceKn * heightM;

            var moment = Math.Max(r.WindMomentKnM, r.ImpactMomentKnM) * 1000.0;                                       // N m
            double Stress(double size) => moment / (Inertia(size, r.RailWallM) / (size / 2)) / 1e6;                   // MPa
            double Deflection(double size) => wPerM * Math.Pow(heightM, 4) / (8.0 * steelE * Inertia(size, r.RailWallM)) * 1000.0;   // mm
            r.RailDeflectionLimitMm = heightM * 1000.0 / 100.0;                                                        // a cantilever: height / 100
            bool Passes(double size) => Stress(size) * sf <= steelFy / 1e6 && Deflection(size) <= r.RailDeflectionLimitMm;

            r.RailSizeM = d["fence_rail_size_m"];
            r.RailStressMpa = Stress(r.RailSizeM);
            r.RailYieldMpa = steelFy / 1e6;
            r.RailUtilisationPercent = 100 * r.RailStressMpa * sf / r.RailYieldMpa;
            r.RailDeflectionMm = Deflection(r.RailSizeM);
            r.RailOk = Passes(r.RailSizeM);
            r.RecommendedRailSizeM = r.RailSizeM;
            for (var trial = r.RailSizeM; !r.RailOk && trial < 0.4; trial += 0.01)
            {
                r.RecommendedRailSizeM = trial;
                if (Passes(trial)) break;
            }

            // the motor lifts the bottom bar and the curtain it pays out, with the friction of the rails (0.2), at the drum's radius
            var lifted = (r.BottomBarMassKg + 0.5 * r.CurtainMassKg) * g * 1.2;
            var drumR = d["fence_roller_diameter_m"] / 2;
            r.MotorForceN = lifted;
            r.MotorTorqueNm = sf * lifted * drumR / eta;
            r.DeploySeconds = d["fence_deploy_s"];
            r.MotorPowerW = lifted * heightM / r.DeploySeconds / eta;

            var inv = CultureInfo.InvariantCulture;
            r.Findings.Add(string.Format(inv, "{0} edge, {1:0.#} m of fence {2:0.0} m high (it stops {3:0}% of the shots that leave the roof there): {4} guide rails {5:0.0} m apart, a {6:0} kg curtain and a {7:0} kg bottom bar.",
                edge, lengthM, heightM, stoppedPercent, r.Rails, r.BaySpacingM, r.CurtainMassKg, r.BottomBarMassKg));
            r.Findings.Add(string.Format(inv, "Guide rails ({0:0} x {1:0.0} mm steel, cantilevers from the roof): {2:0.0} kN m from the wind on the net up to {3:0} m/s, {4:0.0} kN m from a ball ({5:0} J over {6:0.0} m of give is {7:0.0} kN): {8:0} MPa, {9:0}% of the yield with the safety factor, {10:0} mm at the top against {11:0} mm allowed.",
                r.RailSizeM * 1000, r.RailWallM * 1000, r.WindMomentKnM, d["stow_wind_speed_ms"], r.ImpactMomentKnM, r.ImpactEnergyJ, d["net_stretch_m"], r.ImpactForceKn, r.RailStressMpa, r.RailUtilisationPercent, r.RailDeflectionMm, r.RailDeflectionLimitMm));
            if (!r.RailOk)
                r.Findings.Add(string.Format(inv, "The rails are too light for the load: a {0:0} mm square tube passes (or a softer net, which cuts the ball's force, or a rail every {1:0.0} m).", r.RecommendedRailSizeM * 1000, r.BaySpacingM / 2));
            r.Findings.Add(string.Format(inv, "Roller motor: {0:0} N to lift the bar and the curtain it pays out, {1:0.0} N m at a {2:0} mm drum, about {3:0} W to deploy in {4:0} s. It is turned on only when it is needed: deployed while the courts are in use, rolled back down after.",
                r.MotorForceN, r.MotorTorqueNm, drumR * 2000, r.MotorPowerW, r.DeploySeconds));
            if (r.StormRetract)
                r.Findings.Add(string.Format(inv, "In the design peak ({0:0} Pa) the deployed net would be a sail on its rails: the fence must be stowed above {1:0} m/s (an anemometer that overrides the switch), and it is never left up.", r.DesignPressurePa, d["stow_wind_speed_ms"]));
            return r;
        }
    }
}
