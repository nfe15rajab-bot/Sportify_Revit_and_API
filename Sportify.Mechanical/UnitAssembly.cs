using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.Text.Json;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using Sportify.Simulation.Sun;

namespace Sportify.Mechanical;

internal sealed class SimulateOptions
{
    public int Fps = 15, Width = 1280, Height = 720;
    public double MoveS = 2.0, HoldS = 1.2, IntroS = 3.0;
    public string? CancelFile;
    public List<string> OutroLines = new();                  // what the closing card says (filled before the frames are made)
    public double OutroS = 3.5;
    public bool NoVideo;
    public bool StillsOnly;
    public bool Detail = true;                                // a close-up of the mechanism (cranks, piston, housing) after the states
    public double ShiftX = 0, ShiftY = 0, Zoom = 0.92;
    public double CamX = double.NaN, CamY = double.NaN;      // extra rotation of the camera from the standard view, radians (NaN: the default for the kind)
}

internal sealed class InterferenceRow
{
    public string State { get; set; } = "";
    public int Count { get; set; }
    public List<string> Pairs { get; set; } = new();
}

internal sealed class SimulateReport
{
    public string PieceName { get; set; } = "";
    public string Kind { get; set; } = "";
    public string VideoFile { get; set; } = "";
    public string AssemblyFile { get; set; } = "";
    public string StepFile { get; set; } = "";
    public int Parts { get; set; }
    public int Components { get; set; }
    public double TotalMassKg { get; set; }
    public Dictionary<string, double> MassByRoleKg { get; set; } = new();
    public List<InterferenceRow> Interferences { get; set; } = new();
    public int Frames { get; set; }
    public double Seconds { get; set; }
    public double BladeMassSolidWorksKg { get; set; }
    public double BladeMassModelKg { get; set; }
    public List<string> Notes { get; set; } = new();
}

/// <summary>One kind of part: a section extruded over a length, made once and used for every bar that shares it.</summary>
internal sealed class PartSpec
{
    public string Key = "", Role = "", Path = "";
    public double SizeU, SizeV, Length, Wall;
    public bool Circular;
    public double VolumeM3, MassKg;
    public double[] Colour = { 0.6, 0.6, 0.6 };
    public ModelDoc2? Doc;
    public string LengthFeature = "";        // the extrusion feature whose depth (D1) is the part's length
    public int Instances;
    public List<double[]>? Poly;             // a plate's outline in its own plane (x, y), when it is not a rectangle
}

/// <summary>A component in the assembly: the bar (or plate) of the plan it stands for.</summary>
internal sealed class Body
{
    public string Role = "";
    public bool Dynamic;
    public PartSpec Part = null!;
    public Component2 Comp = null!;
    public int Index;                // the bar's index in every state (or the surface's, for a plate)
    public bool IsPlate;
    public bool Varies;              // a bar that changes length (a sail's mast running in and out): its part's length is driven, see BarGroup
    public bool Counted = true;      // a sail's plate comes in many sizes (one is showing at a time): only one counts in the mass
}

/// <summary>A bar whose length changes through the film (a sail's mast running in and out): the extrusion length of its part is a dimension, driven frame by frame.</summary>
internal sealed class BarGroup
{
    public int BarIndex;
    public Body Body = null!;
    public double Length;
}

/// <summary>The sizes a sail's fabric takes through the film: one plate part per size, one showing at a time.</summary>
internal sealed class PlateGroup
{
    public int SurfaceIndex;
    public Dictionary<string, Body> Variants = new();
    public Body? Current;
}

/// <summary>
/// The kinetic unit as a mechanical assembly in SOLIDWORKS: every bar of the plan is a part (an extrusion of its real section, hollow where the add-in's mechanics assume a wall),
/// every part is put where the plan puts it, and the assembly is moved through the unit's states. SOLIDWORKS renders every frame (its own graphics, hidden window); the tool
/// draws the state on it and encodes the MP4. The assembly and its STEP file are kept. A motion study cannot be made in a hidden session (SOLIDWORKS returns none), so the
/// tool drives the components itself, and checks the assembly for interferences at each state.
/// </summary>
internal static class UnitAssembly
{
    const double SteelKgM3 = 7850;

    // ------------------------------------------------------------------ geometry helpers

    /// <summary>The unit's frame (x along, y across, z up, right-handed) into SOLIDWORKS' (y up): a proper rotation, (x, y, z) -> (x, z, -y).</summary>
    static V3 Sw(V3 v) => new V3(v.X, v.Z, -v.Y);

    static V3 V(double[] a) => new V3(a[0], a[1], a[2]);

    static (V3 U, V3 V, V3 A, V3 P) BarPose(V3 p0, V3 p1, V3 uHint)
    {
        var a = (p1 - p0).Unit();
        var u = uHint - a * uHint.Dot(a);
        if (u.Length < 1e-9) u = a.Cross(Math.Abs(a.Z) > 0.9 ? V3.UnitX : V3.UnitZ);
        u = u.Unit();
        return (u, a.Cross(u).Unit(), a, p0);
    }

    static V3 Lerp(V3 a, V3 b, double t) => a + (b - a) * t;

    static (V3 U, V3 V, V3 A, V3 P) LerpBar(RenderBar a, RenderBar b, double t, double partLength)
    {
        var p0 = Lerp(V(a.P0), V(b.P0), t); var p1 = Lerp(V(a.P1), V(b.P1), t);
        // a part cannot change its length: it keeps its start (a bar that changes length is made in every length it takes, see BarGroup)
        if (Math.Abs((p1 - p0).Length - partLength) > 0.002) p1 = p0 + (p1 - p0).Unit() * partLength;
        return BarPose(p0, p1, Lerp(V(a.U), V(b.U), t));
    }

    static double BarLength(RenderBar a, RenderBar b, double t) => (Lerp(V(a.P1), V(b.P1), t) - Lerp(V(a.P0), V(b.P0), t)).Length;

    /// <summary>A membrane as a thin plate: its axes (along the AB/DC edges, across them, the normal), its origin, and its outline in that plane (three points when the fourth is the third, as a triangle's apex is).</summary>
    static ((V3 U, V3 V, V3 A, V3 P) Pose, List<double[]> Poly) PlateGeom(RenderSurface s, double thickness)
    {
        V3 a = V(s.A), b = V(s.B), c = V(s.C), d = V(s.D);
        var centre = (a + b + c + d) * 0.25;
        var e1 = ((b - a) + (c - d)).Unit();
        var e2r = (d - a) + (c - b);
        var e2 = (e2r - e1 * e2r.Dot(e1)).Unit();
        var n = e1.Cross(e2).Unit();
        double[] L(V3 p) => new[] { Math.Round((p - centre).Dot(e1), 3), Math.Round((p - centre).Dot(e2), 3) };
        var poly = (c - d).Length < 0.05 ? new List<double[]> { L(a), L(b), L(c) } : new List<double[]> { L(a), L(b), L(c), L(d) };
        return ((e1, e2, n, centre - n * (thickness / 2)), poly);
    }

