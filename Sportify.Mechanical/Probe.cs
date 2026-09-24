using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorks.Interop.swmotionstudy;

namespace Sportify.Mechanical;

/// <summary>
/// What SOLIDWORKS does when it is driven from here, found out on a scratch assembly: how a component's transform array is laid out, whether a hidden session can
/// capture pictures, and what a motion study does. Development aid (`probe`), not part of the deliverable.
/// </summary>
internal static class Probe
{
    public static int Run(SolidWorksSession sw, string outDir)
    {
        Directory.CreateDirectory(outDir);
        var bar = BuildBox(sw, outDir, "probe_bar", 0.15, 0.03, 1.0);
        Console.WriteLine("bar part: " + bar);

        // AddComponent5 needs the part open in this session
        int oe = 0, ow = 0;
        sw.App.OpenDoc6(bar, (int)swDocumentTypes_e.swDocPART, (int)swOpenDocOptions_e.swOpenDocOptions_Silent, "", ref oe, ref ow);
        Console.WriteLine("part opened (errors " + oe + ", warnings " + ow + ")");
        var asm = (ModelDoc2)sw.App.NewDocument(sw.Template(true), 0, 0, 0);
        var assy = (AssemblyDoc)asm;
        var comps = new List<Component2>();
        for (var i = 0; i < 3; i++)
        {
            var c = assy.AddComponent5(bar, (int)swAddComponentConfigOptions_e.swAddComponentConfigOptions_CurrentSelectedConfig, "", false, "", i * 0.4, 0, 0);
            comps.Add(c);
            Console.WriteLine("component " + i + ": " + (c == null ? "null" : c.Name2));
        }
        asm.ClearSelection2(true);
        comps[0].Select4(false, null, false);
        assy.UnfixComponent();
        asm.ClearSelection2(true);

        var mu = (MathUtility)sw.App.GetMathUtility();

        // -- the transform array: rotation of 90 degrees about Y as row-major R_y, translation (0.5, 0, 0). A point (0,0,1) goes to (1,0,0) for p' = R p.
        var a = Math.PI / 2; double c0 = Math.Cos(a), s0 = Math.Sin(a);
        double[] t = { c0, 0, s0, 0, 1, 0, -s0, 0, c0, 0.5, 0, 0, 1, 0, 0, 0 };
        comps[1].Transform2 = (MathTransform)mu.CreateTransform(t);
        var box = (double[])comps[1].GetBox(false, false);
        Console.WriteLine("component 1 after R_y(90) + (0.5,0,0): box x " + box[0].ToString("0.000") + ".." + box[3].ToString("0.000") + "  y " + box[1].ToString("0.000") + ".." + box[4].ToString("0.000") + "  z " + box[2].ToString("0.000") + ".." + box[5].ToString("0.000"));
        Console.WriteLine("  (row-major R_y: bar's z 0..1 goes to x 0.5..1.5; transposed: x -0.5..0.5)");
        var back = (double[])((MathTransform)comps[1].Transform2).ArrayData;
        Console.WriteLine("  read back: " + string.Join(", ", back.Select(v => v.ToString("0.###"))));

        // -- a picture from this session, in several ways; the number is how many pixels differ from the corner colour (0 = the model is not in the picture)
        int Content(string file)
        {
            try
            {
                using var bmp = new System.Drawing.Bitmap(file);
                var corner = bmp.GetPixel(2, 2); var n = 0;
                for (var y = 0; y < bmp.Height; y += 2) for (var x = 0; x < bmp.Width; x += 2)
                {
                    var p = bmp.GetPixel(x, y);
                    if (Math.Abs(p.R - corner.R) + Math.Abs(p.G - corner.G) + Math.Abs(p.B - corner.B) > 90) n++;
                }
                return n;
            }
            catch (Exception ex) { return -1; }
        }
        void Shot(string label, Func<string, bool> save, string ext)
        {
            var file = Path.Combine(outDir, "probe_" + label + ext);
            try { var ok = save(file); Console.WriteLine("  " + label + ": " + ok + ", " + (File.Exists(file) ? new FileInfo(file).Length + " bytes, content pixels " + Content(file) : "no file")); }
            catch (Exception ex) { Console.WriteLine("  " + label + " failed: " + ex.Message); }
        }
        Console.WriteLine("pictures:");
        asm.ShowNamedView2("*Isometric", (int)swStandardViews_e.swIsometricView);
        asm.ViewZoomtofit2();
        Shot("A_savebmp", f => asm.SaveBMP(f, 640, 480), ".bmp");
        int ae = 0;
        sw.App.ActivateDoc3(asm.GetTitle(), false, (int)swRebuildOnActivation_e.swDontRebuildActiveDoc, ref ae);
        asm.EditRebuild3();
        asm.GraphicsRedraw2();
        asm.ViewZoomtofit2();
        Shot("B_activated", f => asm.SaveBMP(f, 640, 480), ".bmp");
        Shot("C_saveas_png", f => { int e1 = 0, w1 = 0; return asm.Extension.SaveAs(f, (int)swSaveAsVersion_e.swSaveAsCurrentVersion, (int)swSaveAsOptions_e.swSaveAsOptions_Silent, null, ref e1, ref w1); }, ".png");
        var view = asm.ActiveView as ModelView;
        Console.WriteLine("  active view: " + (view == null ? "none" : "frame " + view.FrameLeft + "," + view.FrameTop + " " + view.FrameWidth + "x" + view.FrameHeight + " state " + view.FrameState));
        Console.WriteLine("  component visibility: " + string.Join(", ", comps.Select(c => c.Visible + "/" + c.IsSuppressed())));

        // -- the motion study
        try
        {
            var mgr = (IMotionStudyManager)asm.Extension.GetMotionStudyManager();
            Console.WriteLine("motion studies: " + mgr.GetMotionStudyCount());
            var names = mgr.GetMotionStudyNames() as object[];
            if (names != null) foreach (var n in names) Console.WriteLine("  " + n);
            var studyName = names != null && names.Length > 0 ? (string)names[0] : null;
            IMotionStudy? study = studyName != null ? mgr.GetMotionStudy(studyName) : null;
            if (study == null) { study = mgr.CreateMotionStudy(); Console.WriteLine("created a motion study: " + (study != null)); }
            if (study == null) throw new InvalidOperationException("no motion study can be made in this session");
            Console.WriteLine("study type " + study.StudyType + ", duration " + study.GetDuration() + " s");
            study.StudyType = (int)swMotionStudyType_e.swMotionStudyTypeAssembly;
            Console.WriteLine("study type now " + study.StudyType);
            study.SetDuration(4);
            Console.WriteLine("duration " + study.GetDuration());

            // move a component at time 2 s (auto key?)
            study.SetTime(2.0);
            var t2 = new double[] { 1, 0, 0, 0, 1, 0, 0, 0, 1, 0.4, 0.3, 0, 1, 0, 0, 0 };
            comps[2].Transform2 = (MathTransform)mu.CreateTransform(t2);
            Console.WriteLine("motion features after moving at t=2: " + study.GetMotionFeaturesCount());

            var avi = mgr.CreateAVIParameter();
            avi.ScreenWidth = 640; avi.ScreenHeight = 480; avi.FramePerSecond = 10; avi.SaveEntireAnimation = true;
            var path = Path.Combine(outDir, "probe_motion.avi");
            var saved = study.SaveToAVI(path, avi);
            Console.WriteLine("SaveToAVI: " + saved + " " + (File.Exists(path) ? new FileInfo(path).Length + " bytes" : "no file"));
        }
        catch (Exception ex) { Console.WriteLine("motion study failed: " + ex); }

        Save(asm, Path.Combine(outDir, "probe_assembly.SLDASM"));
        sw.App.CloseDoc(asm.GetTitle());
        return 0;
    }

