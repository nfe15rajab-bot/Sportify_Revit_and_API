using Sportify.Simulation.Sun;

namespace SportfyRevit
{
    /// <summary>One dynamic unit worked out for one host: the mechanics of its kind, the plan (bars and membranes in the host's frame) at its reference state and at every other state, and the result the web app reads.</summary>
    internal sealed class KineticUnit
    {
        public KineticHost Host = null!;
        public UnitPlan Plan = null!;                                   // at the reference state: what Revit is given
        public string StateLabel = "";
        public LouvreMechanicsReport? Louvre;                           // overhead louvre, slat screen, fin screen
        public SailReport? Sail;
        public FenceReport? Fence;
        public List<UnitPlan> StatePlans = new();                       // one per actuation state (the video and the CAD model move through them)
        public List<string> StateLabels = new();
        public KineticPieceDto Dto = null!;
    }

    /// <summary>What the analysis knows that every kind needs: where and when (latitude, north), the shade the analysis asks for, the wind at the roof.</summary>
    internal sealed class KineticEnvironment
    {
        public double LatitudeDeg = SunModel.DefaultLatitudeDeg, NorthDeg;
        public double ShadeTargetPercent = SunModel.DefaultShadeTargetPercent;
        public double PressurePa = 600;
        public string PressureNote = "";
    }

    /// <summary>The mechanics and the plan of a unit, for each kind (LouvreMechanics, SailMechanics, RollerFenceMechanics; KineticUnits).</summary>
    internal static class KineticsBuild
    {
        internal static KineticUnit Build(KineticHost host, LouvreDesign design, KineticEnvironment env)
        {
            var unit = new KineticUnit { Host = host };
            switch (host.Kind)
            {
                case KineticKind.Sail: BuildSail(unit, design, env); break;
                case KineticKind.Fence: BuildFence(unit, design, env); break;
                default: BuildLouvre(unit, design, env); break;
            }
            // Revit places the structure and the moving parts; the mechanism's hardware (cranks, pistons, motors) and the CAD-only curtain slats stay in the videos and the CAD model (StatePlans)
            unit.Plan = new UnitPlan { Kind = unit.Plan.Kind, Label = unit.Plan.Label, LengthM = unit.Plan.LengthM, DepthM = unit.Plan.DepthM, HeightM = unit.Plan.HeightM, Bars = unit.Plan.Bars.Where(b => !b.Detail && !b.CadOnly).ToList(), Surfaces = unit.Plan.Surfaces };
            var info = KineticKinds.Get(host.Kind);
            unit.Dto.Kind = info.Key; unit.Dto.KindLabel = info.Label; unit.Dto.Host = host.From;
            unit.Dto.EquipmentKey = info.Key; unit.Dto.EquipmentName = host.Name;
            unit.Dto.LengthM = Math.Round(host.LengthM, 2); unit.Dto.HeightM = Math.Round(host.HeightM, 2);
            unit.Dto.WidthM = Math.Round(host.LengthM, 2); unit.Dto.DepthM = Math.Round(host.DepthM, 2);
            unit.Dto.XM = host.PlanX; unit.Dto.YM = host.PlanY;
            unit.Dto.FamilyName = AdaptiveFamilyBuilder.BarName + (unit.Plan.Surfaces.Count > 0 ? " + " + AdaptiveFamilyBuilder.SurfaceName : "");
            unit.Dto.Source = "adaptive";
            unit.Dto.MovingParts = unit.Plan.Bars.Count(b => b.Dynamic) + unit.Plan.Surfaces.Count(s => s.Dynamic);
            unit.Dto.PartsPlaced = unit.Plan.Bars.Count + unit.Plan.Surfaces.Count;
            return unit;
        }

        // ------------------------------------------------------------------ louvres: overhead, slats, fins

