using System.Text.Json;
using Sportify.Mechanical;
using Sportify.Simulation.Sun;

// Sportify.Mechanical blade [--span 3] [--width 6] [--out DIR] [--name NAME] [--visible] [--keep-open]
//   Builds the louvre pergola's blade in SOLIDWORKS from the Kinetics inputs (SportifyKineticsInputs.json in Revit's Addins folder, else the built-in values),
//   saves the part and its STEP and SAT exports, reads its mass properties, and prints them against the add-in's own mechanics model.
var opt = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
for (var i = 1; i < args.Length; i++)
    if (args[i].StartsWith("--")) opt[args[i][2..]] = i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[++i] : "true";

var command = args.Length > 0 ? args[0] : "";
if (command == "close")
{
    // closes the SOLIDWORKS instance left open by --keep-open (or any: it asks it to exit, unsaved work would prompt)
    var running = System.Diagnostics.Process.GetProcessesByName("SLDWORKS");
    if (running.Length == 0) { Console.WriteLine("SOLIDWORKS is not running."); return 0; }
    using (var open = SolidWorksSession.Open(false, true)) { open.App.ExitApp(); }
    Console.WriteLine("SOLIDWORKS asked to exit.");
    return 0;
}
if (command == "encode-test")
{
    // a synthetic film through the MP4 encoder, and a frame read back from it: proves Windows can make the video without SOLIDWORKS
    const int W = 640, H = 360, N = 60;
    var mp4 = opt.TryGetValue("out", out var eo) ? eo : Path.Combine(Path.GetTempPath(), "sportify-encode-test.mp4");
    Mp4Encoder.EncodeAsync(mp4, W, H, 20, i =>
    {
        if (i >= N) return null;
        var px = new byte[W * H * 4];
        var cx = 60 + i * 9;
        for (var y = 0; y < H; y++) for (var x = 0; x < W; x++)
        {
            var o = (y * W + x) * 4;
            var inside = Math.Abs(x - cx) < 40 && Math.Abs(y - H / 2) < 40;
            px[o] = inside ? (byte)40 : (byte)(200 - y / 3); px[o + 1] = inside ? (byte)180 : (byte)(210 - y / 3); px[o + 2] = inside ? (byte)240 : (byte)230; px[o + 3] = 255;
        }
        return px;
    }).GetAwaiter().GetResult();
    Console.WriteLine("wrote " + mp4 + " (" + new FileInfo(mp4).Length + " bytes)");
    Console.WriteLine("read back: " + Mp4Encoder.ThumbnailAsync(mp4, Path.ChangeExtension(mp4, ".png"), 1.5).GetAwaiter().GetResult());
    return 0;
}
if (command == "thumb")
{
    // Sportify.Mechanical thumb --mp4 FILE --out FILE.png [--at 1.5]: a frame read back out of a finished video
    if (!opt.TryGetValue("mp4", out var tm) || !File.Exists(tm)) { Console.Error.WriteLine("--mp4 FILE is needed"); return 2; }
    var tout = opt.TryGetValue("out", out var to) ? to : Path.ChangeExtension(tm, ".png");
    Console.WriteLine(Mp4Encoder.ThumbnailAsync(tm, tout, D("at", 1.5)).GetAwaiter().GetResult());
    return 0;
}
if (command == "simulate")
{
    // Sportify.Mechanical simulate --request FILE --out DIR --name NAME [--cancel-file FILE] [--fps 15] [--width 1280] [--height 720] [--stills] [--no-video] [--no-detail] [--stall-min 5] [--timeout-min 45] [--cam X,Y] [--visible]
    //   The unit in the request (the add-in writes it: KineticsRender) as a SOLIDWORKS assembly, moved through its states, checked for interferences, recorded as an MP4.
    //   Talks in lines: PROGRESS <percent> <text>, then RESULT <path of the report json>. Exit code 3 when cancelled.
    try
    {
        if (!opt.TryGetValue("request", out var requestFile) || !File.Exists(requestFile)) { Console.Error.WriteLine("--request FILE is needed"); return 2; }
        var request = JsonSerializer.Deserialize<RenderRequest>(File.ReadAllText(requestFile)) ?? throw new InvalidOperationException("the request is empty");
        if (request.States.Count == 0 || (request.States[0].Bars.Count == 0 && request.States[0].Surfaces.Count == 0)) { Console.Error.WriteLine("the request has no bars or membranes to build"); return 2; }
        var simOut = opt.TryGetValue("out", out var so) ? so : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Sportify Workspace", "Mechanical");
        var simName = opt.TryGetValue("name", out var sn) ? sn : "unit";
        var options = new SimulateOptions
        {
            CancelFile = opt.TryGetValue("cancel-file", out var cf) ? cf : null, NoVideo = opt.ContainsKey("no-video"), StillsOnly = opt.ContainsKey("stills"), Detail = !opt.ContainsKey("no-detail"),
            Fps = (int)D("fps", 15), Width = (int)D("width", 1280) & ~1, Height = (int)D("height", 720) & ~1,
        };
        options.Zoom = D("zoom", 0.92);
        if (opt.TryGetValue("shift", out var shift) && shift.Split(',') is { Length: 2 } sh)
        {
            options.ShiftX = double.Parse(sh[0], System.Globalization.CultureInfo.InvariantCulture); options.ShiftY = double.Parse(sh[1], System.Globalization.CultureInfo.InvariantCulture);
        }
        if (opt.TryGetValue("cam", out var cam))
        {
            var parts = cam.Split(',');
            if (parts.Length == 2) { options.CamX = double.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture); options.CamY = double.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture); }
        }
        // a SOLIDWORKS that stops answering (a window of its own that a hidden session cannot show) is stopped after --stall-min minutes without a sign of life, and a run
        // that takes longer than --timeout-min altogether is ended: exit code 4, "STALLED ..."
        Watchdog.Start(TimeSpan.FromMinutes(Math.Max(6, D("stall-min", 5))), TimeSpan.FromMinutes(D("stall-min", 5)), TimeSpan.FromMinutes(D("timeout-min", 45)));
        Console.WriteLine("PROGRESS 1 Starting SOLIDWORKS (the first start can take a minute or two)");
        Console.Out.Flush();
        using var simSession = SolidWorksSession.Open(opt.ContainsKey("visible"), opt.ContainsKey("keep-open"));
        Watchdog.Beat("SOLIDWORKS started");
        Console.WriteLine("PROGRESS 2 " + simSession.Info());
        var simReport = UnitAssembly.Run(simSession, request, simOut, simName, options);
        Watchdog.Stop();
        var reportPath = Path.Combine(simOut, simName + "_simulation.json");
        File.WriteAllText(reportPath, UnitAssembly.ToJson(simReport));
        Console.WriteLine("PROGRESS 100 Done");
        Console.WriteLine("RESULT " + reportPath);
        return 0;
    }
    catch (OperationCanceledException) { Watchdog.Stop(); Console.WriteLine("CANCELLED"); return 3; }
    catch (Exception ex) { Watchdog.Stop(); Console.Error.WriteLine(ex.ToString()); return 1; }
}
if (command == "probe-extrude")
{
    using var extrudeSession = SolidWorksSession.Open(opt.ContainsKey("visible"), opt.ContainsKey("keep-open"));
    Console.WriteLine(extrudeSession.Info());
    return Probe.RunExtrude(extrudeSession, (int)D("variant", 1));
}
if (command == "watchdog-test") return Smoke.WatchdogTest();
if (command == "smoke") return Smoke.Run(opt.TryGetValue("out", out var smokeOut) ? smokeOut : Path.Combine(Path.GetTempPath(), "sportify-smoke"));
if (command == "probe")
{
    using var probeSession = SolidWorksSession.Open(opt.ContainsKey("visible"), opt.ContainsKey("keep-open"));
    Console.WriteLine(probeSession.Info());
    return Probe.Run(probeSession, opt.TryGetValue("out", out var po) ? po : Path.Combine(Path.GetTempPath(), "sportify-probe"));
}
if (command == "probe-length")
{
    using var lengthSession = SolidWorksSession.Open(opt.ContainsKey("visible"), opt.ContainsKey("keep-open"));
    Console.WriteLine(lengthSession.Info());
    return Probe.RunLength(lengthSession, opt.TryGetValue("out", out var pl) ? pl : Path.Combine(Path.GetTempPath(), "sportify-probe"));
}
if (command != "blade") { Console.WriteLine("usage: Sportify.Mechanical blade [--span 3] [--width 6] [--out DIR] [--name NAME] [--visible] [--keep-open] [--write-inputs]"); Console.WriteLine("       Sportify.Mechanical close"); return 2; }

