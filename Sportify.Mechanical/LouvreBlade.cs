using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Sportify.Mechanical;

/// <summary>The blade: an extruded aluminium hollow section, chord x thickness with a wall, as long as the pergola is deep. Metres, kilograms, pascals.</summary>
internal sealed record BladeSpec(double SpanM, double ChordM, double ThicknessM, double WallM, double DensityKgM3, double YoungPa);

/// <summary>What SOLIDWORKS says about the blade it built, and what the closed-form section maths says, side by side.</summary>
internal sealed class BladeReport
{
    public BladeSpec Spec { get; set; } = null!;
    public string PartFile { get; set; } = "";
    public string? StepFile { get; set; }
    public string? SatFile { get; set; }
    public double VolumeM3 { get; set; }
    public double MassKg { get; set; }                    // volume x the inputs' density
    public double SolidWorksMassKg { get; set; }          // as SOLIDWORKS reports it, with the material it holds
    public double SolidWorksDensityKgM3 { get; set; }
    public double[] CentreOfMassM { get; set; } = Array.Empty<double>();
    public double SectionAreaM2 { get; set; }             // from the volume: volume / span
    public double MassPerMetreKg { get; set; }
    public double MomentAboutChordAxisM4 { get; set; }    // the area moment that resists a wind load in the thickness direction, from SOLIDWORKS' mass moments
    public double ExpectedMassKg { get; set; }
    public double ExpectedMomentM4 { get; set; }
    public string MaterialNote { get; set; } = "";
    public List<string> Notes { get; set; } = new();
}

internal static class LouvreBlade
{
    public static BladeReport Build(SolidWorksSession sw, BladeSpec s, string outDir, string name)
    {
        var report = new BladeReport { Spec = s };
        var model = (ModelDoc2)sw.App.NewDocument(sw.Template(assembly: false), 0, 0, 0)
                    ?? throw new InvalidOperationException("SOLIDWORKS could not create a part from its template.");
        try
        {
            // the section, on the first reference plane (found by type, not name: the plane's name follows SOLIDWORKS' language)
            var plane = FirstFeature(model, "RefPlane") ?? throw new InvalidOperationException("The part template has no reference plane.");
            plane.Select2(false, 0);
            var sketches = model.SketchManager;
            sketches.InsertSketch(true);
            double hc = s.ChordM / 2, ht = s.ThicknessM / 2, w = s.WallM;
            sketches.CreateCornerRectangle(-hc, -ht, 0, hc, ht, 0);
            sketches.CreateCornerRectangle(-(hc - w), -(ht - w), 0, hc - w, ht - w, 0);
            sketches.InsertSketch(true);

            var sketch = LastFeature(model, "ProfileFeature") ?? throw new InvalidOperationException("The section sketch was not created.");
            sketch.Select2(false, 0);
            var blade = model.FeatureManager.FeatureExtrusion3(true, false, false, (int)swEndConditions_e.swEndCondBlind, (int)swEndConditions_e.swEndCondBlind,
                s.SpanM, 0, false, false, false, false, 0, 0, false, false, false, false, true, true, true, (int)swStartConditions_e.swStartSketchPlane, 0, false);
            if (blade == null) throw new InvalidOperationException("The extrusion failed (the section sketch may be open or self-intersecting).");
            model.ShowNamedView2("*Isometric", (int)swStandardViews_e.swIsometricView);
            model.ViewZoomtofit2();

            SetMaterial((PartDoc)model, s, report);

            var mass = model.Extension.CreateMassProperty();
            mass.UseSystemUnits = true;
            var volume = mass.Volume;
            report.VolumeM3 = volume;
            report.CentreOfMassM = (double[])mass.CenterOfMass;
            var moments = (double[])mass.GetMomentOfInertia((int)swMassPropertyMoment_e.swMassPropertyMomentAboutCenterOfMass);   // Lxx Lxy Lxz / Lyx Lyy Lyz / Lzx Lzy Lzz, kg m2
            report.SectionAreaM2 = volume / s.SpanM;

            // What SOLIDWORKS reports goes with the material it holds (unit density when none took); geometry is what it is sure of, so the report's mass and
            // moments are the geometry at the INPUTS' density: mass = rho x volume, and the moment scales the same way.
            var swDensity = mass.Mass / volume;
            report.SolidWorksMassKg = mass.Mass;
            report.SolidWorksDensityKgM3 = swDensity;
            report.MassKg = s.DensityKgM3 * volume;
            report.MassPerMetreKg = report.MassKg / s.SpanM;

            // The blade's axis is the sketch plane's normal (z); the chord runs along the plane's x axis. The mass moment about the transverse x axis through the
            // centre of mass is L_xx = rho span I + m span^2 / 12, with I the area moment about the chord axis, the one that resists a load in the thickness
            // direction; so I = (L_xx at unit density - volume span^2 / 12) / span, whatever the density.
            var lxxUnit = moments[0] / swDensity;
            report.MomentAboutChordAxisM4 = (lxxUnit - volume * s.SpanM * s.SpanM / 12.0) / s.SpanM;

            var innerC = s.ChordM - 2 * w; var innerT = s.ThicknessM - 2 * w;
            report.ExpectedMassKg = s.DensityKgM3 * (s.ChordM * s.ThicknessM - innerC * innerT) * s.SpanM;
            report.ExpectedMomentM4 = (s.ChordM * Math.Pow(s.ThicknessM, 3) - innerC * Math.Pow(innerT, 3)) / 12.0;

            Directory.CreateDirectory(outDir);
            report.PartFile = Save(model, Path.Combine(outDir, name + ".SLDPRT"));
            report.StepFile = Save(model, Path.Combine(outDir, name + ".step"));
            report.SatFile = Save(model, Path.Combine(outDir, name + ".sat"));
            return report;
        }
        finally
        {
            sw.App.CloseDoc(model.GetTitle());
        }
    }