    static (V3 U, V3 V, V3 A, V3 P) PlatePose(RenderSurface s, double thickness) => PlateGeom(s, thickness).Pose;

    /// <summary>The plate's outline on a grid (15 cm): plates of the same outline share a part.</summary>
    static string PolyKey(List<double[]> poly) => string.Join("|", poly.Select(p => Math.Round(p[0] / 0.15).ToString(CultureInfo.InvariantCulture) + "," + Math.Round(p[1] / 0.15).ToString(CultureInfo.InvariantCulture)));

    static List<double[]> Snap(List<double[]> poly) => poly.Select(p => new[] { Math.Round(p[0] / 0.15) * 0.15, Math.Round(p[1] / 0.15) * 0.15 }).ToList();

    static double PolyArea(List<double[]> poly) { double a = 0; for (var i = 0; i < poly.Count; i++) { var p = poly[i]; var q = poly[(i + 1) % poly.Count]; a += p[0] * q[1] - q[0] * p[1]; } return Math.Abs(a) / 2; }

    static RenderSurface LerpSurface(RenderSurface a, RenderSurface b, double t) => new RenderSurface { A = Mix(a.A, b.A, t), B = Mix(a.B, b.B, t), C = Mix(a.C, b.C, t), D = Mix(a.D, b.D, t) };

    static double[] Mix(double[] a, double[] b, double t) => new[] { a[0] + (b[0] - a[0]) * t, a[1] + (b[1] - a[1]) * t, a[2] + (b[2] - a[2]) * t };

    /// <summary>SOLIDWORKS' transform array: the images of the part's x, y and z axes (three rows), then the translation, then the scale.</summary>
    static double[] Array16((V3 U, V3 V, V3 A, V3 P) pose)
    {
        V3 u = Sw(pose.U), v = Sw(pose.V), a = Sw(pose.A), p = Sw(pose.P);
        return new[] { u.X, u.Y, u.Z, v.X, v.Y, v.Z, a.X, a.Y, a.Z, p.X, p.Y, p.Z, 1, 0, 0, 0 };
    }

    // ------------------------------------------------------------------ parts

    static (double Wall, bool Circular, double DensityKgM3, double[] Colour) SectionFor(string role, string kind, RenderRequest req)
    {
        double In(string key, double fallback) => req.Input(key, fallback);
        switch (role)
        {
            case "blade":
            case "fin":
            case "slat":
                return (In("wall_m", 0.002), false, In("density_kg_m3", 2700), kind == "slats" || kind == "fins" ? new[] { 0.63, 0.45, 0.28 } : new[] { 0.78, 0.80, 0.83 });
            case "post": return (In("post_wall_m", 0.003), false, SteelKgM3, new[] { 0.55, 0.57, 0.60 });
            case "rail": return (kind == "fence" ? In("fence_rail_wall_m", 0.004) : In("post_wall_m", 0.003), false, SteelKgM3, new[] { 0.55, 0.57, 0.60 });
            case "mast": return (In("mast_wall_m", 0.006), true, SteelKgM3, new[] { 0.96, 0.76, 0.10 });
            case "housing": return (kind == "fence" ? 0.003 : 0.004, kind == "fence", SteelKgM3, new[] { 0.45, 0.47, 0.50 });      // a fence's roller is a tube; a louvre's actuator housing a box
            case "crank": return (0, false, SteelKgM3, new[] { 0.80, 0.40, 0.15 });
            case "piston": return (0, true, SteelKgM3, new[] { 0.80, 0.40, 0.15 });
            case "motor": return (0.004, false, SteelKgM3, new[] { 0.15, 0.55, 0.35 });
            case "track": return (0.004, false, SteelKgM3, new[] { 0.45, 0.47, 0.50 });
            case "carriage": return (0, false, SteelKgM3, new[] { 0.20, 0.45, 0.75 });
            case "curtainslat": return (0, false, 2700, new[] { 0.32, 0.42, 0.36 });
            case "bottombar": return (0.003, false, SteelKgM3, new[] { 0.30, 0.31, 0.33 });
            case "rod": return (0, true, SteelKgM3, new[] { 0.25, 0.26, 0.28 });      // solid
            case "sail": return (0, false, 350, new[] { 0.92, 0.93, 0.95 });            // 350 g per square metre of fabric spread over the plate's thickness is handled in the mass, not here
            default: return (0.003, false, SteelKgM3, new[] { 0.6, 0.6, 0.6 });
        }
    }

