using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using Sportify.Simulation.Sun;

namespace SportfyRevit
{
    /// <summary>
    /// An unattended check of Kinetics inside a real Revit, for the parts nothing outside Revit can prove: authoring the two adaptive families from Revit's adaptive template,
    /// loading them, and putting every kind of unit in a scratch project (made from Revit's own default template, never saved) at the places the plans give, then reading back
    /// where each instance actually stands. SPORTIFY_KINETICS_SELFTEST=&lt;folder&gt; runs it once at startup, writes kinetics-selftest.json (and a picture of the scratch project) there and
    /// exits Revit (SPORTIFY_KINETICS_SELFTEST_KEEP=1 leaves Revit open). Off unless that variable is set.
    /// </summary>
    internal static class KineticsSelfTest
    {
        sealed class Report
        {
            public List<string> Steps { get; set; } = new();
            public List<string> Failures { get; set; } = new();
            public List<UnitReport> Units { get; set; } = new();
            public string? Image { get; set; }
            public bool Ok => Failures.Count == 0;
        }

        sealed class UnitReport
        {
            public string Kind { get; set; } = "";
            public string Host { get; set; } = "";
            public int Parts { get; set; }
            public int Placed { get; set; }
            public int Moving { get; set; }
            public double MaxDeviationMm { get; set; }
            public int PartsOffTarget { get; set; }
            public List<string> Details { get; set; } = new();
            public string State { get; set; } = "";
            public string Summary { get; set; } = "";
        }