double D(string key, double fallback) => opt.TryGetValue(key, out var v) && double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : fallback;

var overrides = InputsFile.Load();
var design = new LouvreDesign(overrides);
var spec = new BladeSpec(D("span", 3.0), design["chord_m"], design["thickness_m"], design["wall_m"], design["density_kg_m3"], design["youngs_modulus_pa"]);
var width = D("width", 6.0);
var outDir = opt.TryGetValue("out", out var o) ? o : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Sportify Workspace", "Mechanical");
var name = opt.TryGetValue("name", out var n) ? n : $"LouvreBlade_L{(int)Math.Round(spec.SpanM * 100)}_C{(int)Math.Round(spec.ChordM * 1000)}_T{(int)Math.Round(spec.ThicknessM * 1000)}";

Console.WriteLine($"blade {spec.ChordM * 1000:0} x {spec.ThicknessM * 1000:0} mm, wall {spec.WallM * 1000:0.#} mm, span {spec.SpanM:0.##} m  ({(overrides.Count > 0 ? overrides.Count + " input(s) from SportifyKineticsInputs.json" : "built-in inputs")})");
Console.WriteLine("starting SOLIDWORKS (the first start can take a minute or two) ...");

using var sw = SolidWorksSession.Open(opt.ContainsKey("visible"), opt.ContainsKey("keep-open"));
Console.WriteLine(sw.Info());