    static PartSpec MakePart(SolidWorksSession sw, string partsDir, string name, PartSpec spec, string kind)
    {
        var model = (ModelDoc2)sw.App.NewDocument(sw.Template(assembly: false), 0, 0, 0)
                    ?? throw new InvalidOperationException("SOLIDWORKS could not create a part from its template.");
        Feature? plane = null;
        for (var f = (Feature?)model.FirstFeature(); f != null; f = (Feature?)f.GetNextFeature()) if (f.GetTypeName2() == "RefPlane") { plane = f; break; }
        plane!.Select2(false, 0);
        var sk = model.SketchManager;
        sk.InsertSketch(true);
        // entities are added without SOLIDWORKS inferring relations between them: with inference on (a setting that differs from one session or machine to the next) the inner outline of a
        // hollow section snaps to the outer one and the extrusion of the two loops fails (found by the smoke test)
        sk.AddToDB = true;
        var hollow = spec.Wall > 0 && spec.Wall < Math.Min(spec.SizeU, spec.SizeV) / 2 - 1e-6;
        if (spec.Poly != null)
        {
            for (var i = 0; i < spec.Poly.Count; i++)
            {
                var p = spec.Poly[i]; var q = spec.Poly[(i + 1) % spec.Poly.Count];
                sk.CreateLine(p[0], p[1], 0, q[0], q[1], 0);
            }
        }
        else if (spec.Circular)
        {
            var r = spec.SizeU / 2;
            sk.CreateCircleByRadius(0, 0, 0, r);
            if (hollow) sk.CreateCircleByRadius(0, 0, 0, r - spec.Wall);
        }
        else
        {
            sk.CreateCornerRectangle(-spec.SizeU / 2, -spec.SizeV / 2, 0, spec.SizeU / 2, spec.SizeV / 2, 0);
            if (hollow) sk.CreateCornerRectangle(-(spec.SizeU / 2 - spec.Wall), -(spec.SizeV / 2 - spec.Wall), 0, spec.SizeU / 2 - spec.Wall, spec.SizeV / 2 - spec.Wall, 0);
        }
        sk.AddToDB = false;
        sk.InsertSketch(true);
        Feature? sketch = null;
        for (var f = (Feature?)model.FirstFeature(); f != null; f = (Feature?)f.GetNextFeature()) if (f.GetTypeName2() == "ProfileFeature") sketch = f;
        sketch!.Select2(false, 0);
        var body = model.FeatureManager.FeatureExtrusion3(true, false, false, (int)swEndConditions_e.swEndCondBlind, (int)swEndConditions_e.swEndCondBlind,
            spec.Length, 0, false, false, false, false, 0, 0, false, false, false, false, true, true, true, (int)swStartConditions_e.swStartSketchPlane, 0, false);
        if (body == null) throw new InvalidOperationException("The extrusion of the " + spec.Role + " failed (section " + spec.SizeU.ToString("0.####", CultureInfo.InvariantCulture) + " x " + spec.SizeV.ToString("0.####", CultureInfo.InvariantCulture) + " m, wall " + spec.Wall.ToString("0.####", CultureInfo.InvariantCulture) + ", length " + spec.Length.ToString("0.####", CultureInfo.InvariantCulture) + " m).");

        // the colour is only for the eye: the part's appearance (R, G, B, ambient, diffuse, specular, shininess, transparency, emission), not its physics
        var appearance = new[] { spec.Colour[0], spec.Colour[1], spec.Colour[2], 0.5, 0.7, 0.4, 0.3, 0, 0 };
        try { body.SetMaterialPropertyValues(appearance); }                                   // on the feature: what the part shows, whatever it is placed in
        catch (Exception) { try { ((PartDoc)model).MaterialPropertyValues = appearance; } catch (Exception) { /* left grey */ } }

        var mass = model.Extension.CreateMassProperty();
        mass.UseSystemUnits = true;
        spec.VolumeM3 = mass.Volume;
        spec.LengthFeature = body.Name;
        spec.Path = System.IO.Path.Combine(partsDir, name + "_" + spec.Role + "_" + spec.Key + ".SLDPRT");
        int errors = 0, warnings = 0;
        model.Extension.SaveAs(spec.Path, (int)swSaveAsVersion_e.swSaveAsCurrentVersion, (int)swSaveAsOptions_e.swSaveAsOptions_Silent, null, ref errors, ref warnings);
        spec.Doc = model;
        return spec;
    }

    // ------------------------------------------------------------------ the run

    internal static void Progress(int percent, string text) { Watchdog.Beat(text); Console.WriteLine("PROGRESS " + percent + " " + text); Console.Out.Flush(); }

    static void CheckCancel(SimulateOptions o)
    {
        Watchdog.Beat();
        if (o.CancelFile != null && File.Exists(o.CancelFile)) throw new OperationCanceledException("cancelled");
    }

