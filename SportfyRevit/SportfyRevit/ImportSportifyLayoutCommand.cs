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

            SportifyLayout? layout;
            try
            {
                var text = File.ReadAllText(jsonPath);
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

            using (var t = new Transaction(doc, "Import Sportify layout"))
            {
                t.Start();
                var summary = SportifyLayoutBuilder.BuildGeometry(doc, layout);
                t.Commit();

                TaskDialog.Show(
                    "Sportify",
                    $"Imported {summary.PieceCount} placement(s), roof boundary, setback line, " +
                    $"{summary.PathCount} circulation path(s) and {summary.EntryCount} entrance marker(s).\n\n" +
                    "Pieces went to the Sports/Gardens worksets; boundary, setback, circulation " +
                    "and entrances went to the Combine workset.");
            }

            return Result.Succeeded;
        }
    }
}
