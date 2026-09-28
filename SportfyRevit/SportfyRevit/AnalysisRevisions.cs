using Autodesk.Revit.DB;

namespace SportfyRevit
{
    /// <summary>
    /// Turns "Generate Analysis Report" into a real Revit Revision (Manage tab > Revisions) with a cloud round the
    /// roof on a real sheet — a reviewer paging through the project's sheets and revision schedules sees "an
    /// analysis report was generated" the way any other design change would show up there, instead of only a PDF
    /// nobody working the Revit-native way would otherwise notice landed. One revision per report (not one per
    /// individual Analyze* click, which happens far more often while tuning inputs) — its Description names exactly
    /// which analyses are in that report. The cloud sits on a dedicated "Sportify - Analysis Report" sheet created
    /// the first time this runs (so the sheet's own revision count bumps for real, regardless of whether the
    /// separate "Apply Sportify Template" feature has ever been used to build sheets of its own) — reused, not
    /// recreated, on every later report.
    /// </summary>
    internal static class AnalysisRevisions
    {
        private const string ReportSheetNumber = "SPORT-AN";
        private const string ReportSheetName = "Sportify - Analysis Report";

        private static readonly (string Key, string Title)[] TitleMap =
        {
            ("structural_loads", "Structural loads"), ("dynamic_analysis", "Dynamic analysis"), ("wind_erosion", "Wind and erosion"),
            ("soil_percolation", "Rain and soil percolation"), ("sun_and_shading", "Sun and shade"),
            ("fire_safety", "Fire safety"), ("accessibility", "Accessibility"), ("lca", "Embodied carbon (LCA)"),
            ("carbon_impact", "Carbon impact"), ("ball_trajectory", "Ball trajectory"),
        };

        /// <summary>The nice title of every section `results` actually has, in TitleMap's order.</summary>
        public static IEnumerable<string> TitlesFrom(AnalysisResultPayload? results)
        {
            if (results == null) yield break;
            foreach (var (key, title) in TitleMap) if (results.Has(key)) yield return title;
        }

        /// <summary>Call inside an open transaction. Best-effort: never throws out — a revision is a nicety on top of the PDF, not a requirement of it.</summary>
        public static void Record(Document doc, IEnumerable<string> analysisTitles)
        {
            try
            {
                var found = SportifyElementScan.Find(doc);
                if (found.IsEmpty) return;      // nothing imported yet to point the cloud at

                var view = GenerateFunctionalDiagramsCommand.CreateOrReuseCirculationView(doc);
                var bounds = BoundsIn(view, found.Elements);
                if (bounds == null) return;

                EnsureReportSheet(doc, view);

                var titles = analysisTitles.Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
                var revision = Revision.Create(doc);
                revision.Description = titles.Count > 0 ? "Sportify analysis report: " + string.Join(", ", titles) : "Sportify analysis report";
                revision.RevisionDate = DateTime.Now.ToString("yyyy-MM-dd");
                revision.Visibility = RevisionVisibility.CloudAndTagVisible;

                RevisionCloud.Create(doc, view, revision.Id, RectangleLoop(bounds.Value.Min, bounds.Value.Max, 2.0));
            }
            catch (Exception ex)
            {
                SportifyLog.Warn("revisions", "the report's Revit revision could not be recorded: " + ex.Message);
            }
        }

        private static void EnsureReportSheet(Document doc, View view)
        {
            var sheet = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>().FirstOrDefault(s => s.SheetNumber == ReportSheetNumber);
            if (sheet == null)
            {
                var titleblock = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).OfCategory(BuiltInCategory.OST_TitleBlocks).FirstOrDefault();
                sheet = ViewSheet.Create(doc, titleblock?.Id ?? ElementId.InvalidElementId);
                sheet.SheetNumber = ReportSheetNumber;
                sheet.Name = ReportSheetName;
            }
            var alreadyPlaced = new FilteredElementCollector(doc, sheet.Id).OfClass(typeof(Viewport)).Cast<Viewport>().Any(vp => vp.ViewId == view.Id);
            if (!alreadyPlaced && Viewport.CanAddViewToSheet(doc, sheet.Id, view.Id)) Viewport.Create(doc, sheet.Id, view.Id, new XYZ(1.0, 0.7, 0));
        }

        /// <summary>The combined bounding box of `elements`, as seen in `view` (world XYZ, just culled/scoped the way GenerateFunctionalDiagramsCommand's own label placement already reads it) — null if none of them has one in this view.</summary>
        private static (XYZ Min, XYZ Max)? BoundsIn(View view, IEnumerable<Element> elements)
        {
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
            var any = false;
            foreach (var el in elements)
            {
                BoundingBoxXYZ? bb;
                try { bb = el.get_BoundingBox(view); } catch (Exception) { bb = null; }
                if (bb == null) continue;
                any = true;
                minX = Math.Min(minX, bb.Min.X); minY = Math.Min(minY, bb.Min.Y);
                maxX = Math.Max(maxX, bb.Max.X); maxY = Math.Max(maxY, bb.Max.Y);
                maxZ = Math.Max(maxZ, bb.Max.Z);
            }
            return any ? (new XYZ(minX, minY, 0), new XYZ(maxX, maxY, maxZ)) : null;
        }

        /// <summary>A closed rectangle, padded outward by `padFt`, at the roof's own top elevation — a loose loop round what changed, the way a hand-drawn revision cloud sits round a change rather than tracing it exactly.</summary>
        private static IList<Curve> RectangleLoop(XYZ min, XYZ max, double padFt)
        {
            var z = max.Z;
            var p0 = new XYZ(min.X - padFt, min.Y - padFt, z);
            var p1 = new XYZ(max.X + padFt, min.Y - padFt, z);
            var p2 = new XYZ(max.X + padFt, max.Y + padFt, z);
            var p3 = new XYZ(min.X - padFt, max.Y + padFt, z);
            return new List<Curve> { Line.CreateBound(p0, p1), Line.CreateBound(p1, p2), Line.CreateBound(p2, p3), Line.CreateBound(p3, p0) };
        }
    }
}