    public static SimulateReport Run(SolidWorksSession sw, RenderRequest req, string outDir, string name, SimulateOptions opt)
    {
        var clock = Stopwatch.StartNew();
        var report = new SimulateReport { PieceName = req.PieceName, Kind = req.Kind };
        outDir = Path.GetFullPath(outDir);                       // SOLIDWORKS matches an open part by its path: forward slashes make AddComponent5 fail
        Directory.CreateDirectory(outDir);
        var partsDir = Path.Combine(outDir, name + "_parts");
        Directory.CreateDirectory(partsDir);
        var s0 = req.States[0];
        var plan = FramePlan(req, opt);
        Progress(2, "Building the parts");

        // ---- one part per distinct bar
        var specs = new Dictionary<string, PartSpec>();
        PartSpec Spec(string role, double sizeU, double sizeV, double length, bool platE, string unique = "")
        {
            var key = unique + role + "|" + sizeU.ToString("0.0000", CultureInfo.InvariantCulture) + "|" + sizeV.ToString("0.0000", CultureInfo.InvariantCulture) + "|" + length.ToString("0.0000", CultureInfo.InvariantCulture);
            if (!specs.TryGetValue(key, out var spec))
            {
                var (wall, circ, _, colour) = SectionFor(role, req.Kind, req);
                spec = new PartSpec { Key = (specs.Count + 1).ToString(), Role = role, SizeU = sizeU, SizeV = sizeV, Length = length, Wall = wall, Circular = circ, Colour = colour };
                specs[key] = spec;
            }
            spec.Instances++;
            return spec;
        }
        var bodies = new List<Body>();
        var barGroups = new List<BarGroup>();
        for (var i = 0; i < s0.Bars.Count; i++)
        {
            var b = s0.Bars[i];
            var length = (V(b.P1) - V(b.P0)).Length;
            var lengths = new List<double> { length };
            foreach (var fr in plan)
            {
                var to = req.States[Math.Min(fr.To, req.States.Count - 1)];
                lengths.Add(BarLength(req.States[fr.From].Bars[i], to.Bars[Math.Min(i, to.Bars.Count - 1)], fr.T));
            }
            if (lengths.Max() - lengths.Min() > 0.05)
            {
                // a mast that runs in and out: a part of its own whose length is a dimension, set at every frame
                var body = new Body { Role = b.Role, Dynamic = b.Dynamic, Index = i, Varies = true, Part = Spec(b.Role, b.SizeU, b.SizeV, length, false, "bar" + i + "|") };
                bodies.Add(body);
                barGroups.Add(new BarGroup { BarIndex = i, Body = body, Length = length });
            }
            else bodies.Add(new Body { Role = b.Role, Dynamic = b.Dynamic, Index = i, Part = Spec(b.Role, b.SizeU, b.SizeV, length, false) });
        }
        const double PlateThicknessM = 0.003;
        var skipped = new List<string>();
        var plateGroups = new List<PlateGroup>();
        for (var i = 0; i < s0.Surfaces.Count; i++)
        {
            var f = s0.Surfaces[i];
            if (f.Role != "sail") { skipped.Add(f.Role); continue; }        // a fence's curtain is made of slats in the CAD model (bars), not a plate
            // the fabric changes its size and shape as the masts run: one plate part per outline it takes through the film (on a 15 cm grid), one showing at a time
            var group = new PlateGroup { SurfaceIndex = i };
            var firstKey = PolyKey(PlateGeom(s0.Surfaces[i], PlateThicknessM).Poly);
            var outlines = new Dictionary<string, List<double[]>>();
            foreach (var fr in plan)
            {
                var geom = PlateGeom(LerpSurface(req.States[fr.From].Surfaces[i], req.States[Math.Min(fr.To, req.States.Count - 1)].Surfaces[Math.Min(i, req.States[Math.Min(fr.To, req.States.Count - 1)].Surfaces.Count - 1)], fr.T), PlateThicknessM);
                var key = PolyKey(geom.Poly);
                if (!outlines.ContainsKey(key)) outlines[key] = Snap(geom.Poly);
            }
            if (!outlines.ContainsKey(firstKey)) outlines[firstKey] = Snap(PlateGeom(s0.Surfaces[i], PlateThicknessM).Poly);
            foreach (var kv in outlines)
            {
                var xs = kv.Value.Select(p => p[0]); var ys = kv.Value.Select(p => p[1]);
                var spec = new PartSpec { Key = "s" + (specs.Count + 1), Role = "sail", SizeU = xs.Max() - xs.Min(), SizeV = ys.Max() - ys.Min(), Length = PlateThicknessM, Wall = 0, Colour = new[] { 0.92, 0.93, 0.95 }, Poly = kv.Value, Instances = 1 };
                specs[i + "|" + kv.Key] = spec;
                var body = new Body { Role = "sail", Dynamic = f.Dynamic, Index = i, IsPlate = true, Part = spec, Counted = kv.Key == firstKey };
                bodies.Add(body); group.Variants[kv.Key] = body;
            }
            plateGroups.Add(group);
        }
        foreach (var spec in specs.Values)
        {
            CheckCancel(opt);
            MakePart(sw, partsDir, name, spec, req.Kind);
            var (_, _, density, _) = SectionFor(spec.Role, req.Kind, req);
            // a sail's plate stands for fabric: 0.35 kg/m2 (the input) over its area, not steel or aluminium
            spec.MassKg = spec.Role == "sail" ? req.Input("fabric_mass_kg_m2", 0.35) * PolyArea(spec.Poly ?? new List<double[]> { new[] { 0.0, 0.0 }, new[] { spec.SizeU, 0.0 }, new[] { spec.SizeU, spec.SizeV }, new[] { 0.0, spec.SizeV } })
                        : spec.Role == "curtainslat" ? req.Input("fence_curtain_kg_m2", 0.6) * spec.Length * spec.SizeU : spec.VolumeM3 * density;
            Progress(2 + 18 * specs.Values.TakeWhile(x => x != spec).Count() / Math.Max(1, specs.Count), "Built the " + spec.Role + " (" + spec.Instances + " of them)");
        }
        report.Parts = specs.Values.Count(p => p.Role != "sail") + (plateGroups.Count > 0 ? 1 : 0);

        // ---- the assembly
        Progress(22, "Assembling " + bodies.Count + " components");
        var asm = (ModelDoc2)sw.App.NewDocument(sw.Template(true), 0, 0, 0) ?? throw new InvalidOperationException("SOLIDWORKS could not create an assembly from its template.");
        var assy = (AssemblyDoc)asm;
        var mu = (MathUtility)sw.App.GetMathUtility();
        try
        {
            foreach (var body in bodies)
            {
                CheckCancel(opt);
                body.Comp = assy.AddComponent5(body.Part.Path, (int)swAddComponentConfigOptions_e.swAddComponentConfigOptions_CurrentSelectedConfig, "", false, "", 0, 0, 0)
                            ?? throw new InvalidOperationException("SOLIDWORKS did not add the " + body.Role + " to the assembly.");
            }
            asm.ClearSelection2(true);
            bodies[0].Comp.Select4(false, null, false);
            assy.UnfixComponent();
            asm.ClearSelection2(true);
            report.Components = bodies.Count(b => b.Counted);
            foreach (var g in plateGroups)
            {
                foreach (var v in g.Variants.Values) if (!v.Counted) v.Comp.Visible = (int)swComponentVisibilityState_e.swComponentHidden;
                g.Current = g.Variants.Values.First(v => v.Counted);
            }

            void Pose(int fromState, int toState, double t, bool everything)
            {
                var a = req.States[fromState]; var b = req.States[toState];
                foreach (var body in bodies)
                {
                    if (body.IsPlate || body.Varies || (!everything && !body.Dynamic)) continue;
                    var pose = LerpBar(a.Bars[body.Index], b.Bars[Math.Min(body.Index, b.Bars.Count - 1)], t, body.Part.Length);
                    body.Comp.Transform2 = (MathTransform)mu.CreateTransform(Array16(pose));
                }
                var lengthChanged = false;
                foreach (var g in barGroups)
                {
                    var ba = a.Bars[g.BarIndex]; var bb = b.Bars[Math.Min(g.BarIndex, b.Bars.Count - 1)];
                    var length = BarLength(ba, bb, t);
                    if (Math.Abs(length - g.Length) > 0.002 && g.Body.Part.Doc?.Parameter("D1@" + g.Body.Part.LengthFeature) is Dimension dim)
                    {
                        dim.SystemValue = Math.Max(0.02, length);
                        g.Body.Part.Doc.EditRebuild3();
                        g.Length = length; lengthChanged = true;
                    }
                    g.Body.Comp.Transform2 = (MathTransform)mu.CreateTransform(Array16(LerpBar(ba, bb, t, g.Length)));
                }
                if (lengthChanged) asm.EditRebuild3();
                foreach (var g in plateGroups)
                {
                    var surface = LerpSurface(a.Surfaces[g.SurfaceIndex], b.Surfaces[Math.Min(g.SurfaceIndex, b.Surfaces.Count - 1)], t);
                    var geom = PlateGeom(surface, PlateThicknessM);
                    if (!g.Variants.TryGetValue(PolyKey(geom.Poly), out var show)) show = g.Current ?? g.Variants.Values.First();
                    if (!ReferenceEquals(g.Current, show))
                    {
                        if (g.Current != null) g.Current.Comp.Visible = (int)swComponentVisibilityState_e.swComponentHidden;
                        show.Comp.Visible = (int)swComponentVisibilityState_e.swComponentVisible;
                        g.Current = show;
                    }
                    show.Comp.Transform2 = (MathTransform)mu.CreateTransform(Array16(geom.Pose));
                }
            }
            Pose(0, 0, 0, true);
            asm.ForceRebuild3(false);

            // ---- what the assembly weighs, against what the add-in's mechanics says the blade weighs
            foreach (var body in bodies.Where(b => b.Counted)) report.MassByRoleKg[body.Role] = report.MassByRoleKg.GetValueOrDefault(body.Role) + body.Part.MassKg;
            report.TotalMassKg = report.MassByRoleKg.Values.Sum();
            var blade = specs.Values.FirstOrDefault(p => p.Role == "blade" || p.Role == "fin" || p.Role == "slat");
            if (blade != null)
            {
                double c = req.Input("chord_m", 0.15), t = req.Input("thickness_m", 0.03), w = Math.Min(req.Input("wall_m", 0.002), Math.Min(c, t) / 2 - 1e-6);
                var modelPerM = req.Input("blade_mass_per_m_kg", 0) > 0 ? req.Input("blade_mass_per_m_kg", 0) : req.Input("density_kg_m3", 2700) * (c * t - (c - 2 * w) * (t - 2 * w));
                report.BladeMassSolidWorksKg = blade.MassKg; report.BladeMassModelKg = modelPerM * blade.Length;
                if (Math.Abs(report.BladeMassSolidWorksKg - report.BladeMassModelKg) > 0.01 * report.BladeMassModelKg)
                    report.Notes.Add("A blade weighs " + report.BladeMassSolidWorksKg.ToString("0.000") + " kg in SOLIDWORKS against " + report.BladeMassModelKg.ToString("0.000") + " kg in the add-in's model: the CAD blade mass per metre (blade_mass_per_m_kg) is in use, or the sections differ.");
            }
            if (skipped.Count > 0) report.Notes.Add("The " + string.Join(", ", skipped.Distinct()) + " is drawn as slats that follow the bottom bar (a real one is a rolled curtain whose height changes as it is paid out); the guide rails, roller housing, motor and bottom bar are as in the plan.");

            // ---- camera: the unit's front (an overhead louvre, a sail) or its outer face (a screen, a fence), from a little above and to the side
            var view = asm.ActiveView as ModelView;
            var outside = req.Kind == "slats" || req.Kind == "fins" || req.Kind == "fence";
            void Camera()
            {
                try { if (view != null) { view.FrameWidth = opt.Width * 3 / 2; view.FrameHeight = opt.Height * 3 / 2; } } catch (Exception) { /* a hidden window may keep its size */ }
                asm.ShowNamedView2(outside ? "*Back" : "*Front", outside ? (int)swStandardViews_e.swBackView : (int)swStandardViews_e.swFrontView);
                var ax = double.IsNaN(opt.CamX) ? (outside ? 0.55 : 0.55) : opt.CamX;         // the standard view turned about its own axes
                var ay = double.IsNaN(opt.CamY) ? (outside ? 0.45 : -0.55) : opt.CamY;
                view?.RotateAboutCenter(ax, ay);
                asm.ViewZoomtofit2();
                view?.ZoomByFactor(opt.Zoom);
                if (opt.ShiftX != 0 || opt.ShiftY != 0) view?.TranslateBy(opt.ShiftX, opt.ShiftY);
            }
            int ae = 0;
            sw.App.ActivateDoc3(asm.GetTitle(), false, (int)swRebuildOnActivation_e.swDontRebuildActiveDoc, ref ae);
            view = asm.ActiveView as ModelView ?? view;
            Camera();
            asm.GraphicsRedraw2();
            // pictures are taken at the window's own size (the model is centred in that) and cropped to the film's shape afterwards
            int capW = opt.Width, capH = opt.Height;
            try { if (view != null && view.FrameWidth >= 320 && view.FrameHeight >= 240) { capW = view.FrameWidth; capH = view.FrameHeight; } } catch (Exception) { /* keep the film's size */ }

            // ---- interferences at every state (pairs that are meant to touch are not interferences: a blade's end in its rail, a post in its rail)
            Progress(26, "Checking every state for interferences");
            var expected = new HashSet<string> { "blade|rail", "fin|rail", "slat|rail", "post|rail", "rail|rail", "blade|rod", "fin|rod", "mast|sail", "housing|rail", "bottombar|rail", "bottombar|housing", "housing|post", "post|post",
                                              // the mechanism: a crank is pinned to its blade and to the rod, the piston runs into its housing and through what it passes; a carriage sits on its track, a motor at the end of one; a curtain's slats lap
                                              "blade|crank", "crank|fin", "crank|rod", "piston|rod", "housing|piston", "piston|rail", "piston|post", "carriage|mast", "carriage|track", "motor|track", "carriage|motor",
                                              "curtainslat|curtainslat", "curtainslat|housing", "bottombar|curtainslat", "curtainslat|rail",
                                              // the push-rod runs through the end post (a slot in the real post), an L of tracks crosses at its corner, a motor sits against the rail it drives
                                              "post|rod", "track|track", "motor|rail" };
            var byName = bodies.ToDictionary(b => b.Comp.Name2, b => b.Role);
            for (var s = 0; s < req.States.Count; s++)
            {
                CheckCancel(opt);
                Pose(0, s, 1.0, false);
                asm.ForceRebuild3(false);
                var row = new InterferenceRow { State = req.States[s].Label };
                try
                {
                    var mgr = assy.InterferenceDetectionManager;
                    mgr.TreatCoincidenceAsInterference = false;
                    mgr.TreatSubAssembliesAsComponents = true;
                    mgr.IncludeMultibodyPartInterferences = false;
                    mgr.MakeInterferingPartsTransparent = false;
                    mgr.ShowIgnoredInterferences = false;
                    var found = mgr.GetInterferences() as object[];
                    // a sail's plate variants that are hidden at this state stand where they were built: they are not part of what is shown, so what they touch is no interference
                    var parked = new HashSet<string>(plateGroups.SelectMany(g => g.Variants.Values.Where(v => !ReferenceEquals(v, g.Current)).Select(v => v.Comp.Name2)));
                    var pairs = new Dictionary<string, int>();
                    if (found != null)
                        foreach (Interference itf in found)
                        {
                            if (itf.Volume < 1e-9) continue;
                            var involved = (itf.Components as object[])?.Cast<Component2>().ToList() ?? new List<Component2>();
                            if (involved.Any(c => parked.Contains(c.Name2))) continue;
                            var comps = (itf.Components as object[])?.Cast<Component2>().Select(c => byName.GetValueOrDefault(c.Name2, "?")).OrderBy(x => x, StringComparer.Ordinal).ToArray() ?? Array.Empty<string>();
                            if (comps.Length < 2) continue;
                            var key = comps[0] + "|" + comps[1];
                            if (expected.Contains(key)) continue;
                            pairs[key] = pairs.GetValueOrDefault(key) + 1;
                        }
                    mgr.Done();
                    row.Count = pairs.Values.Sum();
                    row.Pairs = pairs.Select(p => p.Key.Replace("|", " / ") + " x" + p.Value).ToList();
                }
                catch (Exception ex) { report.Notes.Add("The interference check could not run at \"" + row.State + "\": " + ex.Message); }
                report.Interferences.Add(row);
            }
            Pose(0, 0, 0, false);
            asm.ForceRebuild3(false);

            // ---- the assembly and its STEP file, at the first state
            report.AssemblyFile = Path.Combine(outDir, name + "_assembly.SLDASM");
            report.StepFile = Path.Combine(outDir, name + "_assembly.step");
            int e1 = 0, w1 = 0;
            asm.Extension.SaveAs(report.AssemblyFile, (int)swSaveAsVersion_e.swSaveAsCurrentVersion, (int)swSaveAsOptions_e.swSaveAsOptions_Silent, null, ref e1, ref w1);
            e1 = 0; w1 = 0;
            asm.Extension.SaveAs(report.StepFile, (int)swSaveAsVersion_e.swSaveAsCurrentVersion, (int)swSaveAsOptions_e.swSaveAsOptions_Silent, null, ref e1, ref w1);
            if (!File.Exists(report.AssemblyFile)) report.AssemblyFile = "";
            if (!File.Exists(report.StepFile)) report.StepFile = "";

            opt.OutroLines.Add("SOLIDWORKS assembly: " + report.Components + " components from " + report.Parts + " parts, " + report.TotalMassKg.ToString("0.0", CultureInfo.InvariantCulture) + " kg");
            var clash = report.Interferences.Where(r => r.Count > 0).ToList();
            opt.OutroLines.Add(clash.Count == 0 ? "No interference between any two parts in any of the " + report.Interferences.Count + " states" : "Interferences: " + string.Join("; ", clash.Select(r => r.State + " - " + string.Join(", ", r.Pairs))));
            if (report.BladeMassSolidWorksKg > 0) opt.OutroLines.Add("A blade weighs " + report.BladeMassSolidWorksKg.ToString("0.000", CultureInfo.InvariantCulture) + " kg here, " + report.BladeMassModelKg.ToString("0.000", CultureInfo.InvariantCulture) + " kg in the add-in's model");
            opt.OutroLines.Add("States shown: " + string.Join(", ", req.States.Select(x => x.Label)));
            if (opt.NoVideo && !opt.StillsOnly) { report.Seconds = clock.Elapsed.TotalSeconds; return report; }

            // ---- the frames
            var frameDir = Path.Combine(Path.GetTempPath(), "sportify-sw-frames-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(frameDir);
            try
            {
                // stills: one picture per state (the first frame that holds it), to look at the camera and the poses without recording a film
                var detailBodies = DetailBodies(bodies, req);
                var hasDetail = detailBodies.Count > 0;
                bool IsDetail(FrameSpec f) => f.Detail && hasDetail;
                var stills = plan.Select((f, i) => (f, i)).Where(x => x.f.Hold && !x.f.Detail).GroupBy(x => x.f.State).Select(g => g.First().i).ToList();
                if (hasDetail) { var firstDetail = plan.FindIndex(f => f.Detail); if (firstDetail >= 0) stills.Add(firstDetail); }
                var todo = opt.StillsOnly ? stills : Enumerable.Range(0, plan.Count).Where(i => hasDetail || !plan[i].Detail).ToList();
                var closeUp = false;
                void ZoomToMechanism()
                {
                    asm.ClearSelection2(true);
                    var first = true;
                    foreach (var b in detailBodies) { b.Comp.Select4(!first, null, false); first = false; }
                    asm.ViewZoomToSelection();
                    asm.ClearSelection2(true);
                    view?.ZoomByFactor(0.6);
                }
                var done = 0;
                foreach (var fi in todo)
                {
                    CheckCancel(opt);
                    var f = plan[fi];
                    Pose(f.From, f.To, f.T, false);
                    if (IsDetail(f) != closeUp) { closeUp = IsDetail(f); if (closeUp) ZoomToMechanism(); else Camera(); }
                    asm.GraphicsRedraw2();
                    var raw = Path.Combine(frameDir, "f" + fi.ToString("00000") + ".bmp");
                    if (!asm.SaveBMP(raw, capW, capH)) throw new InvalidOperationException("SOLIDWORKS could not save frame " + fi + ".");
                    // kept as a JPEG: a film is hundreds of frames and a raw picture at this size is several megabytes
                    using (var shot = new Bitmap(raw)) SaveJpeg(shot, Path.Combine(frameDir, "f" + fi.ToString("00000") + ".jpg"));
                    File.Delete(raw);
                    done++;
                    if (done % 5 == 0 || done == todo.Count) Progress(28 + 62 * done / todo.Count, "Recorded frame " + done + " of " + todo.Count);
                }
                report.Frames = todo.Count;

                // one framing for the whole film: the union of the model's box over frames spread through it
                var whole = todo.Where(i => !IsDetail(plan[i])).ToList();
                var box = Framing(frameDir, whole.Where((_, k) => k % Math.Max(1, whole.Count / 24) == 0));
                if (opt.StillsOnly)
                {
                    foreach (var fi in todo)
                    {
                        var f0 = plan[fi];
                        using var composed = Compose(Path.Combine(frameDir, "f" + fi.ToString("00000") + ".jpg"), f0, req, opt, IsDetail(f0) ? RectangleF.Empty : box);
                        composed.Save(Path.Combine(outDir, name + "_still_" + (IsDetail(f0) ? "detail" : f0.To.ToString()) + ".png"), ImageFormat.Png);
                    }
                    report.Notes.Add("Stills only: " + todo.Count + " picture(s) in " + outDir);
                }
                else
                {
                    Progress(91, "Encoding the video");
                    report.VideoFile = Path.Combine(outDir, name + "_simulation.mp4");
                    var lastLogged = 0;
                    var order = todo;
                    Mp4Encoder.EncodeAsync(report.VideoFile, opt.Width, opt.Height, opt.Fps, k =>
                    {
                        if (k >= order.Count) return null;
                        var i = order[k];
                        if (k - lastLogged >= 20) { lastLogged = k; Progress(91 + 8 * k / order.Count, "Encoding the video"); }
                        return Overlay(Path.Combine(frameDir, "f" + i.ToString("00000") + ".jpg"), plan[i], req, opt, IsDetail(plan[i]) ? RectangleF.Empty : box);
                    }).GetAwaiter().GetResult();
                    if (!File.Exists(report.VideoFile)) throw new InvalidOperationException("The video was not written.");
                }
            }
            finally { try { Directory.Delete(frameDir, true); } catch (Exception) { /* temp files */ } }
        }
        finally
        {
            try { sw.App.CloseDoc(asm.GetTitle()); } catch (Exception) { }
            foreach (var spec in specs.Values) { try { if (spec.Doc != null) sw.App.CloseDoc(spec.Doc.GetTitle()); } catch (Exception) { } }
        }
        report.Seconds = clock.Elapsed.TotalSeconds;
        return report;
    }

    // ------------------------------------------------------------------ the film

    sealed class FrameSpec { public int From, To; public double T; public bool Hold, Intro, Outro, Detail; public int State; }

    static List<FrameSpec> FramePlan(RenderRequest req, SimulateOptions o)
    {
        var plan = new List<FrameSpec>();
        void Hold(int s, double seconds) { for (var i = 0; i < Math.Max(1, (int)Math.Round(seconds * o.Fps)); i++) plan.Add(new FrameSpec { From = s, To = s, T = 0, Hold = true, State = s }); }
        void Move(int a, int b)
        {
            var n = Math.Max(2, (int)Math.Round(o.MoveS * o.Fps));
            for (var i = 0; i < n; i++) { var x = i / (double)(n - 1); plan.Add(new FrameSpec { From = a, To = b, T = x * x * (3 - 2 * x), State = b }); }
        }
        Hold(0, o.IntroS);
        foreach (var fs in plan) fs.Intro = true;
        for (var s = 1; s < req.States.Count; s++) { Move(s - 1, s); Hold(s, o.HoldS); }
        if (req.States.Count > 1) { Move(req.States.Count - 1, 0); Hold(0, o.HoldS * 0.5); }
        if (o.Detail)
        {
            // the mechanism close up (the caller drops these frames when the unit has no piston to look at): a shorter walk through the same states
            var n = req.States.Count;
            void DHold(int st, double seconds) { for (var i = 0; i < Math.Max(1, (int)Math.Round(seconds * o.Fps)); i++) plan.Add(new FrameSpec { From = st, To = st, T = 0, Hold = true, Detail = true, State = st }); }
            void DMove(int a, int b)
            {
                var k = Math.Max(2, (int)Math.Round(o.MoveS * 0.6 * o.Fps));
                for (var i = 0; i < k; i++) { var x = i / (double)(k - 1); plan.Add(new FrameSpec { From = a, To = b, T = x * x * (3 - 2 * x), Detail = true, State = b }); }
            }
            DHold(0, 0.6);
            for (var st = 1; st < n; st++) { DMove(st - 1, st); DHold(st, 0.5); }
            if (n > 1) DMove(n - 1, 0);
        }
        var before = plan.Count;
        Hold(0, o.OutroS);
        for (var i = before; i < plan.Count; i++) plan[i].Outro = true;
        return plan;
    }

    static List<string> opt0Lines(SimulateOptions o) => o.OutroLines;

    /// <summary>The components the close-up frames: the actuator piston, its housing and the three cranks nearest to it. None when the unit has no piston (a sail, a fence).</summary>
    static List<Body> DetailBodies(List<Body> bodies, RenderRequest req)
    {
        var bars = req.States[0].Bars;
        V3 P(Body b) => V(bars[b.Index].P0);
        var piston = bodies.FirstOrDefault(b => b.Role == "piston");
        if (piston == null) return new List<Body>();
        var list = new List<Body> { piston };
        var housing = bodies.Where(b => b.Role == "housing").OrderBy(b => (P(b) - P(piston)).Length).FirstOrDefault();
        if (housing != null) list.Add(housing);
        list.AddRange(bodies.Where(b => b.Role == "crank").OrderBy(b => (P(b) - P(piston)).Length).Take(3));
        return list;
    }

    static string Banner(RenderState s, string kind)
    {
        var sun = s.SunElevationDeg > 0.5 ? "   SUN " + s.SunElevationDeg.ToString("0") + " DEG HIGH" : "";
        return kind switch
        {
            "overhead" => s.Label.ToUpperInvariant() + sun + "   LOUVRES " + s.OpenDeg.ToString("0") + " DEG OPEN",
            "slats" => s.Label.ToUpperInvariant() + sun + "   SLATS TIPPED " + s.OpenDeg.ToString("0") + " DEG",
            "fins" => s.Label.ToUpperInvariant() + sun + "   FINS TURNED " + s.OpenDeg.ToString("0") + " DEG",
            "sail" => s.Label.ToUpperInvariant() + sun + "   MASTS RUN TO " + (s.OpenDeg * 100).ToString("0") + "% OF THE ANALYSED SIZE",
            _ => "FENCE " + s.Label.ToUpperInvariant(),
        };
    }

    /// <summary>A frame as BGRA bytes with the state written on it: a title at the top, the state along the bottom, and the mechanics over the first seconds.</summary>
    static byte[] Overlay(string bmpFile, FrameSpec f, RenderRequest req, SimulateOptions o, RectangleF box)
    {
        using var canvas = Compose(bmpFile, f, req, o, box);
        var data = canvas.LockBits(new Rectangle(0, 0, o.Width, o.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var bytes = new byte[o.Width * o.Height * 4];
            for (var y = 0; y < o.Height; y++) System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + y * data.Stride, bytes, y * o.Width * 4, o.Width * 4);
            return bytes;
        }
        finally { canvas.UnlockBits(data); }
    }

    static void SaveJpeg(Bitmap bmp, string path)
    {
        var codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        using var quality = new EncoderParameters(1);
        quality.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 92L);
        bmp.Save(path, codec, quality);
    }

