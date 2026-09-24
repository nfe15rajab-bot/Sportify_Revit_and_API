using System.Text.Json.Serialization;
using Sportify.Simulation.Sun;

namespace SportfyRevit
{
    /// <summary>
    /// What one dynamic unit looks like through its states, for whoever draws or builds it: the Unity video (KineticsRunner.cs, JsonUtility, camelCase, field for field)
    /// and the SOLIDWORKS tool (Sportify.Mechanical). Every bar and membrane is in the unit's OWN frame (x along the unit, y outward or across its depth, z up, metres); the
    /// states differ only in where the moving parts are, so bar i of one state is bar i of every other and a renderer can move between them.
    /// </summary>
    internal class KineticsRenderRequest
    {
        [JsonPropertyName("pieceName")] public string PieceName { get; set; } = "";
        [JsonPropertyName("kind")] public string Kind { get; set; } = "";
        [JsonPropertyName("kindLabel")] public string KindLabel { get; set; } = "";
        [JsonPropertyName("lengthM")] public double LengthM { get; set; }
        [JsonPropertyName("depthM")] public double DepthM { get; set; }
        [JsonPropertyName("heightM")] public double HeightM { get; set; }
        [JsonPropertyName("chordM")] public double ChordM { get; set; }
        [JsonPropertyName("thicknessM")] public double ThicknessM { get; set; }
        [JsonPropertyName("count")] public int Count { get; set; }
        [JsonPropertyName("pitchM")] public double PitchM { get; set; }
        [JsonPropertyName("bays")] public int Bays { get; set; }
        [JsonPropertyName("states")] public List<KineticsRenderState> States { get; set; } = new();
        /// <summary>The mechanics the video's title card shows, in words.</summary>
        [JsonPropertyName("mechanicsLines")] public List<string> MechanicsLines { get; set; } = new();
        /// <summary>The design inputs the CAD model is built from (LouvreDesign, by key): sections, materials, the crank, so the model and the numbers use the same values.</summary>
        [JsonPropertyName("inputs")] public List<KineticsRenderInput> Inputs { get; set; } = new();
    }

    internal class KineticsRenderInput
    {
        [JsonPropertyName("key")] public string Key { get; set; } = "";
        [JsonPropertyName("value")] public double Value { get; set; }
    }

    internal class KineticsRenderState
    {
        [JsonPropertyName("label")] public string Label { get; set; } = "";
        [JsonPropertyName("solarTimeH")] public double SolarTimeH { get; set; }
        [JsonPropertyName("sunElevationDeg")] public double SunElevationDeg { get; set; }
        /// <summary>The horizontal direction toward the sun, in the unit's frame (x, y; a unit vector), for the light of the video.</summary>
        [JsonPropertyName("sunX")] public double SunX { get; set; }
        [JsonPropertyName("sunY")] public double SunY { get; set; }
        [JsonPropertyName("openDeg")] public double OpenDeg { get; set; }
        [JsonPropertyName("sunStoppedPercent")] public double SunStoppedPercent { get; set; }
        [JsonPropertyName("windTorqueOperatingNm")] public double WindTorqueOperatingNm { get; set; }
        [JsonPropertyName("bars")] public List<KineticsRenderBar> Bars { get; set; } = new();
        [JsonPropertyName("surfaces")] public List<KineticsRenderSurface> Surfaces { get; set; } = new();
    }

    internal class KineticsRenderBar
    {
        [JsonPropertyName("role")] public string Role { get; set; } = "";
        [JsonPropertyName("dynamic")] public bool Dynamic { get; set; }
        /// <summary>The mechanism's hardware (cranks, pistons, motors): drawn by the videos and the CAD model, not placed in Revit.</summary>
        [JsonPropertyName("detail")] public bool Detail { get; set; }
        /// <summary>Only the CAD model has it (the slats of a roller curtain, which the videos draw as one membrane).</summary>
        [JsonPropertyName("cadOnly")] public bool CadOnly { get; set; }
        [JsonPropertyName("p0")] public double[] P0 { get; set; } = new double[3];
        [JsonPropertyName("p1")] public double[] P1 { get; set; } = new double[3];
        /// <summary>Unit vector square to the axis: the direction of the section's first size.</summary>
        [JsonPropertyName("u")] public double[] U { get; set; } = new double[3];
        [JsonPropertyName("sizeU")] public double SizeU { get; set; }
        [JsonPropertyName("sizeV")] public double SizeV { get; set; }
    }

    internal class KineticsRenderSurface
    {
        [JsonPropertyName("role")] public string Role { get; set; } = "";
        [JsonPropertyName("dynamic")] public bool Dynamic { get; set; }
        [JsonPropertyName("a")] public double[] A { get; set; } = new double[3];
        [JsonPropertyName("b")] public double[] B { get; set; } = new double[3];
        [JsonPropertyName("c")] public double[] C { get; set; } = new double[3];
        [JsonPropertyName("d")] public double[] D { get; set; } = new double[3];
    }

    internal static class KineticsRender
    {
        static double[] Arr(V3 v) => new[] { Math.Round(v.X, 4), Math.Round(v.Y, 4), Math.Round(v.Z, 4) };