        internal static void Install(UIControlledApplication application)
        {
            var dir = Environment.GetEnvironmentVariable("SPORTIFY_KINETICS_SELFTEST");
            if (string.IsNullOrWhiteSpace(dir)) return;
            void OnIdle(object? sender, IdlingEventArgs e)
            {
                application.Idling -= OnIdle;
                if (sender is not UIApplication uiApp) return;
                var report = new Report();
                try { Run(uiApp, dir!, report); }
                catch (Exception ex) { report.Failures.Add("the self-test itself failed: " + ex); }
                try
                {
                    Directory.CreateDirectory(dir!);
                    File.WriteAllText(Path.Combine(dir!, "kinetics-selftest.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
                }
                catch (Exception ex) { SportifyLog.Warn("selftest", "the report could not be written: " + ex.Message); }
                SportifyLog.Info("selftest", "Kinetics self-test " + (report.Ok ? "PASSED" : "FAILED (" + report.Failures.Count + ")") + ", report in " + dir);
                if (Environment.GetEnvironmentVariable("SPORTIFY_KINETICS_SELFTEST_KEEP") != "1")
                {
                    try { uiApp.PostCommand(RevitCommandId.LookupPostableCommandId(PostableCommand.ExitRevit)); }
                    catch (Exception) { Process.GetCurrentProcess().Kill(); }
                }
            }
            application.Idling += OnIdle;
        }

        static string? FindProjectTemplate(Application app)
        {
            var configured = app.DefaultProjectTemplate;
            if (!string.IsNullOrEmpty(configured) && File.Exists(configured)) return configured;
            var root = @"C:\ProgramData\Autodesk\RVT " + app.VersionNumber + @"\Templates";
            if (!Directory.Exists(root)) return null;
            var all = Directory.GetFiles(root, "*.rte", SearchOption.AllDirectories);
            return all.FirstOrDefault(f => Path.GetFileName(f).Equals("Default_M_ENU.rte", StringComparison.OrdinalIgnoreCase))
                   ?? all.FirstOrDefault(f => Path.GetFileName(f).StartsWith("Default_M", StringComparison.OrdinalIgnoreCase)) ?? all.FirstOrDefault();
        }

        /// <summary>The solid geometry an instance really has (its own edges, not its bounding box, which an adaptive instance pads): the box round its vertices and its volume, metres.</summary>
        static (V3 Min, V3 Max, double VolumeM3)? SolidStats(Element el)
        {
            var pts = new List<V3>();
            double volumeFt3 = 0;
            void Take(GeometryElement geom)
            {
                foreach (var go in geom)
                {
                    if (go is GeometryInstance gi) Take(gi.GetInstanceGeometry());
                    else if (go is Face face) { foreach (var p in face.Triangulate().Vertices) pts.Add(KineticsPlan.FromFeet(p)); }
                    else if (go is Mesh mesh) { foreach (var p in mesh.Vertices) pts.Add(KineticsPlan.FromFeet(p)); }
                    else if (go is Solid s && (s.Volume > 1e-9 || s.Faces.Size > 0))
                    {
                        volumeFt3 += s.Volume;
                        foreach (Edge e in s.Edges) foreach (var p in e.Tessellate()) pts.Add(KineticsPlan.FromFeet(p));
                    }
                }
            }
            var g = el.get_Geometry(new Options { DetailLevel = ViewDetailLevel.Fine, ComputeReferences = false });
            if (g == null) return null;
            Take(g);
            if (pts.Count == 0) return null;
            var box = Aabb(pts);
            return (box.Min, box.Max, volumeFt3 * 0.3048 * 0.3048 * 0.3048);
        }

        static string Fmt(V3 v) => "(" + v.X.ToString("0.000") + ", " + v.Y.ToString("0.000") + ", " + v.Z.ToString("0.000") + ")";

        static (V3 Min, V3 Max) Aabb(IEnumerable<V3> pts)
        {
            var list = pts.ToList();
            return (new V3(list.Min(p => p.X), list.Min(p => p.Y), list.Min(p => p.Z)), new V3(list.Max(p => p.X), list.Max(p => p.Y), list.Max(p => p.Z)));
        }

        static void Run(UIApplication uiApp, string dir, Report report)
        {
            var app = uiApp.Application;
            Directory.CreateDirectory(dir);

            // ---- the adaptive template and the two families, authored fresh (a code fix must be what is tested, not last week's cache)
            var sw = Stopwatch.StartNew();
            var template = AdaptiveFamilyBuilder.FindTemplate(app);
            if (template == null) { report.Failures.Add("the adaptive component template was not found: " + AdaptiveFamilyBuilder.FailureReason); return; }
            report.Steps.Add("adaptive template: " + template + " (" + sw.ElapsedMilliseconds + " ms)");
            foreach (var (name, bar) in new[] { (AdaptiveFamilyBuilder.BarName, true), (AdaptiveFamilyBuilder.SurfaceName, false) })
            {
                try { var path = AdaptiveFamilyBuilder.Author(app, name, bar, rebuild: true); report.Steps.Add("authored " + name + " -> " + path + " (" + new FileInfo(path).Length + " bytes)"); }
                catch (Exception ex) { report.Failures.Add("authoring " + name + " failed: " + ex.Message); return; }
            }

            // ---- a scratch project from Revit's own template
            var projectTemplate = FindProjectTemplate(app);
            if (projectTemplate == null) { report.Failures.Add("no Revit project template found for a scratch project"); return; }
            var doc = app.NewProjectDocument(projectTemplate);
            report.Steps.Add("scratch project from " + projectTemplate);
            try
            {
                var design = KineticsInputsFile.LoadDesign();
                var env = new KineticEnvironment { LatitudeDeg = 50, NorthDeg = 0, ShadeTargetPercent = 60, PressurePa = 700, PressureNote = "self-test" };
                SportifyLayoutBuilder.AdoptRoofFrame(new SportifyLayout { RoofContext = new RoofContextDto { LengthM = 30, WidthM = 20 } });
                report.Steps.Add("roof frame: 30 x 20 m at the origin, elevation " + KineticsPlan.RoofZM.ToString("0.##") + " m");

                // ---- hosts of every kind
                var hosts = new List<KineticHost>();
                hosts.AddRange(KineticsHosts.FromPieces(KineticKind.Overhead, new[] { new SunEquipmentDto { Key = "pergola", Name = "Louvre pergola", XM = 10, YM = 5, WidthM = 6, DepthM = 4, HeightM = 2.6, WindUpliftKn = 3 } }));
                // two sails against a small site: a rectangle with a garden along its rails, and a triangle beside another garden; a field beside the first
                var site = new[]
                {
                    new SiteRect { Label = "Green roof A", IsGarden = true, X = 26, Y = 10, W = 4, H = 4 },
                    new SiteRect { Label = "Court 1", IsGarden = false, X = 20, Y = 15, W = 8, H = 4 },
                    new SiteRect { Label = "Green roof B", IsGarden = true, X = 1, Y = 2, W = 3, H = 5 },
                };
                hosts.AddRange(KineticsHosts.FromPieces(KineticKind.Sail, new[] { new SunEquipmentDto { Key = "sail", Name = "Shade sail", XM = 20, YM = 10, WidthM = 5, DepthM = 4, HeightM = 3.5, WindUpliftKn = 2 } }, env, SailShapeChoice.Rectangle, site));
                hosts.AddRange(KineticsHosts.FromPieces(KineticKind.Sail, new[] { new SunEquipmentDto { Key = "sail", Name = "Shade sail", XM = 5, YM = 3, WidthM = 5, DepthM = 4, HeightM = 3.5, WindUpliftKn = 2 } }, env, SailShapeChoice.Triangle, site));
                hosts.AddRange(KineticsHosts.FromFences(new[] { new RoofFenceDto { Edge = "top", FromM = 5, ToM = 25, HeightM = 3, FullHeightM = 4, StopsPercentOfExits = 80 } }, 30, 20));

                var elements = new List<Element>();
                using (var t = new Transaction(doc, "Sportify self-test: a wall and a railing"))
                {
                    t.Start();
                    var level = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation).FirstOrDefault();
                    var wallType = new FilteredElementCollector(doc).OfClass(typeof(WallType)).FirstElementId();
                    if (level != null && wallType != ElementId.InvalidElementId)
                    {
                        var wall = Wall.Create(doc, Line.CreateBound(new XYZ(0, 0, 0), new XYZ(8 / 0.3048, 0, 0)), wallType, level.Id, 3.0 / 0.3048, 0, false, false);
                        elements.Add(wall);
                    }
                    else report.Steps.Add("no level or wall type in the template: no wall host");
                    var railingType = new FilteredElementCollector(doc).OfClass(typeof(RailingType)).FirstElementId();
                    if (level != null && railingType != ElementId.InvalidElementId)
                    {
                        try
                        {
                            var loop = new CurveLoop();
                            loop.Append(Line.CreateBound(new XYZ(0, 5 / 0.3048, 0), new XYZ(6 / 0.3048, 5 / 0.3048, 0)));
                            elements.Add(Railing.Create(doc, loop, railingType, level.Id));
                        }
                        catch (Exception ex) { report.Steps.Add("a railing could not be created in the scratch project (" + ex.Message + "): no railing host"); }
                    }
                    t.Commit();
                }
                if (elements.Count > 0)
                {
                    hosts.AddRange(KineticsHosts.FromElements(elements.Take(1), KineticKind.Slats, out var p1));
                    hosts.AddRange(KineticsHosts.FromElements(elements.Skip(1), KineticKind.Fins, out var p2));
                    if (p1.Length > 0 || p2.Length > 0) report.Steps.Add("screen hosts: " + p1 + " " + p2);
                }
                report.Steps.Add(hosts.Count + " hosts: " + string.Join(", ", hosts.Select(h => h.Key)));

                // ---- the mechanics, then the placement
                var units = hosts.Select(h => KineticsBuild.Build(h, design, env)).ToList();
                report.Steps.Add("mechanics done for " + units.Count + " units");
                try
                {
                    // what the web app's Post Analysis tab is given for these units, kept to look at the tab without Revit
                    var payload = new KineticsResultDto
                    {
                        Priority = "sun", Preliminary = true, PreliminaryNote = "self-test", Pieces = units.Select(u => u.Dto).ToList(), MechanicalInputs = KineticsShared.InputsDto(design),
                        PlacementNotes = new List<string> { "self-test placement notes" },
                    };
                    File.WriteAllText(Path.Combine(dir, "kinetics-payload.json"), JsonSerializer.Serialize(payload));
                }
                catch (Exception ex) { report.Steps.Add("the results payload was not written: " + ex.Message); }
                foreach (var u in units)
                {
                    // the very request the Unity video and the SOLIDWORKS tool are given for this unit, kept for looking at and for developing them without Revit
                    try { File.WriteAllText(Path.Combine(dir, "request_" + u.Host.Key.Split('_')[0] + ".json"), JsonSerializer.Serialize(KineticsRender.For(u, design, env))); }
                    catch (Exception ex) { report.Steps.Add("request for " + u.Host.Key + " not written: " + ex.Message); }
                }

                // ---- the SOLIDWORKS tool through the add-in's own runner (progress window and all), only when asked: it starts SOLIDWORKS and takes a minute or two
                if (Environment.GetEnvironmentVariable("SPORTIFY_KINETICS_SELFTEST_SW") == "1")
                {
                    try
                    {
                        var tool = MechanicalTool.Locate(out var whyNot);
                        if (tool == null) report.Failures.Add("the SOLIDWORKS tool was not found: " + whyNot);
                        else
                        {
                            var sample = units.First(x => x.Host.Kind == KineticKind.Slats);
                            var requestFile = Path.Combine(dir, "sw-request.json");
                            File.WriteAllText(requestFile, JsonSerializer.Serialize(KineticsRender.For(sample, design, env)));
                            var swOut = Path.Combine(dir, "mechanical");
                            var run = MechanicalTool.Run(tool, requestFile, swOut, sample.Host.Key, "Self-test: simulating the slat screen in SOLIDWORKS");
                            report.Steps.Add("SOLIDWORKS run: ok=" + run.Ok + ", video=" + run.VideoPath + ", assembly=" + run.AssemblyPath + (run.Message.Length > 0 ? ", message: " + run.Message : "") + ", " + run.Lines.Count + " lines of progress");
                            if (!run.Ok) report.Failures.Add("the SOLIDWORKS run did not succeed: " + run.Message);
                            else if (run.VideoPath == null || !File.Exists(run.VideoPath)) report.Failures.Add("the SOLIDWORKS run wrote no video");
                            else report.Steps.Add("the film is " + new FileInfo(run.VideoPath).Length + " bytes; the report says " + run.Result.GetProperty("components").GetInt32() + " components, " + run.Result.GetProperty("totalMassKg").GetDouble().ToString("0.0") + " kg");
                        }
                    }
                    catch (Exception ex) { report.Failures.Add("the SOLIDWORKS step failed: " + ex.Message); }
                }

                var placedByUnit = new List<(KineticUnit Unit, UnitPlacement Placed)>();
                using (var t = new Transaction(doc, "Sportify self-test: place the kinetic units"))
                {
                    t.Start();
                    var bar = AdaptiveFamilyBuilder.GetOrLoad(doc, true);
                    var surface = AdaptiveFamilyBuilder.GetOrLoad(doc, false);
                    var ws = SportifyWorksetSet.Ensure(doc, new[] { SportifyWorksetSet.DynamicFurniture, SportifyWorksetSet.Structure });
                    var all = new List<ElementId>();
                    foreach (var u in units)
                    {
                        var placed = AdaptiveUnitPlacer.Place(doc, u.Plan, u.Host.Frame, bar, surface, ws[SportifyWorksetSet.DynamicFurniture], ws[SportifyWorksetSet.Structure], u.Host.Key);
                        placedByUnit.Add((u, placed));
                        all.AddRange(placed.Ids);
                    }
                    var phases = SportifyPhases.Read(doc);
                    report.Steps.Add("phases in the scratch project: " + string.Join(", ", phases.Phases) + (phases.Missing.Count > 0 ? "; missing: " + string.Join(", ", phases.Missing) : "") + (phases.ToRename.Count > 0 ? "; would rename: " + string.Join(", ", phases.ToRename) : ""));
                    var moved = SportifyPhases.Assign(doc, all, SportifyPhases.PostAnalysis, null, out var phaseNote);
                    report.Steps.Add("assigned " + moved + " element(s) to Post analysis" + (phaseNote.Length > 0 ? " (" + phaseNote + ")" : ""));
                    t.Commit();
                }

                // ---- read back where every part stands and compare it with the plan
                foreach (var (u, placed) in placedByUnit)
                {
                    var ur = new UnitReport { Kind = u.Host.Kind.ToString(), Host = u.Host.Key, State = u.StateLabel, Parts = u.Plan.Bars.Count + u.Plan.Surfaces.Count, Placed = placed.Ids.Count, Moving = u.Dto.MovingParts };
                    ur.Summary = u.Louvre?.Spacing?.Reason ?? u.Sail?.Findings.FirstOrDefault() ?? u.Fence?.Findings.FirstOrDefault() ?? "";
                    var expected = new List<(V3 Min, V3 Max)>();
                    foreach (var b in u.Plan.Bars)
                    {
                        var world = new BarPlan { Role = b.Role, P0 = u.Host.Frame.ToWorld(b.P0), P1 = u.Host.Frame.ToWorld(b.P1), SizeU = b.SizeU, SizeV = b.SizeV, U = u.Host.Frame.DirToWorld(b.U), Dynamic = b.Dynamic };
                        expected.Add(Aabb(world.Corners()));
                    }
                    foreach (var s in u.Plan.Surfaces) expected.Add(Aabb(new[] { s.A, s.B, s.C, s.D }.Select(u.Host.Frame.ToWorld)));
                    for (var i = 0; i < placed.Ids.Count && i < expected.Count; i++)
                    {
                        var el = doc.GetElement(placed.Ids[i]);
                        var solid = el == null ? null : SolidStats(el);
                        if (solid == null) { ur.PartsOffTarget++; ur.Details.Add("part " + i + ": no solid geometry"); continue; }
                        var got = (Min: solid.Value.Min, Max: solid.Value.Max);
                        if (i < 2)
                        {
                            var bar = i < u.Plan.Bars.Count ? u.Plan.Bars[i] : null;
                            var bb0 = el!.get_BoundingBox(null);
                            ur.Details.Add("part " + i + ": solid volume " + (solid.Value.VolumeM3 * 1e6).ToString("0") + " cm3" +
                                           (bar != null ? " (a straight bar of its section would be " + (bar.SizeU * bar.SizeV * (bar.P1 - bar.P0).Length * 1e6).ToString("0") + ")" : "") +
                                           "; Revit's bounding box " + (bb0 == null ? "none" : Fmt(KineticsPlan.FromFeet(bb0.Min)) + " .. " + Fmt(KineticsPlan.FromFeet(bb0.Max))));
                        }
                        var dev = new[] { got.Min.X - expected[i].Min.X, got.Min.Y - expected[i].Min.Y, got.Min.Z - expected[i].Min.Z, got.Max.X - expected[i].Max.X, got.Max.Y - expected[i].Max.Y, got.Max.Z - expected[i].Max.Z }.Max(Math.Abs);
                        ur.MaxDeviationMm = Math.Max(ur.MaxDeviationMm, Math.Round(dev * 1000, 1));
                        if (dev > 0.03) ur.PartsOffTarget++;
                        if (i == 0 && el is FamilyInstance fi0)
                        {
                            // where the placement points really are, against the corners the plan gave them: separates "the points were not set" from "the family's geometry is not where its points are"
                            var ptIds = AdaptiveComponentInstanceUtils.GetInstancePlacementPointElementRefIds(fi0);
                            var intended = i < u.Plan.Bars.Count
                                ? new BarPlan { Role = u.Plan.Bars[i].Role, P0 = u.Host.Frame.ToWorld(u.Plan.Bars[i].P0), P1 = u.Host.Frame.ToWorld(u.Plan.Bars[i].P1), SizeU = u.Plan.Bars[i].SizeU, SizeV = u.Plan.Bars[i].SizeV, U = u.Host.Frame.DirToWorld(u.Plan.Bars[i].U) }.Corners()
                                : Array.Empty<V3>();
                            var worst = 0.0;
                            for (var k = 0; k < ptIds.Count && k < intended.Length; k++)
                                if (doc.GetElement(ptIds[k]) is ReferencePoint rp) { var q = KineticsPlan.FromFeet(rp.Position); worst = Math.Max(worst, (q - intended[k]).Length); }
                            ur.Details.Add("part 0: " + ptIds.Count + " placement points, the furthest " + (worst * 1000).ToString("0.0") + " mm from the intended corner");
                        }
                        if (i < 3 || (dev > 0.03 && dev * 1000 >= ur.MaxDeviationMm))
                            ur.Details.Add("part " + i + " " + (i < u.Plan.Bars.Count ? u.Plan.Bars[i].Role : "surface") + ": plan " + Fmt(expected[i].Min) + " .. " + Fmt(expected[i].Max) + "; Revit " + Fmt(got.Min) + " .. " + Fmt(got.Max));
                    }
                    report.Units.Add(ur);
                    if (ur.Placed != ur.Parts) report.Failures.Add(ur.Host + ": " + ur.Placed + " placed of " + ur.Parts + " planned");
                    if (ur.PartsOffTarget > 0) report.Failures.Add(ur.Host + ": " + ur.PartsOffTarget + " of " + ur.Parts + " parts are more than 30 mm from where the plan puts them (worst " + ur.MaxDeviationMm + " mm)");
                }

                // ---- a picture of it, when the scratch project can render one
                try
                {
                    using var t = new Transaction(doc, "Sportify self-test: a 3D view");
                    t.Start();
                    var type3d = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().First(v => v.ViewFamily == ViewFamily.ThreeDimensional);
                    var view = View3D.CreateIsometric(doc, type3d.Id);
                    view.DetailLevel = ViewDetailLevel.Fine;
                    view.DisplayStyle = DisplayStyle.ShadingWithEdges;
                    t.Commit();
                    var image = Path.Combine(dir, "kinetics-selftest");
                    var opts = new ImageExportOptions
                    {
                        ZoomType = ZoomFitType.FitToPage, PixelSize = 1600, ImageResolution = ImageResolution.DPI_150, FitDirection = FitDirectionType.Horizontal,
                        ExportRange = ExportRange.SetOfViews, FilePath = image, HLRandWFViewsFileType = ImageFileType.PNG, ShadowViewsFileType = ImageFileType.PNG,
                    };
                    opts.SetViewsAndSheets(new List<ElementId> { view.Id });
                    doc.ExportImage(opts);
                    report.Image = Directory.GetFiles(dir, "kinetics-selftest*.png").FirstOrDefault();
                    report.Steps.Add("picture: " + (report.Image ?? "none written"));
                }
                catch (Exception ex) { report.Steps.Add("no picture (" + ex.Message + ")"); }
            }
            finally
            {
                try { doc.Close(false); } catch (Exception) { }
            }

            try { RunWorksharedAndPhases(app, projectTemplate, report); }
            catch (Exception ex) { report.Failures.Add("the worksets and phases check failed: " + ex); }
        }

        /// <summary>
        /// A second scratch project, workshared: the phases can be renamed to Sportify's, worksharing can be turned on, the six worksets are made, a pergola is placed on them (its blades on
        /// Dynamic Furniture, its posts on Structure) and its elements can be put into a phase.
        /// </summary>
        static void RunWorksharedAndPhases(Application app, string projectTemplate, Report report)
        {
            var doc = app.NewProjectDocument(projectTemplate);
            try
            {
                var s = SportifyPhases.Read(doc);
                // Revit refuses to rename a phase (and cannot create one): recorded as what it is, not as a failure; Sportify then finds the phases by their order
                using (var t = new Transaction(doc, "Sportify self-test: name the phases"))
                {
                    t.Start();
                    var done = SportifyPhases.Rename(doc, s, out var failures);
                    t.Commit();
                    report.Steps.Add("phase rename through the API: " + (done.Count > 0 ? string.Join("; ", done) : "nothing renamed") + (failures.Count > 0 ? " (refused, as expected: " + failures[0] + ")" : ""));
                }
                var after = SportifyPhases.Read(doc);
                report.Steps.Add("phases now: " + string.Join(", ", after.Phases) + (after.Missing.Count > 0 ? "; missing: " + string.Join(", ", after.Missing) : "") + (after.ToRename.Count > 0 ? "; to rename by hand: " + string.Join(", ", after.ToRename) : ""));
                if (SportifyPhases.For(doc, SportifyPhases.DesignAndAnalysis) == null) report.Failures.Add("no phase stands in for Design and analysis, though the project has a second phase");

                if (!doc.CanEnableWorksharing()) { report.Steps.Add("worksharing cannot be enabled in the scratch project: the workset placement was not tested"); return; }
                doc.EnableWorksharing("Shared Levels and Grids", "Workset1");
                report.Steps.Add("worksharing enabled in the scratch project");

                var design = KineticsInputsFile.LoadDesign();
                var env = new KineticEnvironment { LatitudeDeg = 50, NorthDeg = 0, ShadeTargetPercent = 60, PressurePa = 700, PressureNote = "self-test" };
                SportifyLayoutBuilder.AdoptRoofFrame(new SportifyLayout { RoofContext = new RoofContextDto { LengthM = 30, WidthM = 20 } });
                var host = KineticsHosts.FromPieces(KineticKind.Overhead, new[] { new SunEquipmentDto { Key = "pergola", Name = "Louvre pergola", XM = 10, YM = 5, WidthM = 6, DepthM = 4, HeightM = 2.6, WindUpliftKn = 3 } })[0];
                var unit = KineticsBuild.Build(host, design, env);

                UnitPlacement placed;
                Dictionary<string, WorksetId> ws;
                using (var t = new Transaction(doc, "Sportify self-test: place on worksets"))
                {
                    t.Start();
                    var bar = AdaptiveFamilyBuilder.GetOrLoad(doc, true);
                    var surface = AdaptiveFamilyBuilder.GetOrLoad(doc, false);
                    ws = SportifyWorksetSet.Ensure(doc);
                    placed = AdaptiveUnitPlacer.Place(doc, unit.Plan, unit.Host.Frame, bar, surface, ws[SportifyWorksetSet.DynamicFurniture], ws[SportifyWorksetSet.Structure], unit.Host.Key);
                    var moved = SportifyPhases.Assign(doc, placed.Ids, SportifyPhases.DesignAndAnalysis, null, out var note);
                    report.Steps.Add("phase assign to Design and analysis: " + moved + " element(s)" + (note.Length > 0 ? " (" + note + ")" : ""));
                    t.Commit();
                }
                var names = new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset).Select(w => w.Name).ToList();
                var missing = SportifyWorksetSet.All.Where(n => !names.Contains(n)).ToList();
                report.Steps.Add("worksets in the project: " + string.Join(", ", names));
                if (missing.Count > 0) report.Failures.Add("these worksets were not made: " + string.Join(", ", missing));

                string WorksetOf(ElementId id) => doc.GetWorksetTable().GetWorkset(doc.GetElement(id).WorksetId).Name;
                var dynamicIds = placed.Ids.Where((id, i) => i < unit.Plan.Bars.Count && unit.Plan.Bars[i].Dynamic).ToList();
                var frameIds = placed.Ids.Where((id, i) => i < unit.Plan.Bars.Count && !unit.Plan.Bars[i].Dynamic).ToList();
                var wrongDynamic = dynamicIds.Count(id => WorksetOf(id) != SportifyWorksetSet.DynamicFurniture);
                var wrongFrame = frameIds.Count(id => WorksetOf(id) != SportifyWorksetSet.Structure);
                report.Steps.Add("workset check: " + (dynamicIds.Count - wrongDynamic) + "/" + dynamicIds.Count + " moving parts on Dynamic Furniture, " + (frameIds.Count - wrongFrame) + "/" + frameIds.Count + " frame parts on Structure");
                if (wrongDynamic > 0 || wrongFrame > 0) report.Failures.Add("parts are on the wrong workset: " + wrongDynamic + " moving, " + wrongFrame + " frame");

                var design1 = SportifyPhases.For(doc, SportifyPhases.DesignAndAnalysis);
                var inPhase = placed.Ids.Count(id => doc.GetElement(id).get_Parameter(BuiltInParameter.PHASE_CREATED)?.AsElementId() == design1?.Id);
                report.Steps.Add("phase check: " + inPhase + "/" + placed.Ids.Count + " elements have \"" + design1?.Name + "\" as Phase Created");
                if (design1 == null || inPhase != placed.Ids.Count) report.Failures.Add("only " + inPhase + " of " + placed.Ids.Count + " elements got the phase");
            }
            finally { try { doc.Close(false); } catch (Exception) { } }
        }
    }
}
