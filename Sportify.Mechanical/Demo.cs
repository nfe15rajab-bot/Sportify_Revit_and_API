using Sportify.Simulation.Sun;

namespace Sportify.Mechanical;

/// <summary>
/// Builds a full production-scale overhead louvre pergola (not the tiny 3-blade smoke-test unit) through three
/// sun-tracking states across a day, using the same real cores the add-in uses (KineticUnits.Overhead,
/// KineticUnits.NormalOverhead) -- the numbers match a real reference render: width 6.0 m, 23 blades gives a
/// 6.0/23 = 0.261 m (261 mm) pitch, chord 150 mm, on 2.0 m bays (2 bays across a 4.0 m depth), 2.6 m high
/// (the "louvre pergola 2.6 m high" default in assumptions.js's shading-equipment catalogue).
/// </summary>
internal static class Demo
{
    static double[] A(V3 v) => new[] { v.X, v.Y, v.Z };

    internal static RenderRequest LouvrePergola()
    {
        const double width = 6.0, depth = 4.0, height = 2.6, chord = 0.15, thickness = 0.03;
        const int count = 23, bays = 2;
        var request = new RenderRequest
        {
            PieceName = "Overhead louvre (pergola)", Kind = "overhead", KindLabel = "Overhead louvre (pergola)",
            LengthM = width, DepthM = depth, HeightM = height, ChordM = chord, ThicknessM = thickness, Count = count, PitchM = width / count, Bays = bays,
            MechanicsLines = new List<string> { "Production-scale demo: " + count + " blades of " + (int)(chord * 1000) + " mm at a " + (int)(width / count * 1000) + " mm pitch across " + width.ToString("0.0") + " m, tracking the sun through three states." },
        };
        // (label, openDeg, sunElevationDeg, solarTimeH) -- matches the reference video's three call-outs.
        foreach (var (label, open, elevation, hour) in new[] { ("Morning", 42.0, 46.0, 9.0), ("Solar noon", 22.0, 58.0, 12.0), ("Afternoon", 72.0, 18.0, 16.0) })
        {
            var plan = KineticUnits.Overhead(width, depth, height, count, chord, thickness, bays, 0.06, 0.04, KineticUnits.NormalOverhead(open, V3.UnitX), 0.06);
            var state = new RenderState { Label = label, SolarTimeH = hour, SunElevationDeg = elevation, OpenDeg = open, SunStoppedPercent = 50 };
            foreach (var b in plan.Bars)
                state.Bars.Add(new RenderBar { Role = b.Role, Dynamic = b.Dynamic, Detail = b.Detail, CadOnly = b.CadOnly, P0 = A(b.P0), P1 = A(b.P1), U = A(b.U), SizeU = b.SizeU, SizeV = b.SizeV });
            request.States.Add(state);
        }
        return request;
    }

    public static int Run(string outDir, string name)
    {
        if (Type.GetTypeFromProgID("SldWorks.Application") == null) { Console.WriteLine("DEMO SKIPPED: SOLIDWORKS is not installed here."); return 2; }
        Directory.CreateDirectory(outDir);
        var request = LouvrePergola();
        var options = new SimulateOptions { Width = 1280, Height = 720, Fps = 15, IntroS = 0.5, MoveS = 0.8, HoldS = 0.6, OutroS = 0.5, Detail = true };
        Watchdog.Start(TimeSpan.FromMinutes(6), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(20));
        try
        {
            using var session = SolidWorksSession.Open(false, false);
            Console.WriteLine(session.Info());
            var report = UnitAssembly.Run(session, request, outDir, name, options);
            Watchdog.Stop();
            var reportPath = Path.Combine(outDir, name + "_simulation.json");
            File.WriteAllText(reportPath, UnitAssembly.ToJson(report));
            Console.WriteLine("demo unit: " + report.Components + " components, " + report.Frames + " frames, " + report.TotalMassKg.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " kg");
            Console.WriteLine(report.Interferences.All(r => r.Count == 0)
                ? "No interference between any two parts in any of the " + report.Interferences.Count + " states"
                : "Interferences: " + string.Join("; ", report.Interferences.Where(r => r.Count > 0).Select(r => r.State + " " + string.Join(", ", r.Pairs))));
            Console.WriteLine("RESULT " + reportPath);
            Console.WriteLine("VIDEO " + report.VideoFile);
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine("DEMO FAILED: " + ex.Message); return 1; }
        finally { Watchdog.Stop(); }
    }
}
