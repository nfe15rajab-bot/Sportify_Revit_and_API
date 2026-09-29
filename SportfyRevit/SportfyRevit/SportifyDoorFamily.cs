using System.IO;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;

namespace SportfyRevit
{
    /// <summary>
    /// The door of the indoor zone's wall (IndoorWallBuilder).
    ///
    /// A door the project already has is used - the practice's own door family is the right one. When it has none (the Goldbeck model came from an IFC:
    /// its doors are shapes, not families), Sportify makes a plain one from Revit's own door template, "Sportify - Door [100x210 cm]": the template's
    /// opening, a leaf in it, the web app's door width (algoPlacementCore.js DOOR_WIDTH_M = 1.0 m) and a 2.1 m height. Built outside the import
    /// transaction like every generated family (FamilyPreparation), cached in SportifyGeneratedFamilies. When neither works the wall keeps its opening
    /// and the import report says why.
    /// </summary>
    internal static class SportifyDoorFamily
    {
        internal const double WidthM = 1.0, HeightM = 2.1, LeafThicknessM = 0.04;
        internal static string FamilyName => $"Sportify - Door [{(int)Math.Round(WidthM * 100)}x{(int)Math.Round(HeightM * 100)} cm]";

        /// <summary>The door to place, or null with the reason. Must run outside any open transaction (it may load a family).</summary>
        internal static FamilySymbol? GetOrCreate(Document doc, out string note)
        {
            note = "";
            try
            {
                var doors = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Doors).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>().ToList();
                var ours = doors.FirstOrDefault(s => string.Equals(s.Family?.Name, FamilyName, StringComparison.OrdinalIgnoreCase));
                var theirs = ours ?? doors.FirstOrDefault(s => s.Family != null && !s.Family.IsInPlace && (s.Family.FamilyPlacementType == FamilyPlacementType.OneLevelBasedHosted));
                if (theirs != null)
                {
                    note = ours != null ? "" : $"the project's own door \"{theirs.Family?.Name}: {theirs.Name}\"";
                    return Activate(doc, theirs);
                }

                var template = FindDoorTemplate();
                if (template == null) { note = "no door family in the project and Revit's door template (Metric Door.rft) was not found: the opening is left open"; return null; }

                var rfa = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Autodesk", "Revit", "Addins", "2025", "SportifyGeneratedFamilies", FamilyName + ".rfa");
                if (!File.Exists(rfa)) Generate(doc.Application, template, rfa);

                Family? family = null;
                using (var t = new Transaction(doc, "Sportify: load " + FamilyName))
                {
                    t.Start();
                    doc.LoadFamily(rfa, new OverwriteFamilyLoadOptions(), out family);
                    t.Commit();
                }
                family ??= new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>().FirstOrDefault(f => f.Name == FamilyName);
                var symbolId = family?.GetFamilySymbolIds().FirstOrDefault();
                if (symbolId == null || doc.GetElement(symbolId) is not FamilySymbol symbol) { note = $"{FamilyName} was made but could not be loaded: the opening is left open"; return null; }
                return Activate(doc, symbol);
            }
            catch (Exception ex)
            {
                note = $"the door could not be made ({ex.Message}): the opening is left open";
                SportifyLog.Warn("walls", note);
                return null;
            }
        }

        static FamilySymbol Activate(Document doc, FamilySymbol s)
        {
            if (s.IsActive) return s;
            using var t = new Transaction(doc, "Sportify: activate the door");
            t.Start();
            s.Activate();
            t.Commit();
            return s;
        }

