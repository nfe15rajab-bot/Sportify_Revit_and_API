using System.IO;
using System.Text.Json;
using Autodesk.Revit.DB;

namespace SportfyRevit
{
    /// <summary>
    /// The title block of the submission's sheets (user, 2026-09-30: "can you fix this yourself, keep the e-mails part"), done on the family the project
    /// actually uses: Edit Family from the project, report / change it, load it back over the old one (every sheet follows) and keep a copy of the file.
    /// Steps of SubmissionFix: "titleblock-inspect" writes every text, label and line of the family with its place and size (mm on the sheet) and exports
    /// its view as a PNG; "titleblock" applies SPORTIFY_FIX_TB_EDITS, a JSON list of edits found from that report, so a fix needs no new build:
    ///   { "match": "GEZEICHNET", "size_mm": 2.0, "width_mm": 120, "move_mm": [0, -2] }      a text or label, by what it shows
    ///   { "lines_in_mm": [x0, y0, x1, y1], "move_mm": [0, -6] }                               every line lying wholly inside that box
    /// Revit-API-only; nothing here runs unless SubmissionFix is started with these steps.
    /// </summary>
    internal static class TitleBlockFix
    {
        const double Mm = 1 / 304.8;

        internal sealed class Edit
        {
            public string? Match { get; set; }
            public double? SizeMm { get; set; }
            public double? WidthMm { get; set; }
            public double[]? MoveMm { get; set; }
            public double[]? LinesInMm { get; set; }
        }

        /// <summary>The title block family of the Sportify sheets (S2-01 first), else of any sheet.</summary>
        internal static Family? FamilyOnSheets(Document doc)
        {
            var sheets = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>().OrderBy(s => s.SheetNumber.StartsWith("S2-") ? 0 : 1).ThenBy(s => s.SheetNumber, StringComparer.Ordinal);
            foreach (var s in sheets)
                if (new FilteredElementCollector(doc, s.Id).OfCategory(BuiltInCategory.OST_TitleBlocks).WhereElementIsNotElementType().OfType<FamilyInstance>().FirstOrDefault() is FamilyInstance tb)
                    return tb.Symbol.Family;
            return null;
        }

        internal static void Inspect(Document doc, string? exportFolder, List<string> lines)
        {
            var family = FamilyOnSheets(doc);
            if (family == null) { lines.Add("titleblock: no sheet has a title block"); return; }
            var fam = doc.EditFamily(family);
            try
            {
                lines.Add($"---- title block family \"{family.Name}\" ----");
                SetA0(fam, lines);
                Report(fam, lines);
                if (!string.IsNullOrWhiteSpace(exportFolder)) Export(fam, exportFolder!, "titleblock-before", lines);
            }
            finally { fam.Close(false); }
        }

        internal static void Apply(Document doc, string? editsPath, string? copyFolder, string? exportFolder, List<string> lines)
        {
            if (string.IsNullOrWhiteSpace(editsPath) || !File.Exists(editsPath)) { lines.Add("titleblock: no SPORTIFY_FIX_TB_EDITS file"); return; }
            var edits = JsonSerializer.Deserialize<List<Edit>>(File.ReadAllText(editsPath), new JsonSerializerOptions { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower }) ?? new List<Edit>();
            var family = FamilyOnSheets(doc);
            if (family == null) { lines.Add("titleblock: no sheet has a title block"); return; }
            var fam = doc.EditFamily(family);
            try
            {
                SetA0(fam, lines);
                using (var t = new Transaction(fam, "Sportify: title block"))
                {
                    t.Start();
                    foreach (var e in edits) ApplyOne(fam, e, lines);
                    t.Commit();
                }
                Report(fam, lines);
                if (!string.IsNullOrWhiteSpace(exportFolder)) Export(fam, exportFolder!, "titleblock-after", lines);
                if (!string.IsNullOrWhiteSpace(copyFolder))
                {
                    Directory.CreateDirectory(copyFolder!);
                    var copy = Path.Combine(copyFolder!, family.Name + ".rfa");
                    fam.SaveAs(copy, new SaveAsOptions { OverwriteExistingFile = true, MaximumBackups = 1 });
                    lines.Add("titleblock: a copy saved to " + copy);
                }
                fam.LoadFamily(doc, new Overwrite());
                lines.Add($"titleblock: \"{family.Name}\" loaded back into the project over the old one");
            }
            finally { fam.Close(false); }
        }

