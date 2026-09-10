using System.Text.Json;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace SportfyRevit
{
    /// <summary>
    /// Builds a text summary from whatever Analyze* commands have actually
    /// published this session (RoofBoundaryServer's analysis-results
    /// state — the same channel a future web-app poll loop would read),
    /// plus an optional chart image hand-off. The image step predates the
    /// text summary (first real prototype of this command, proving "a
    /// chart the web app generates ends up embedded in the Revit
    /// deliverable" end to end) and is now optional rather than required —
    /// Cancel skips it instead of aborting the whole report. No per-
    /// section toggle UI yet (the placeholder's own text mentions one) —
    /// this includes everything published, which is the honest baseline
    /// before building a custom picker dialog.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class GenerateAnalysisReportCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            const string title = "Sportify — Generate Analysis Report";
            var doc = commandData.Application.ActiveUIDocument.Document;

            string? imagePath = TryPickChartImage();

            AnalysisResultPayload? results = null;
            if (RoofBoundaryServer.TryGetLatestAnalysisResults(out var resultsJson) && resultsJson != null)
            {
                try { results = JsonSerializer.Deserialize<AnalysisResultPayload>(resultsJson); }
                catch (Exception) { /* malformed — report without it below */ }
            }

            string reportText = BuildReportText(results);

            using (var t = new Transaction(doc, "Generate Sportify analysis report"))
            {
                t.Start();
                try
                {
                    var textTypeId = new FilteredElementCollector(doc).OfClass(typeof(TextNoteType)).FirstOrDefault()?.Id;
                    if (textTypeId != null)
                        TextNote.Create(doc, doc.ActiveView.Id, new XYZ(-20, 0, 0), reportText, textTypeId);

                    if (imagePath != null)
                    {
                        var options = new ImageTypeOptions(imagePath, false, ImageTypeSource.Import);
                        var imageType = ImageType.Create(doc, options);
                        var placement = new ImagePlacementOptions(XYZ.Zero, BoxPlacement.Center);
                        ImageInstance.Create(doc, doc.ActiveView, imageType.Id, placement);
                    }

                    t.Commit();
                }
                catch (Exception ex)
                {
                    t.RollBack();
                    message = "Couldn't assemble the report: " + ex.Message;
                    return Result.Failed;
                }
            }

            TaskDialog.Show(title,
                "Placed a text summary" + (imagePath != null ? " and chart image" : "") + " on the active view.\n\n" +
                (results == null
                    ? "No Analysis panel checks have been run yet this session — run Fire Safety/Accessibility/etc. first for real numbers here."
                    : "Includes every check that's been run so far this session — run more Analysis commands and regenerate to add them."));

            return Result.Succeeded;
        }

        private static string? TryPickChartImage()
        {
            var fod = new FileOpenDialog("Chart image (*.png)|*.png");
            fod.Title = "Optionally pick a chart exported from the Sportify web app (Cancel to skip)";
            if (fod.Show() != ItemSelectionDialogResult.Confirmed) return null;

            try { return ModelPathUtils.ConvertModelPathToUserVisiblePath(fod.GetSelectedModelPath()); }
            catch (Exception) { return null; }
        }

        private static string BuildReportText(AnalysisResultPayload? results)
        {
            var lines = new List<string> { "Sportify Analysis Report" };

            if (results?.FireSafety is { } fs)
                lines.Add($"Fire Safety: {(fs.WithinLimit ? "within limit" : "OVER LIMIT")} — {fs.MaxDistM:0.0} m (ref {fs.MaxTravelDistanceM:0.#} m), {fs.UnreachableCount} unreachable");
            if (results?.Accessibility is { } ac)
                lines.Add($"Accessibility: width {ac.CurrentWidthM:0.0} m (min {ac.MinWidthM:0.#} m) — {(ac.WidthOk ? "meets" : "below")} minimum, reachable: {(ac.ReachOk ? "yes" : "no")}");
            if (results?.WaterManagement is { } wm)
                lines.Add($"Water Management: {wm.TotalAreaM2:0.#} m², {wm.AvgDepthCm:0} cm depth, ~{wm.RetentionPercent:0}% retention");
            if (results?.Lca is { } lca)
                lines.Add($"LCA: ~{lca.TotalKg:0.#} kg CO2e ({lca.CoveredCount}/{lca.TotalCount} pieces covered)");
            if (results?.LiveLoads is { } ll)
                lines.Add($"Live Loads: {ll.WorstCaseKnPerM2:0.00} kN/m² ({(ll.WithinReference ? "within" : "OVER")} reference {ll.ReferenceKnPerM2:0.#} kN/m²)");
            if (results?.CarbonImpact is { } ci)
                lines.Add($"Carbon Impact: ~{ci.EstimatedDailyWh:0.#} Wh/day over {ci.ActiveSurfaceAreaM2:0.#} m²");
            if (results?.SunAndShading is { } ss)
                lines.Add($"Sun & Shading: location confirmed, configured for {ss.ConfiguredForDateTime}");

            if (lines.Count == 1)
                lines.Add("(No checks run yet this session.)");

            return string.Join("\n", lines);
        }
    }
}