        static KineticsRenderState StateOf(UnitPlan plan, string label)
        {
            var s = new KineticsRenderState { Label = label };
            foreach (var b in plan.Bars)
                s.Bars.Add(new KineticsRenderBar { Role = b.Role, Dynamic = b.Dynamic, Detail = b.Detail, CadOnly = b.CadOnly, P0 = Arr(b.P0), P1 = Arr(b.P1), U = Arr(b.U), SizeU = Math.Round(b.SizeU, 4), SizeV = Math.Round(b.SizeV, 4) });
            foreach (var f in plan.Surfaces)
                s.Surfaces.Add(new KineticsRenderSurface { Role = f.Role, Dynamic = f.Dynamic, A = Arr(f.A), B = Arr(f.B), C = Arr(f.C), D = Arr(f.D) });
            return s;
        }

        /// <summary>The unit's request: its plan at every state, with what each state means (the sun, the opening, what the wind does) where the kind has it.</summary>
        internal static KineticsRenderRequest For(KineticUnit unit, LouvreDesign design, KineticEnvironment env)
        {
            var host = unit.Host;
            var req = new KineticsRenderRequest
            {
                PieceName = host.Name, Kind = KineticKinds.Get(host.Kind).Key, KindLabel = KineticKinds.Get(host.Kind).Label,
                LengthM = host.LengthM, DepthM = host.DepthM, HeightM = host.HeightM,
                ChordM = design["chord_m"], ThicknessM = design["thickness_m"],
                Count = unit.Dto.Spacing?.Count ?? unit.Dto.BladeCount, PitchM = (unit.Dto.Spacing?.PitchMm ?? 0) / 1000.0, Bays = unit.Dto.Supports?.Bays ?? unit.Dto.Fence?.Rails - 1 ?? 1,
            };
            for (var i = 0; i < unit.StatePlans.Count; i++)
            {
                var s = StateOf(unit.StatePlans[i], unit.StateLabels[i]);
                var stateDto = i < (unit.Dto.States?.Count ?? 0) ? unit.Dto.States![i] : null;
                if (stateDto != null)
                {
                    s.SolarTimeH = stateDto.SolarTimeH; s.SunElevationDeg = stateDto.SunElevationDeg; s.OpenDeg = stateDto.LouvreOpenAngleDeg;
                    s.SunStoppedPercent = stateDto.SunStoppedPercent; s.WindTorqueOperatingNm = stateDto.WindTorqueOperatingNm;
                    var sun = SunModel.SunAt(env.LatitudeDeg, SunModel.DayOfYear[0], stateDto.SolarTimeH);
                    double tx, ty; SunModel.TowardSun(sun, env.NorthDeg, out tx, out ty);
                    var local = KineticsPlan.PlanDirToLocal(host.Frame, tx, ty);
                    var l = Math.Sqrt(local.X * local.X + local.Y * local.Y);
                    s.SunX = l > 1e-9 ? Math.Round(local.X / l, 4) : 0; s.SunY = l > 1e-9 ? Math.Round(local.Y / l, 4) : 1;
                }
                else { s.SunX = 0; s.SunY = 1; s.SunElevationDeg = 50; }
                req.States.Add(s);
            }
            foreach (var key in new[] { "chord_m", "thickness_m", "wall_m", "density_kg_m3", "post_size_m", "post_wall_m", "rail_size_m", "crank_radius_m", "mast_diameter_m", "mast_wall_m", "fence_roller_diameter_m", "fence_rail_size_m", "fence_rail_wall_m" })
                req.Inputs.Add(new KineticsRenderInput { Key = key, Value = design[key] });
            req.MechanicsLines = LinesFor(unit);
            return req;
        }

        static string Ratio(double r) => double.IsInfinity(r) || r <= 0 ? "n/a" : "span/" + r.ToString("0");

        /// <summary>A few plain lines for the title card: what was recommended and what it takes to move it.</summary>
        static List<string> LinesFor(KineticUnit unit)
        {
            var lines = new List<string>();
            if (unit.Louvre is { } m)
            {
                if (unit.Louvre.Spacing != null) lines.Add(unit.Louvre.Spacing.Reason);
                lines.Add("Actuator " + m.ActuatorTorqueNm.ToString("0.#") + " N m (" + m.ActuatorForceN.ToString("0") + " N at the crank) moves them in wind up to " + m.OperatingWindMs.ToString("0") + " m/s; it holds " + m.HoldingTorqueNm.ToString("0.#") + " N m in the design gust");
                lines.Add("Blade deflection in the design gust: " + m.DeflectionMm.ToString("0.#") + " mm, " + Ratio(m.DeflectionRatio) + (m.DeflectionOk ? ", within the limit" : ", past the limit") + (m.StowRequired ? "; they stow above the operating wind" : ""));
                if (m.Supports != null && m.Supports.Bays > 1) lines.Add("Posts every " + m.Supports.BayLengthM.ToString("0.0#") + " m (" + m.Supports.Bays + " bays): a blade may span " + m.Supports.AllowedSpanM.ToString("0.0#") + " m in the design gust");
            }
            else if (unit.Sail is { } s)
            {
                lines.AddRange(s.Findings.Take(3));
            }
            else if (unit.Fence is { } f)
            {
                lines.AddRange(f.Findings.Take(3));
            }
            return lines;
        }
    }
}