    /// <summary>The box round the model in a picture: where the pixels change sharply from one to the next (the lines and edges of the parts; the background is a smooth gradient).</summary>
    static RectangleF ContentBox(string bmpFile)
    {
        using var bmp = new Bitmap(bmpFile);
        int w = bmp.Width, h = bmp.Height;
        var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        var bytes = new byte[Math.Abs(data.Stride) * h];
        System.Runtime.InteropServices.Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
        var stride = data.Stride;
        bmp.UnlockBits(data);
        int Lum(int x, int y) { var o = y * stride + x * 3; return (bytes[o] * 29 + bytes[o + 1] * 150 + bytes[o + 2] * 77) >> 8; }     // B, G, R
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        for (var y = 8; y < h - 14; y += 2)
            for (var x = 8; x < w - 14; x += 2)
            {
                var l = Lum(x, y);
                if (Math.Abs(l - Lum(x + 5, y)) + Math.Abs(l - Lum(x, y + 5)) <= 40) continue;
                if (x < minX) minX = x; if (x > maxX) maxX = x; if (y < minY) minY = y; if (y > maxY) maxY = y;
            }
        return maxX < 0 ? RectangleF.Empty : new RectangleF(minX, minY, maxX - minX + 5, maxY - minY + 5);
    }

    /// <summary>The union of the model's box over frames spread through the film (and the box's own margin), so one framing serves every frame.</summary>
    static RectangleF Framing(string frameDir, IEnumerable<int> indices)
    {
        RectangleF? all = null;
        foreach (var i in indices)
        {
            var file = Path.Combine(frameDir, "f" + i.ToString("00000") + ".jpg");
            if (!File.Exists(file)) continue;
            var b = ContentBox(file);
            if (b.IsEmpty) continue;
            all = all == null ? b : RectangleF.Union(all.Value, b);
        }
        return all ?? RectangleF.Empty;
    }