        /// <summary>Revit's door template in any language it is installed in ("Metric Door.rft" first).</summary>
        static string? FindDoorTemplate()
        {
            foreach (var year in new[] { "2025", "2026", "2024" })
            {
                var root = $@"C:\ProgramData\Autodesk\RVT {year}\Family Templates";
                if (!Directory.Exists(root)) continue;
                var all = Directory.GetFiles(root, "*.rft", SearchOption.AllDirectories)
                    .Where(f => !f.Contains("Annotation", StringComparison.OrdinalIgnoreCase) && !f.Contains("Beschriftung", StringComparison.OrdinalIgnoreCase))
                    .Where(f => { var n = Path.GetFileNameWithoutExtension(f); return (n.Contains("Door", StringComparison.OrdinalIgnoreCase) || n.Contains("Tür", StringComparison.OrdinalIgnoreCase))
                                     && !n.Contains("Curtain", StringComparison.OrdinalIgnoreCase) && !n.Contains("Tag", StringComparison.OrdinalIgnoreCase); })
                    .ToList();
                var metric = all.FirstOrDefault(f => Path.GetFileName(f).Equals("Metric Door.rft", StringComparison.OrdinalIgnoreCase)) ?? all.FirstOrDefault(f => f.Contains("Metric", StringComparison.OrdinalIgnoreCase)) ?? all.FirstOrDefault();
                if (metric != null) return metric;
            }
            return null;
        }

        /// <summary>The template's opening at the web app's size, and a leaf in it: a plain door, centred on the family's origin like the template's own.</summary>
        static void Generate(Application app, string templatePath, string rfaPath)
        {
            var fdoc = app.NewFamilyDocument(templatePath);
            try
            {
                using (var t = new Transaction(fdoc, "Build " + FamilyName))
                {
                    t.Start();
                    var fm = fdoc.FamilyManager;
                    if (fm.CurrentType == null) fm.NewType($"{(int)Math.Round(WidthM * 1000)} x {(int)Math.Round(HeightM * 1000)}");
                    double w = UnitUtils.ConvertToInternalUnits(WidthM, UnitTypeId.Meters), h = UnitUtils.ConvertToInternalUnits(HeightM, UnitTypeId.Meters);
                    var width = fm.get_Parameter(BuiltInParameter.DOOR_WIDTH) ?? fm.get_Parameter(BuiltInParameter.FAMILY_WIDTH_PARAM) ?? fm.get_Parameter("Width");
                    var height = fm.get_Parameter(BuiltInParameter.DOOR_HEIGHT) ?? fm.get_Parameter(BuiltInParameter.FAMILY_HEIGHT_PARAM) ?? fm.get_Parameter("Height");
                    if (width != null && !width.IsDeterminedByFormula) fm.Set(width, w);
                    if (height != null && !height.IsDeterminedByFormula) fm.Set(height, h);

                    // the leaf: a slab in the opening, 5 cm in from the frame, 4 cm thick, across the wall's centre line (the template's wall runs along x)
                    double inset = UnitUtils.ConvertToInternalUnits(0.05, UnitTypeId.Meters), half = UnitUtils.ConvertToInternalUnits(LeafThicknessM / 2, UnitTypeId.Meters);
                    var pts = new[] { new XYZ(-w / 2 + inset, -half, 0), new XYZ(w / 2 - inset, -half, 0), new XYZ(w / 2 - inset, half, 0), new XYZ(-w / 2 + inset, half, 0) };
                    var loop = new CurveArray();
                    for (int i = 0; i < 4; i++) loop.Append(Line.CreateBound(pts[i], pts[(i + 1) % 4]));
                    var profile = new CurveArrArray();
                    profile.Append(loop);
                    var plane = SketchPlane.Create(fdoc, Plane.CreateByNormalAndOrigin(XYZ.BasisZ, XYZ.Zero));
                    fdoc.FamilyCreate.NewExtrusion(true, profile, plane, h - inset);
                    t.Commit();
                }
                Directory.CreateDirectory(Path.GetDirectoryName(rfaPath)!);
                fdoc.SaveAs(rfaPath, new SaveAsOptions { OverwriteExistingFile = true });
            }
            finally
            {
                fdoc.Close(false);
            }
        }

        private sealed class OverwriteFamilyLoadOptions : IFamilyLoadOptions
        {
            public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues)
            {
                overwriteParameterValues = true;
                return true;
            }

            public bool OnSharedFamilyFound(Family sharedFamily, bool familyInUse, out FamilySource source, out bool overwriteParameterValues)
            {
                source = FamilySource.Family;
                overwriteParameterValues = true;
                return true;
            }
        }
    }
}