    /// <summary>Whether a part's extrusion length can be driven from here (a mast that runs in and out is one part whose length is a dimension).</summary>
    public static int RunLength(SolidWorksSession sw, string outDir)
    {
        outDir = Path.GetFullPath(outDir);
        Directory.CreateDirectory(outDir);
        var model = (ModelDoc2)sw.App.NewDocument(sw.Template(assembly: false), 0, 0, 0);
        Feature? plane = null;
        for (var f = (Feature?)model.FirstFeature(); f != null; f = (Feature?)f.GetNextFeature()) if (f.GetTypeName2() == "RefPlane") { plane = f; break; }
        plane!.Select2(false, 0);
        model.SketchManager.InsertSketch(true);
        model.SketchManager.CreateCircleByRadius(0, 0, 0, 0.07);
        model.SketchManager.InsertSketch(true);
        Feature? sketch = null;
        for (var f = (Feature?)model.FirstFeature(); f != null; f = (Feature?)f.GetNextFeature()) if (f.GetTypeName2() == "ProfileFeature") sketch = f;
        sketch!.Select2(false, 0);
        var feat = model.FeatureManager.FeatureExtrusion3(true, false, false, (int)swEndConditions_e.swEndCondBlind, (int)swEndConditions_e.swEndCondBlind, 1.0, 0, false, false, false, false, 0, 0, false, false, false, false, true, true, true, (int)swStartConditions_e.swStartSketchPlane, 0, false);
        Console.WriteLine("extrusion feature: " + feat?.Name);
        var path = Path.Combine(outDir, "probe_len.SLDPRT");
        Save(model, path);
        var asm = (ModelDoc2)sw.App.NewDocument(sw.Template(true), 0, 0, 0);
        var assy = (AssemblyDoc)asm;
        var comp = assy.AddComponent5(path, (int)swAddComponentConfigOptions_e.swAddComponentConfigOptions_CurrentSelectedConfig, "", false, "", 0, 0, 0);
        Console.WriteLine("component: " + (comp == null ? "null" : comp.Name2));
        string Box() { var b = (double[])comp!.GetBox(false, false); return "x " + b[0].ToString("0.000") + ".." + b[3].ToString("0.000") + "  y " + b[1].ToString("0.000") + ".." + b[4].ToString("0.000") + "  z " + b[2].ToString("0.000") + ".." + b[5].ToString("0.000"); }
        Console.WriteLine("before: " + Box());
        foreach (var length in new[] { 2.0, 0.44, 3.46 })
        {
            var sw0 = System.Diagnostics.Stopwatch.StartNew();
            var dim = (Dimension?)model.Parameter("D1@" + feat!.Name);
            Console.WriteLine("dimension D1@" + feat.Name + ": " + (dim == null ? "not found" : "found, value " + dim.SystemValue));
            if (dim == null) return 1;
            dim.SystemValue = length;
            model.EditRebuild3();
            asm.EditRebuild3();
            Console.WriteLine("length " + length + " -> " + Box() + "   (" + sw0.ElapsedMilliseconds + " ms)");
        }
        return 0;
    }