        static void ApplyOne(Document fam, Edit e, List<string> lines)
        {
            var move = e.MoveMm is { Length: 2 } m ? new XYZ(m[0] * Mm, m[1] * Mm, 0) : null;
            if (e.LinesInMm is { Length: 4 } box)
            {
                var ids = new FilteredElementCollector(fam).OfClass(typeof(CurveElement)).Cast<CurveElement>()
                    .Where(c => c.GeometryCurve is Curve g && Inside(g.GetEndPoint(0), box) && Inside(g.GetEndPoint(1), box)).Select(c => c.Id).ToList();
                if (move != null && ids.Count > 0) ElementTransformUtils.MoveElements(fam, ids, move);
                lines.Add($"titleblock edit: {ids.Count} line(s) inside [{string.Join(", ", box)}] moved {Fmt(e.MoveMm)}");
                return;
            }
            if (string.IsNullOrWhiteSpace(e.Match)) return;
            var hits = new FilteredElementCollector(fam).OfClass(typeof(TextElement)).Cast<TextElement>()
                .Where(x => (x.Text ?? "").IndexOf(e.Match, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            if (hits.Count == 0) { lines.Add($"titleblock edit: nothing shows \"{e.Match}\""); return; }
            foreach (var x in hits)
            {
                try { EditOne(fam, x, e, move, lines); }
                catch (Exception ex) { lines.Add($"titleblock edit: \"{e.Match}\" on {x.Id.Value} not possible: {ex.Message.Split('
')[0]}"); }
            }
        }

        static void EditOne(Document fam, TextElement x, Edit e, XYZ? move, List<string> lines)
        {
            {
                var was = Describe(fam, x);
                if (e.SizeMm is double size && fam.GetElement(x.GetTypeId()) is ElementType type)
                {
                    var name = $"{type.Name} {size:0.0#} mm";
                    var sized = new FilteredElementCollector(fam).OfClass(type.GetType()).Cast<ElementType>().FirstOrDefault(t => t.Name == name)
                                ?? (ElementType)type.Duplicate(name);
                    sized.get_Parameter(BuiltInParameter.TEXT_SIZE)?.Set(size * Mm);
                    x.ChangeTypeId(sized.Id);
                }
                if (e.WidthMm is double w) x.Width = w * Mm;
                if (move != null) ElementTransformUtils.MoveElement(fam, x.Id, move);
                lines.Add($"titleblock edit: \"{e.Match}\": {was}  ->  {Describe(fam, x)}");
            }
        }

        static bool Inside(XYZ p, double[] b) => p.X >= b[0] * Mm - 1e-6 && p.X <= b[2] * Mm + 1e-6 && p.Y >= b[1] * Mm - 1e-6 && p.Y <= b[3] * Mm + 1e-6;

        /// <summary>The A0 type made current, so what is reported and exported is the sheet size the plan set uses.</summary>
        static void SetA0(Document fam, List<string> lines)
        {
            var a0 = fam.FamilyManager.Types.Cast<FamilyType>().FirstOrDefault(t => t.Name == "A0");
            if (a0 == null) return;
            using var t = new Transaction(fam, "Sportify: A0");
            t.Start();
            fam.FamilyManager.CurrentType = a0;
            t.Commit();
            lines.Add("titleblock: current type A0; types " + string.Join(", ", fam.FamilyManager.Types.Cast<FamilyType>().Select(x => x.Name)));
        }

        static void Report(Document fam, List<string> lines)
        {
            foreach (var g in new FilteredElementCollector(fam).WhereElementIsNotElementType().GroupBy(e => e.GetType().Name + " / " + (e.Category?.Name ?? "-")).OrderBy(g => g.Key))
                lines.Add($"titleblock element kind {g.Key}: {g.Count()}");
            foreach (var x in new FilteredElementCollector(fam).OfClass(typeof(TextElement)).Cast<TextElement>().OrderByDescending(x => x.Coord.Y).ThenBy(x => x.Coord.X))
                lines.Add("titleblock text " + Describe(fam, x));
            var curves = new FilteredElementCollector(fam).OfClass(typeof(CurveElement)).Cast<CurveElement>().Select(c => c.GeometryCurve).OfType<Line>().ToList();
            foreach (var c in curves.OrderByDescending(c => Math.Max(c.GetEndPoint(0).Y, c.GetEndPoint(1).Y)))
            {
                XYZ a = c.GetEndPoint(0), b = c.GetEndPoint(1);
                lines.Add($"titleblock line ({a.X / Mm:0.0}, {a.Y / Mm:0.0}) - ({b.X / Mm:0.0}, {b.Y / Mm:0.0})");
            }
        }

        static string Describe(Document fam, TextElement x)
        {
            var type = fam.GetElement(x.GetTypeId()) as ElementType;
            var size = type?.get_Parameter(BuiltInParameter.TEXT_SIZE)?.AsDouble() / Mm;
            var text = (x.Text ?? "").Replace("\r", " / ").Replace("\n", " / ");
            if (text.Length > 70) text = text.Substring(0, 70) + "...";
            return $"[{x.GetType().Name} {x.Id.Value} {x.Category?.Name}] \"{text}\" at ({x.Coord.X / Mm:0.0}, {x.Coord.Y / Mm:0.0}) width {x.Width / Mm:0.0} mm, type \"{type?.Name}\" {size:0.0#} mm, {x.HorizontalAlignment}";
        }

        static string Fmt(double[]? v) => v == null ? "-" : "[" + string.Join(", ", v) + "] mm";

        static void Export(Document fam, string folder, string name, List<string> lines)
        {
            try
            {
                Directory.CreateDirectory(folder);
                var views = new FilteredElementCollector(fam).OfClass(typeof(View)).Cast<View>().Where(v => !v.IsTemplate && v.CanBePrinted).Select(v => v.Id).ToList();
                var opts = new ImageExportOptions
                {
                    ExportRange = ExportRange.SetOfViews, FilePath = Path.Combine(folder, name), HLRandWFViewsFileType = ImageFileType.PNG, ShadowViewsFileType = ImageFileType.PNG,
                    ImageResolution = ImageResolution.DPI_300, ZoomType = ZoomFitType.FitToPage, PixelSize = 4000,
                };
                opts.SetViewsAndSheets(views);
                fam.ExportImage(opts);
                lines.Add($"titleblock: {views.Count} view(s) exported as {name}*.png");
            }
            catch (Exception ex) { lines.Add("titleblock: export failed: " + ex.Message); }
        }

        sealed class Overwrite : IFamilyLoadOptions
        {
            public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues) { overwriteParameterValues = false; return true; }
            public bool OnSharedFamilyFound(Family sharedFamily, bool familyInUse, out FamilySource source, out bool overwriteParameterValues)
            { source = FamilySource.Family; overwriteParameterValues = false; return true; }
        }
    }
}