var report = LouvreBlade.Build(sw, spec, outDir, name);

// the add-in's own model of the same pergola, for the comparison
var states = new List<LouvreActuationModel.ActuationState>();
var mech = LouvreMechanics.Analyse(design, width, spec.SpanM, 1000, 0, states);

Console.WriteLine();
Console.WriteLine($"  volume        {report.VolumeM3 * 1e6,10:0.0} cm3      section {report.SectionAreaM2 * 1e6:0.0} mm2");
Console.WriteLine($"  mass          {report.MassKg,10:0.000} kg       formula {report.ExpectedMassKg:0.000} kg   (add-in model: {mech.BladeMassKg:0.000} kg; SOLIDWORKS' own reading {report.SolidWorksMassKg:0.000} kg at {report.SolidWorksDensityKgM3:0} kg/m3)");
Console.WriteLine($"  per metre     {report.MassPerMetreKg,10:0.000} kg/m");
Console.WriteLine($"  area moment   {report.MomentAboutChordAxisM4,10:0.0000e+00} m4     formula {report.ExpectedMomentM4:0.0000e+00} m4");
Console.WriteLine($"  centre of mass ({string.Join(", ", report.CentreOfMassM.Select(v => v.ToString("0.000")))}) m");
Console.WriteLine($"  files: {report.PartFile}");
Console.WriteLine($"         {report.StepFile}");
Console.WriteLine($"         {report.SatFile}");
foreach (var note in report.Notes) Console.WriteLine("  note: " + note);

var ok = Math.Abs(report.MassKg - report.ExpectedMassKg) < 0.01 * report.ExpectedMassKg && Math.Abs(report.MomentAboutChordAxisM4 - report.ExpectedMomentM4) < 0.02 * report.ExpectedMomentM4;
Console.WriteLine(ok ? "\nSOLIDWORKS agrees with the section maths (mass within 1%, area moment within 2%)." : "\nDIFFERENCE: SOLIDWORKS and the section maths disagree; look at the notes above.");
File.WriteAllText(Path.Combine(outDir, name + ".json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));

if (opt.ContainsKey("write-inputs"))
{
    if (!ok) { Console.WriteLine("Not writing the inputs: the checks above failed."); return 1; }
    var written = InputsFile.Merge(("blade_mass_per_m_kg", Math.Round(report.MassPerMetreKg, 4)), ("blade_inertia_m4", double.Parse(report.MomentAboutChordAxisM4.ToString("G5", System.Globalization.CultureInfo.InvariantCulture), System.Globalization.CultureInfo.InvariantCulture)));
    Console.WriteLine("Wrote blade_mass_per_m_kg and blade_inertia_m4 into " + written + ": Kinetics now uses the CAD numbers.");
}
return ok ? 0 : 1;

/// <summary>The Kinetics inputs the mechanical engineer edits, read the same way the add-in reads them.</summary>
static class InputsFile
{
    static string PathOf() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Autodesk", "Revit", "Addins", "2025", "SportifyKineticsInputs.json");

    /// <summary>Adds or replaces values under "overrides", keeping everything else in the file (other overrides, the defaults for reference) as it is; the file is created when there is none.</summary>
    public static string Merge(params (string Key, double Value)[] values)
    {
        var path = PathOf();
        var root = new Dictionary<string, System.Text.Json.Nodes.JsonNode?>();
        if (File.Exists(path))
        {
            try { foreach (var kv in System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!.AsObject()) root[kv.Key] = kv.Value?.DeepClone(); } catch { /* an unreadable file is replaced */ }
        }
        var overrides = root.TryGetValue("overrides", out var o) && o is System.Text.Json.Nodes.JsonObject jo ? jo : new System.Text.Json.Nodes.JsonObject();
        foreach (var (key, value) in values) overrides[key] = value;
        root["overrides"] = overrides;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, new System.Text.Json.Nodes.JsonObject(root.Select(kv => KeyValuePair.Create(kv.Key, kv.Value))).ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return path;
    }

    public static Dictionary<string, double> Load()
    {
        var overrides = new Dictionary<string, double>();
        var path = PathOf();
        try
        {
            if (!File.Exists(path)) return overrides;
            using var doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (doc.RootElement.TryGetProperty("overrides", out var o) && o.ValueKind == JsonValueKind.Object)
                foreach (var p in o.EnumerateObject())
                    if (p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetDouble(out var v)) overrides[p.Name] = v;
        }
        catch (Exception ex) { Console.WriteLine("SportifyKineticsInputs.json could not be read, built-in values used: " + ex.Message); }
        return overrides;
    }
}
