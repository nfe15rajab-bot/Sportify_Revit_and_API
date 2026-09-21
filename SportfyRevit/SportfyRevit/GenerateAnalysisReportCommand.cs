using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace SportfyRevit
{
    /// <summary>
    /// One-click PDF deliverable: pulls together whatever Analyze* commands
    /// have published this session (AnalysisResultDto/RoofBoundaryServer),
    /// the current layout's placements table, and the circulation/axonometric
    /// diagrams (auto-created via GenerateFunctionalDiagramsCommand's own
    /// view logic if they don't already exist, then exported to PNG — no
    /// manual "pick a chart image" step anymore, see AnalysisReportPdfBuilder
    /// for the actual template), then renders it all with QuestPDF and opens
    /// the result. Every input is optional — a fresh session with nothing run
    /// yet still produces a (short) valid PDF rather than failing.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class GenerateAnalysisReportCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            const string title = "Sportify — Generate Analysis Report";
            var doc = commandData.Application.ActiveUIDocument.Document;

            var layout = AnalysisLayoutSource.GetLayout("Generate Analysis Report");

            AnalysisResultPayload? results = null;
            if (RoofBoundaryServer.TryGetLatestAnalysisResults(out var resultsJson) && resultsJson != null)
            {
                try { results = JsonSerializer.Deserialize<AnalysisResultPayload>(resultsJson); }
                catch (Exception) { /* malformed — report without it below */ }
            }

            var (circulationImagePath, axoImagePath) = TryExportDiagramImages(doc);

            string outputPath;
            try
            {
                var reportsDir = SportifyWorkspace.PathFor("reports");
                outputPath = Path.Combine(reportsDir, $"Sportify_Analysis_Report_{DateTime.Now:yyyyMMdd_HHmmss}.pdf");
                AnalysisReportPdfBuilder.Generate(outputPath, layout, results, circulationImagePath, axoImagePath);
            }
            catch (Exception ex)
            {
                message = "Couldn't generate the PDF report: " + ex.Message;
                return Result.Failed;
            }

            try
            {
                Process.Start(new ProcessStartInfo(outputPath) { UseShellExecute = true });
            }
            catch (Exception)
            {
                // Best-effort — the PDF exists on disk either way.
            }

            TaskDialog.Show(title,
                $"Report saved and opened:\n{outputPath}" +
                (results == null
                    ? "\n\nNo Algorithmic Analysis checks have been run yet this session — run Fire Safety/Accessibility/etc. first for real numbers here."
                    : "\n\nIncludes every check that's been run so far this session — run more Analysis commands and regenerate to add them."));

            return Result.Succeeded;
        }

        /// <summary>
        /// Best-effort, never blocks the report: creates the circulation/axo
        /// views (reusing GenerateFunctionalDiagramsCommand's own logic) if a
        /// Sportify layout has actually been imported into this project, then
        /// exports each to a PNG in a per-run temp folder for the PDF to embed.
        /// </summary>
        private static (string? Circulation, string? Axo) TryExportDiagramImages(Document doc)
        {
            // what there is to draw is decided by what the import created, not by whether the project has worksets (the views work either way)
            if (SportifyElementScan.Find(doc).IsEmpty) return (null, null);

            try
            {
                ViewPlan circulationView;
                View3D axoView;
                using (var t = new Transaction(doc, "Sportify report: ensure diagram views"))
                {
                    t.Start();
                    circulationView = GenerateFunctionalDiagramsCommand.CreateOrReuseCirculationView(doc);
                    axoView = GenerateFunctionalDiagramsCommand.CreateOrReuseAxonometricView(doc);
                    t.Commit();
                }

                var tempDir = Path.Combine(Path.GetTempPath(), "Sportify", "ReportImages");
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
                Directory.CreateDirectory(tempDir);

                var circulationPath = ExportViewImage(doc, circulationView.Id, Path.Combine(tempDir, "circulation"));
                var axoPath = ExportViewImage(doc, axoView.Id, Path.Combine(tempDir, "axonometric"));
                return KeepDiagrams(circulationPath, axoPath);
            }
            catch (Exception)
            {
                return (null, null);
            }
        }

        /// <summary>Copies the two diagram images into the workspace's Diagrams folder (timestamped), so they are deliverables and the next report finds them; returns the copies.</summary>
        internal static (string? Circulation, string? Axo) KeepDiagrams(string? circulationPath, string? axoPath)
        {
            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            return (circulationPath == null ? null : SportifyWorkspace.AdoptAs("diagrams", circulationPath, $"circulation_{stamp}.png"),
                    axoPath == null ? null : SportifyWorkspace.AdoptAs("diagrams", axoPath, $"axonometric_{stamp}.png"));
        }

        /// <summary>
        /// Revit's multi-view image export appends its own suffix to the given
        /// base path rather than writing exactly to it (so exporting several
        /// views in one call never collides) — search for whatever it actually
        /// produced instead of assuming an exact filename.
        /// </summary>
        internal static string? ExportViewImage(Document doc, ElementId viewId, string outputBasePath)
        {
            var options = new ImageExportOptions
            {
                FilePath = outputBasePath,
                ExportRange = ExportRange.SetOfViews,
                ZoomType = ZoomFitType.FitToPage,
                PixelSize = 1600,
                ImageResolution = ImageResolution.DPI_150,
                HLRandWFViewsFileType = ImageFileType.PNG,
            };
            options.SetViewsAndSheets(new List<ElementId> { viewId });

            doc.ExportImage(options);

            var dir = Path.GetDirectoryName(outputBasePath)!;
            var baseName = Path.GetFileName(outputBasePath);
            return Directory.GetFiles(dir, baseName + "*.png")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
    }
}