    /// <summary>
    /// Aluminium 6063-T5 from SOLIDWORKS' own library on the saved part (2700 kg/m3, 69 GPa: the same alloy the inputs default to). A custom material file
    /// from the inputs was tried and SOLIDWORKS does not take one through the API, so the report never depends on the stored material: it takes the
    /// geometry (volume, centre of mass, inertia at unit density) from SOLIDWORKS and scales it to the inputs' density itself.
    /// </summary>
    static void SetMaterial(PartDoc part, BladeSpec s, BladeReport report)
    {
        var model = (ModelDoc2)part;
        var config = ((Configuration)model.ConfigurationManager.ActiveConfiguration).Name;
        part.SetMaterialPropertyName2(config, "SOLIDWORKS Materials", "6063-T5");
        string db; var name = part.GetMaterialPropertyName2(config, out db);      // the material's name is the return value, its database the out parameter
        var differs = Math.Abs(s.DensityKgM3 - 2700) > 1 || Math.Abs(s.YoungPa - 69e9) > 2e9;
        report.MaterialNote = $"the part holds \"{name}\" from \"{db}\" (2700 kg/m3, 69 GPa)" +
            (differs ? $"; the inputs ask for {s.DensityKgM3:0} kg/m3 and {s.YoungPa / 1e9:0.#} GPa, which this report uses (mass and moments are scaled to them)" : "; the same as the inputs");
    }

    static string Save(ModelDoc2 model, string path)
    {
        int errors = 0, warnings = 0;
        var ok = model.Extension.SaveAs(path, (int)swSaveAsVersion_e.swSaveAsCurrentVersion, (int)swSaveAsOptions_e.swSaveAsOptions_Silent, null, ref errors, ref warnings);
        if (!ok || !File.Exists(path)) throw new InvalidOperationException($"SOLIDWORKS could not save {path} (errors {errors}, warnings {warnings}).");
        return path;
    }

    static Feature? FirstFeature(ModelDoc2 model, string typeName)
    {
        for (var f = (Feature?)model.FirstFeature(); f != null; f = (Feature?)f.GetNextFeature())
            if (f.GetTypeName2() == typeName) return f;
        return null;
    }

    static Feature? LastFeature(ModelDoc2 model, string typeName)
    {
        Feature? last = null;
        for (var f = (Feature?)model.FirstFeature(); f != null; f = (Feature?)f.GetNextFeature())
            if (f.GetTypeName2() == typeName) last = f;
        return last;
    }
}