        static void BuildLouvre(KineticUnit unit, LouvreDesign d, KineticEnvironment env)
        {
            var host = unit.Host;
            var lh = host.Kind == KineticKind.Overhead ? LouvreHost.Horizontal : host.Kind == KineticKind.Slats ? LouvreHost.Vertical : LouvreHost.VerticalFins;
            var states = LouvreActuationModel.StatesFor(lh, env.LatitudeDeg, env.NorthDeg, host.AxisPlan.X, host.AxisPlan.Y, host.NormalPlan.X, host.NormalPlan.Y);

            // what the blades are arrayed across, and how far each one spans
            var stack = host.Kind == KineticKind.Slats ? host.HeightM : host.LengthM;
            var span = host.Kind == KineticKind.Overhead ? host.DepthM : host.Kind == KineticKind.Slats ? host.LengthM : host.HeightM;
            if (host.Kind == KineticKind.Overhead) { stack = host.LengthM; span = host.DepthM; }

            var spacing = LouvreMechanics.RecommendSpacing(d, lh, stack, env.ShadeTargetPercent, states);
            var uplift = host.Piece?.WindUpliftKn ?? 0;
            var mech = LouvreMechanics.Analyse(d, lh, stack, span, env.PressurePa, uplift, states, spacing);
            var bays = Math.Max(1, mech.Supports?.Bays ?? 1);

            UnitPlan PlanAt(LouvreActuationModel.ActuationState? s)
            {
                var open = s?.LouvreOpenAngleDeg ?? 0.0;
                var lean = s != null ? KineticsPlan.PlanDirToLocal(host.Frame, s.LeanX, s.LeanY) : V3.UnitY;
                switch (host.Kind)
                {
                    case KineticKind.Overhead:
                        return KineticUnits.Overhead(host.LengthM, host.DepthM, host.HeightM, spacing.Count, mech.ChordM, mech.ThicknessM, bays, d["post_size_m"], d["rail_size_m"], KineticUnits.NormalOverhead(open, lean));
                    case KineticKind.Slats:
                        return KineticUnits.SlatScreen(host.LengthM, host.HeightM, spacing.Count, mech.ChordM, mech.ThicknessM, bays, d["post_size_m"], d["rail_size_m"], KineticUnits.NormalSlat(open));
                    default:
                        return KineticUnits.FinScreen(host.LengthM, host.HeightM, spacing.Count, mech.ChordM, mech.ThicknessM, bays, d["post_size_m"], d["rail_size_m"], KineticUnits.NormalFin(open, lean.X));
                }
            }

            LouvreActuationModel.ActuationState? reference = states.Count == 0 ? null
                : states.FirstOrDefault(s => s.Label == "solar noon" || s.Label == "peak sun") ?? states[states.Count / 2];
            unit.Plan = PlanAt(reference);
            unit.StateLabel = reference?.Label ?? "rest (the sun never reaches it)";
            foreach (var s in states) { unit.StatePlans.Add(PlanAt(s)); unit.StateLabels.Add(s.Label); }
            if (states.Count == 0) { unit.StatePlans.Add(unit.Plan); unit.StateLabels.Add("rest"); }
            unit.Louvre = mech;

            unit.Dto = new KineticPieceDto
            {
                BladeCount = spacing.Count,
                States = KineticsShared.StatesDto(mech), Mechanics = KineticsShared.ToDto(mech),
                Spacing = new KineticSpacingDto
                {
                    Count = spacing.Count, PitchMm = Math.Round(spacing.PitchM * 1000, 1), StackLengthM = Math.Round(spacing.StackLengthM, 2),
                    TargetStoppedPercent = spacing.TargetStoppedPercent, StoppedAtBindingPercent = spacing.StoppedAtBindingPercent, BindingState = spacing.BindingLabel,
                    BindingProfileDeg = Math.Round(spacing.BindingProfileDeg, 1), ClosedStoppedPercent = spacing.ClosedStoppedPercent, CappedAtMaxPitch = spacing.CappedAtMaxPitch, Reason = spacing.Reason,
                },
                Supports = mech.Supports == null ? null : new KineticSupportsDto
                {
                    SpanM = Math.Round(mech.Supports.SpanM, 2), AllowedSpanM = Math.Round(mech.Supports.AllowedSpanM, 2), Bays = mech.Supports.Bays, BayLengthM = Math.Round(mech.Supports.BayLengthM, 2),
                    IntermediatePosts = mech.Supports.IntermediatePosts, DeflectionMm = Math.Round(mech.Supports.DeflectionMm, 1),
                },
            };
            if (states.Count == 0)
                unit.Dto.Mechanics!.Findings!.Insert(0, "On the design day the sun is never in front of this " + (host.Kind == KineticKind.Fins ? "fin" : "slat") + " screen: it is placed at rest and there is nothing for it to track (the storm position holds).");
        }