    /// <summary>The largest font, from `size` down to `min`, in which the text still fits the width.</summary>
    static Font FitFont(Graphics g, string text, string family, float size, float maxWidth, float min)
    {
        var fs = size;
        for (; fs > min; fs -= 0.5f) { using var probe = new Font(family, fs); if (g.MeasureString(text, probe).Width <= maxWidth) break; }
        return new Font(family, Math.Max(fs, min));
    }

    /// <summary>Draws (or, with no brush, only measures) lines of text word-wrapped to the width; returns the height they take.</summary>
    static float WrapBlock(Graphics g, IList<string> lines, Font font, Brush? brush, float x, float y, float width, float gap)
    {
        var total = 0f;
        foreach (var line in lines)
        {
            var h = g.MeasureString(line, font, (int)width).Height;
            if (brush != null) g.DrawString(line, font, brush, new RectangleF(x, y + total, width, h + 2));
            total += h + gap;
        }
        return total;
    }

    static Bitmap Compose(string bmpFile, FrameSpec f, RenderRequest req, SimulateOptions o, RectangleF box)
    {
        using var raw = new Bitmap(bmpFile);
        var canvas = new Bitmap(o.Width, o.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(canvas))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            // the picture is the window as SOLIDWORKS drew it; the model's own box (found once over the whole film, so the camera never wobbles) is centred in the film and scaled to fill it
            var corner = raw.GetPixel(3, 3);
            g.Clear(corner);
            double scale = Math.Max(o.Width / (double)raw.Width, o.Height / (double)raw.Height);
            double cx = raw.Width / 2.0, cy = raw.Height / 2.0;
            if (box.Width > 20 && box.Height > 20)
            {
                scale = Math.Min(0.82 * o.Width / box.Width, 0.68 * o.Height / box.Height);
                scale = Math.Max(0.6, Math.Min(1.7, scale));
                cx = box.X + box.Width / 2; cy = box.Y + box.Height / 2;
            }
            // the picture must cover the whole film: never smaller than the cover scale, and pushed back over the edge it would otherwise leave bare
            scale = Math.Max(scale, Math.Max(o.Width / (double)raw.Width, o.Height / (double)raw.Height));
            var ox = Math.Min(0.0, Math.Max(o.Width - raw.Width * scale, o.Width / 2.0 - cx * scale));
            var oy = Math.Min(0.0, Math.Max(o.Height - raw.Height * scale, o.Height * 0.52 - cy * scale));
            g.DrawImage(raw, (float)ox, (float)oy, (float)(raw.Width * scale), (float)(raw.Height * scale));
            var margin = o.Height / 40f;
            var textWidth = o.Width - 2 * margin;
            using var shade = new SolidBrush(Color.FromArgb(150, 20, 24, 30));
            // title bar and state banner: one line each, the font shrinking until the text fits the film's width
            using var titleFont = FitFont(g, (req.KindLabel.Length > 0 ? req.KindLabel : "Dynamic unit") + " — " + req.PieceName + "   (SOLIDWORKS assembly)", "Segoe UI Semibold", o.Height / 30f, textWidth, o.Height / 60f);
            g.FillRectangle(shade, 0, 0, o.Width, o.Height / 11);
            g.DrawString((req.KindLabel.Length > 0 ? req.KindLabel : "Dynamic unit") + " — " + req.PieceName + "   (SOLIDWORKS assembly)", titleFont, Brushes.White, margin, o.Height / 80f);
            var s = req.States[Math.Min(f.State, req.States.Count - 1)];
            var banner = (f.Detail && !f.Outro ? "MECHANISM CLOSE-UP   " : "") + Banner(s, req.Kind);
            using var bannerFont = FitFont(g, banner, "Segoe UI Semibold", o.Height / 30f, textWidth, o.Height / 60f);
            g.FillRectangle(shade, 0, o.Height - o.Height / 10, o.Width, o.Height / 10);
            g.DrawString(banner, bannerFont, Brushes.White, margin, o.Height - o.Height / 10 + o.Height / 60f);
            using var smallFont = new Font("Segoe UI", o.Height / 46f);
            if (f.Outro && opt0Lines(o).Count > 0)
            {
                var y0 = o.Height / 6f;
                var heading = "What SOLIDWORKS found";
                var lines = opt0Lines(o);
                var body = WrapBlock(g, lines, smallFont, null, margin * 1.5f, 0, textWidth - margin, o.Height / 150f);
                g.FillRectangle(shade, margin, y0 - o.Height / 100f, textWidth, o.Height / 12f + body + o.Height / 40f);
                g.DrawString(heading, titleFont, Brushes.White, margin * 1.5f, y0);
                WrapBlock(g, lines, smallFont, Brushes.White, margin * 1.5f, y0 + o.Height / 12f, textWidth - margin, o.Height / 150f);
            }
            else if (f.Intro && req.MechanicsLines.Count > 0)
            {
                // the mechanics over the first seconds
                var y = o.Height / 8f;
                var lines = req.MechanicsLines.Take(5).ToList();
                var body = WrapBlock(g, lines, smallFont, null, margin * 1.5f, 0, textWidth - margin, o.Height / 150f);
                g.FillRectangle(shade, margin, y - o.Height / 100f, textWidth, body + o.Height / 50f);
                WrapBlock(g, lines, smallFont, Brushes.White, margin * 1.5f, y, textWidth - margin, o.Height / 150f);
            }
        }
        return canvas;
    }

    public static string ToJson(SimulateReport r) => JsonSerializer.Serialize(r, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
}
