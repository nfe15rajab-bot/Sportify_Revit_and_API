using System.Diagnostics;
using System.Globalization;
using Sportify.Simulation.Sun;

namespace Sportify.Mechanical;

/// <summary>
/// Two quick self-checks of the tool, run by `Tools/run-checks.js --local` (they cannot run in CI: the tool compiles against SOLIDWORKS' interop assemblies).
///   watchdog-test  a run that hangs is stopped: needs no SOLIDWORKS. The watchdog is started with a 2 second limit and the main thread then sleeps for ever; the process must end by
///                  itself with exit code 4 and a "STALLED" line.
///   smoke          the whole path on a small unit, in about a minute: the REAL cores (KineticUnits.Overhead, the same code the add-in uses) plan a 1.2 x 1.2 m louvre with three blades,
///                  SOLIDWORKS builds it as an assembly, moves it through two states, records a small film with the mechanism close-up and saves the assembly and its STEP file.
///                  Exit 0 = passed, 1 = failed, 2 = skipped (SOLIDWORKS is not installed).
/// </summary>
internal static class Smoke
{
    public static int WatchdogTest()
    {
        Watchdog.Start(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2), TimeSpan.FromMinutes(1));
        Watchdog.Beat("about to hang");
        Console.WriteLine("hanging on purpose");
        Console.Out.Flush();
        Thread.Sleep(Timeout.Infinite);
        return 0;
    }

    static double[] A(V3 v) => new[] { v.X, v.Y, v.Z };

    /// <summary>The request the add-in would write for this unit: the plan's bars at each of the states (the blades turned to the state's normal).</summary>
    internal static RenderRequest TinyLouvre()
    {
        const double width = 1.2, depth = 1.2, height = 2.5, chord = 0.15, thickness = 0.03;
        var request = new RenderRequest
        {
            PieceName = "Smoke test louvre", Kind = "overhead", KindLabel = "Overhead louvre (pergola)",
            LengthM = width, DepthM = depth, HeightM = height, ChordM = chord, ThicknessM = thickness, Count = 3, PitchM = width / 3, Bays = 1,
            MechanicsLines = new List<string> { "A smoke test: three blades on one rod, two states." },
        };
        foreach (var (label, open, elevation) in new[] { ("closed to the sun", 0.0, 60.0), ("turned face-on", 45.0, 45.0) })
        {
            var plan = KineticUnits.Overhead(width, depth, height, 3, chord, thickness, 1, 0.06, 0.04, KineticUnits.NormalOverhead(open, V3.UnitX), 0.06);
            var state = new RenderState { Label = label, SolarTimeH = 12, SunElevationDeg = elevation, OpenDeg = open, SunStoppedPercent = 50 };
            foreach (var b in plan.Bars)
                state.Bars.Add(new RenderBar { Role = b.Role, Dynamic = b.Dynamic, Detail = b.Detail, CadOnly = b.CadOnly, P0 = A(b.P0), P1 = A(b.P1), U = A(b.U), SizeU = b.SizeU, SizeV = b.SizeV });
            request.States.Add(state);
        }
        return request;
    }

    public static int Run(string outDir)
    {
        if (Type.GetTypeFromProgID("SldWorks.Application") == null) { Console.WriteLine("SMOKE SKIPPED: SOLIDWORKS is not installed here (SldWorks.Application is not registered)."); return 2; }
        var failures = new List<string>();
        var clock = Stopwatch.StartNew();
        try
        {
            outDir = Path.GetFullPath(outDir);
            var request = TinyLouvre();
            var options = new SimulateOptions { Width = 640, Height = 360, Fps = 8, IntroS = 0.4, MoveS = 0.5, HoldS = 0.3, OutroS = 0.5, Detail = true };
            Watchdog.Start(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(3), TimeSpan.FromMinutes(10));
            using var session = SolidWorksSession.Open(false, false);
            Console.WriteLine(session.Info());
            var report = UnitAssembly.Run(session, request, outDir, "smoke", options);
            Watchdog.Stop();

            void Expect(bool ok, string what) { if (!ok) failures.Add(what); }
            var bladeCount = request.States[0].Bars.Count(b => b.Role == "blade");
            Expect(report.Components >= request.States[0].Bars.Count, "the assembly holds " + report.Components + " components, the plan has " + request.States[0].Bars.Count + " bars");
            Expect(report.Frames > 5, "only " + report.Frames + " frames were recorded");
            Expect(!string.IsNullOrEmpty(report.VideoFile) && File.Exists(report.VideoFile) && new FileInfo(report.VideoFile).Length > 20_000, "the film is missing or tiny");
            Expect(!string.IsNullOrEmpty(report.AssemblyFile) && File.Exists(report.AssemblyFile), "the assembly file was not saved");
            Expect(!string.IsNullOrEmpty(report.StepFile) && File.Exists(report.StepFile), "the STEP file was not saved");
            Expect(report.Interferences.Count == request.States.Count, "the interference check ran at " + report.Interferences.Count + " of " + request.States.Count + " states");
            Expect(report.Interferences.All(r => r.Count == 0), "interferences: " + string.Join("; ", report.Interferences.Where(r => r.Count > 0).Select(r => r.State + " " + string.Join(", ", r.Pairs))));
            Expect(report.TotalMassKg > 1 && report.TotalMassKg < 200, "the assembly weighs " + report.TotalMassKg.ToString("0.0", CultureInfo.InvariantCulture) + " kg");
            Expect(bladeCount == 3, "the test unit should have three blades, the plan has " + bladeCount);
            // the blade's mass in SOLIDWORKS against the closed-form section of the model: a difference is written into the notes by the run itself
            Expect(!report.Notes.Any(n => n.Contains("A blade weighs")), "blade mass: " + report.Notes.FirstOrDefault(n => n.Contains("A blade weighs")));
            Console.WriteLine("smoke unit: " + report.Components + " components, " + report.Frames + " frames, " + report.TotalMassKg.ToString("0.0", CultureInfo.InvariantCulture) + " kg, " + clock.Elapsed.TotalSeconds.ToString("0") + " s");
        }
        catch (Exception ex) { failures.Add("the run failed: " + ex.Message); }
        finally { Watchdog.Stop(); }

        if (failures.Count == 0) { Console.WriteLine("SMOKE OK"); return 0; }
        foreach (var f in failures) Console.WriteLine("SMOKE FAIL " + f);
        return 1;
    }
}