        // ------------------------------------------------------------------ sails on movable pillars

        static void BuildSail(KineticUnit unit, LouvreDesign d, KineticEnvironment env)
        {
            var host = unit.Host;
            var w = host.LengthM; var depth = host.DepthM; var h = host.HeightM;
            var triangle = host.Triangle; var anchorSide = host.AnchorSide;                          // from the site: a triangle beside a garden, the masts farthest from it staying put
            var xp = KineticsPlan.DirToPlan(host.Frame.X); var yp = KineticsPlan.DirToPlan(host.Frame.Y);
            var states = SailMechanics.States(d, env.LatitudeDeg, env.NorthDeg, triangle, w, depth, h, anchorSide, xp.X, xp.Y, yp.X, yp.Y);
            var rep = SailMechanics.Analyse(d, triangle, w, depth, h, anchorSide, env.PressurePa, host.Piece?.WindUpliftKn ?? 0, states);
            var mast = Math.Max(d["mast_diameter_m"], rep.RecommendedMastDiameterM);
            var twist = d["sail_twist_ratio"] * Math.Min(w, depth);

            UnitPlan PlanAt(SailState s) =>
                KineticUnits.Sail(triangle, w, depth, s.Scale, anchorSide, s.Storm ? rep.StormHeightM : h, mast, d["sail_rail_size_m"], rep.MinScale, rep.MaxScale, s.Storm ? 0 : twist, d["sail_carriage_len_m"]);

            var reference = states.FirstOrDefault(s => s.Label.StartsWith("middle")) ?? states[0];
            unit.Plan = PlanAt(reference);
            unit.StateLabel = reference.Label;
            foreach (var s in states) { unit.StatePlans.Add(PlanAt(s)); unit.StateLabels.Add(s.Label); }
            unit.Sail = rep;

            unit.Dto = new KineticPieceDto
            {
                BladeCount = rep.MastCount,
                States = states.Select(s => new KineticStateDto
                {
                    Label = s.Label, SolarTimeH = s.SolarTimeH, SunElevationDeg = s.SunElevationDeg, LouvreOpenAngleDeg = s.Scale, SunStoppedPercent = s.ShadeHeldPercent,
                }).ToList(),
                Sail = new KineticSailDto
                {
                    WidthM = Math.Round(rep.WidthM, 2), DepthM = Math.Round(rep.DepthM, 2), MastHeightM = Math.Round(rep.HeightM, 2), AreaM2 = Math.Round(rep.AreaM2, 1),
                    MastDiameterMm = Math.Round(mast * 1000, 0), RecommendedMastDiameterMm = Math.Round(rep.RecommendedMastDiameterM * 1000, 0), MastOk = rep.MastOk,
                    MastUtilisationPercent = Math.Round(rep.MastUtilisationPercent, 0), MastBaseMomentKnM = Math.Round(rep.MastBaseMomentKnM, 1),
                    PretensionPullKnPerCorner = Math.Round(rep.PretensionPullKnPerCorner, 2), UpliftKnPerMast = Math.Round(rep.UpliftKnPerMast, 2),
                    StormHeightM = Math.Round(rep.StormHeightM, 2), FabricMassKg = Math.Round(rep.FabricMassKg, 1),
                    Shape = rep.Triangle ? "triangle" : "rectangle", MinScale = rep.MinScale, MaxScale = rep.MaxScale, AreaMinM2 = Math.Round(rep.AreaMinM2, 1), AreaMaxM2 = Math.Round(rep.AreaMaxM2, 1),
                    GardenFreedM2 = Math.Round(rep.GardenFreedM2, 1), RailLengthM = Math.Round(rep.RailLengthM, 1), TravelM = Math.Round(rep.TravelM, 2), Tracks = rep.Tracks,
                    CarriageForceKn = Math.Round(rep.CarriageForceKn, 2), DriveSpeedCmS = Math.Round(rep.DriveSpeedMs * 100, 1), TravelSeconds = Math.Round(rep.TravelSeconds, 0), DrivePowerW = Math.Round(rep.DrivePowerW, 0),
                    ShadeFixedMinPercent = rep.ShadeFixedMinPercent, ShadeTrackedMinPercent = rep.ShadeMovingMinPercent,
                    ShadeFixedMeanPercent = rep.ShadeFixedMeanPercent, ShadeTrackedMeanPercent = rep.ShadeMovingMeanPercent,
                    States = states.Select(s => new KineticSailStateDto
                    {
                        Label = s.Label, SolarTimeH = s.SolarTimeH, SunElevationDeg = s.SunElevationDeg, Scale = s.Scale, AreaM2 = Math.Round(s.AreaM2, 1), TravelM = Math.Round(s.TravelM, 2),
                        ShadeHeldFixedPercent = s.ShadeHeldFixedPercent, ShadeHeldTrackedPercent = s.ShadeHeldPercent,
                    }).ToList(),
                    Findings = new List<string>(rep.Findings),
                },
            };
        }