    static string BuildBox(SolidWorksSession sw, string outDir, string name, double w, double h, double len)
    {
        var model = (ModelDoc2)sw.App.NewDocument(sw.Template(assembly: false), 0, 0, 0);
        Feature? plane = null;
        for (var f = (Feature?)model.FirstFeature(); f != null; f = (Feature?)f.GetNextFeature()) if (f.GetTypeName2() == "RefPlane") { plane = f; break; }
        plane!.Select2(false, 0);
        model.SketchManager.InsertSketch(true);
        model.SketchManager.CreateCornerRectangle(-w / 2, -h / 2, 0, w / 2, h / 2, 0);
        model.SketchManager.InsertSketch(true);
        Feature? sketch = null;
        for (var f = (Feature?)model.FirstFeature(); f != null; f = (Feature?)f.GetNextFeature()) if (f.GetTypeName2() == "ProfileFeature") sketch = f;
        sketch!.Select2(false, 0);
        model.FeatureManager.FeatureExtrusion3(true, false, false, (int)swEndConditions_e.swEndCondBlind, (int)swEndConditions_e.swEndCondBlind, len, 0, false, false, false, false, 0, 0, false, false, false, false, true, true, true, (int)swStartConditions_e.swStartSketchPlane, 0, false);
        var path = Path.Combine(outDir, name + ".SLDPRT");
        Save(model, path);
        sw.App.CloseDoc(model.GetTitle());
        return path;
    }

    static void Save(ModelDoc2 model, string path)
    {
        int errors = 0, warnings = 0;
        model.Extension.SaveAs(path, (int)swSaveAsVersion_e.swSaveAsCurrentVersion, (int)swSaveAsOptions_e.swSaveAsOptions_Silent, null, ref errors, ref warnings);
    }
}
