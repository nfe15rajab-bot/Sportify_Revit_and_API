using System.IO;
using System.Text.Json;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace SportfyRevit
{
    /// <summary>
    /// Manual, on-demand half of the Combine-to-Revit import: prompts for a
    /// JSON file and builds geometry for it via SportifyLayoutBuilder. Kept
    /// independent of AutoImportSync's live toggle — this always adds a
    /// fresh set of elements (never deletes a previous run's geometry),
    /// since a deliberate manual run is expected to be occasional, not a
    /// repeated live-sync loop.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class ImportSportifyLayoutCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var uidoc = commandData.Application.ActiveUIDocument;
            var doc = uidoc.Document;

            if (doc.IsFamilyDocument)
            {
                message = "Import Configuration builds a project layout (worksets, roof boundary, placed " +
                           "components) and only works in a Revit project (.rvt) — not while editing a family " +
                           "(.rfa). Open or switch to a project document, then run this again.";
                return Result.Failed;
            }

            var fod = new FileOpenDialog("Sportify layout JSON (*.json)|*.json");
            fod.Title = "Select the Sportify Combine export (sportify_combined_revit.json)";
            if (fod.Show() != ItemSelectionDialogResult.Confirmed)
                return Result.Cancelled;

            string jsonPath;
            try
            {
                jsonPath = ModelPathUtils.ConvertModelPathToUserVisiblePath(fod.GetSelectedModelPath());
            }
            catch (Exception ex)
            {
                message = "Couldn't resolve the selected file: " + ex.Message;
                return Result.Failed;
            }

            string text;
            SportifyLayout? layout;
            try
            {
                text = File.ReadAllText(jsonPath);
                layout = JsonSerializer.Deserialize<SportifyLayout>(text);
            }
            catch (Exception ex)
            {
                message = "Couldn't read that JSON file: " + ex.Message;
                return Result.Failed;
            }

            if (layout?.Placements == null)
            {
                message = "That file doesn't look like a Sportify Combine export (no \"placements\" array).";
                return Result.Failed;
            }

            // Makes this manually-imported layout visible to RoofBoundaryServer's
            // TryGetLatestCombinedLayout the same way a live Combine-tab push would
            // be — so SimulateBallTrajectoriesCommand (and anything else that wants
            // "the current layout") works right after a file-picker import too, not
            // only after a live push from the web app.
            RoofBoundaryServer.SetCombinedLayoutPayload(text);

            // Must run before the transaction opens — EnableWorksharing (inside this,
            // only on a not-yet-workshared document) throws if called from within one.
            SportifyLayoutBuilder.EnsureWorksharing(doc);

            using (var t = new Transaction(doc, "Import Sportify layout"))
            {
                t.Start();
                ImportDiagnostics.Begin();
                ImportSummary summary;
                try
                {
                    summary = SportifyLayoutBuilder.BuildGeometry(doc, layout);
                }
                catch (Exception ex)
                {
                    t.RollBack();
                    message = "Import failed while building geometry: " + ex.Message;
                    return Result.Failed;
                }

                t.Commit();

                TaskDialog.Show(
                    "Sportify",
                    $"Imported {summary.PieceCount} placement(s), roof boundary, setback line, " +
                    $"{summary.PathCount} circulation path(s) and {summary.EntryCount} entrance marker(s).\n\n" +
                    "Pieces went to the Sports/Gardens worksets; boundary, setback, circulation " +
                    "and entrances went to the Combine workset.\n\n" +
                    // How each piece was actually placed. Without this, an import
                    // that quietly used the wrong family type looks exactly like
                    // one that worked.
                    ImportDiagnostics.Report());
            }

            return Result.Succeeded;
        }
    }
}