        // ------------------------------------------------------------------ roller fences

        static void BuildFence(KineticUnit unit, LouvreDesign d, KineticEnvironment env)
        {
            var host = unit.Host;
            var f = host.Fence!;
            var rep = RollerFenceMechanics.Analyse(d, f.Edge ?? "", host.LengthM, host.HeightM, f.StopsPercentOfExits, env.PressurePa);
            var roller = d["fence_roller_diameter_m"];
            var rail = Math.Max(rep.RailSizeM, rep.RecommendedRailSizeM);
            UnitPlan PlanAt(double deployed) => KineticUnits.RollerFence(host.LengthM, host.HeightM + roller, deployed, rep.Bays, rail, roller);

            unit.Plan = PlanAt(host.HeightM);                                             // placed in play: deployed
            unit.StateLabel = "deployed (in play)";
            unit.StatePlans.Add(PlanAt(0)); unit.StateLabels.Add("stored");
            unit.StatePlans.Add(PlanAt(host.HeightM * 0.5)); unit.StateLabels.Add("half up");
            unit.StatePlans.Add(unit.Plan); unit.StateLabels.Add("deployed (in play)");
            unit.Fence = rep;

            unit.Dto = new KineticPieceDto
            {
                BladeCount = rep.Rails,
                Fence = new KineticFenceDto
                {
                    Edge = rep.Edge, LengthM = Math.Round(rep.LengthM, 2), HeightM = Math.Round(rep.HeightM, 2), Rails = rep.Rails, BaySpacingM = Math.Round(rep.BaySpacingM, 2),
                    RailSizeMm = Math.Round(rep.RailSizeM * 1000, 0), RecommendedRailSizeMm = Math.Round(rep.RecommendedRailSizeM * 1000, 0), RailOk = rep.RailOk,
                    RailUtilisationPercent = Math.Round(rep.RailUtilisationPercent, 0), RailDeflectionMm = Math.Round(rep.RailDeflectionMm, 1), RailDeflectionLimitMm = Math.Round(rep.RailDeflectionLimitMm, 0),
                    WindMomentKnM = Math.Round(rep.WindMomentKnM, 2), ImpactMomentKnM = Math.Round(rep.ImpactMomentKnM, 2), ImpactEnergyJ = Math.Round(rep.ImpactEnergyJ, 0), ImpactForceKn = Math.Round(rep.ImpactForceKn, 2),
                    CurtainMassKg = Math.Round(rep.CurtainMassKg, 1), MotorForceN = Math.Round(rep.MotorForceN, 0), MotorTorqueNm = Math.Round(rep.MotorTorqueNm, 1), MotorPowerW = Math.Round(rep.MotorPowerW, 0),
                    DeploySeconds = rep.DeploySeconds, StormRetract = rep.StormRetract, StopsPercentOfExits = Math.Round(rep.StoppedPercentOfExits, 0),
                    Findings = new List<string>(rep.Findings),
                },
            };
        }
    }
}
