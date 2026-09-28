using System.IO;
using System.Text.Json;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace SportfyRevit
{
    /// <summary>
    /// Manual, on-demand half of the Combine-to-Revit import: prompts for a JSON file and builds geometry for it through LayoutImporter,
    /// the same import Auto Import runs. Unlike before it REPLACES what the previous import of this project created (ImportLedger) instead of
    /// adding a second copy on top of it, asks before turning worksharing on, and shows which family every piece got or why it is a box.
    /// The same report is in the log (%APPDATA%\Sportify\logs).
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class ImportSportifyLayoutCommand : IExternalCommand
    {
        private const long MaxLayoutBytes = 64L * 1024 * 1024;

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

            SportifyLog.Info("import", "Import Configuration started in " + (string.IsNullOrEmpty(doc.Title) ? "an unsaved project" : doc.Title));
            var jsonPath = LayoutFilePicker.Pick(commandData.Application.MainWindowHandle, "Import Configuration", "Select the Sportify Combine export (sportify_combined_revit.json)");
            if (jsonPath == null)
                return Result.Cancelled;

            string text;
            SportifyLayout? layout;
            try
            {
                if (new FileInfo(jsonPath).Length > MaxLayoutBytes)
                {
                    message = "That file is larger than 64 MB: it is not a Sportify layout export.";
                    return Result.Failed;
                }
                text = File.ReadAllText(jsonPath);
                layout = JsonSerializer.Deserialize<SportifyLayout>(text);
            }
            catch (Exception ex)
            {
                SportifyLog.Error("import", "the file " + jsonPath + " could not be read as a layout", ex);
                message = "Couldn't read that JSON file: " + ex.Message;
                return Result.Failed;
            }

            if (layout?.Placements == null)
            {
                message = "That file doesn't look like a Sportify Combine export (no \"placements\" array).";
                return Result.Failed;
            }

            // Makes this manually-imported layout visible to RoofBoundaryServer's TryGetLatestCombinedLayout the same way a live Combine-tab push
            // would be — so SimulateBallTrajectoriesCommand (and anything else that wants "the current layout") works right after a file-picker
            // import too, not only after a live push from the web app. Filed under whichever roof is currently active (the same one this then
            // imports against below), same as a live combined-layout push always is.
            RoofBoundaryServer.SetCombinedLayoutPayload(text);
            var roofId = (RoofBoundaryServer.ActiveRoofId ?? 0).ToString();

            var clearIterations = IterationLedger.HasAny(doc) && AskClearIterations(doc);
            var outcome = LayoutImporter.Run(doc, layout, ImportSource.Manual, clearIterations, roofId);
            if (outcome.Cancelled) return Result.Cancelled;
            if (!outcome.Succeeded || outcome.Summary == null)
            {
                message = "Import failed: " + outcome.Error + "\n\nThe project is as it was before. The log has the details:\n" + SportifyLog.CurrentFile;
                return Result.Failed;
            }

            var summary = outcome.Summary;
            TaskDialog.Show(
                "Sportify",
                $"Imported {summary.PieceCount} placement(s), roof boundary, setback line, " +
                $"{summary.PathCount} circulation path(s) and {summary.EntryCount} entrance marker(s).\n" +
                (outcome.Replaced > 0 ? $"Replaced {outcome.Replaced} element(s) of the previous import (with the sketches and lines that depend on them).\n" : "") +
                (outcome.ReplacedIterations > 0 ? $"Also cleared {outcome.ReplacedIterations} element(s) of the previously imported iterations.\n" : "") +
                "\nPieces went to the Sports/Gardens worksets when the project has worksets; boundary, setback, circulation and entrances to the Combine workset.\n\n" +
                // Which family every piece got, or why it became a box.
                outcome.Report + "\nLog: " + SportifyLog.CurrentFile);

            return Result.Succeeded;
        }

        /// <summary>Asked only when this project actually has something from "Import Iterations as Design Options" to clear.</summary>
        private static bool AskClearIterations(Document doc)
        {
            var ask = new TaskDialog("Sportify — Import Configuration")
            {
                MainInstruction = "This project also has Design-Option iterations imported earlier",
                MainContent = "\"Import Iterations as Design Options\" built one or more iterations on their own worksets. This import is unrelated to those " +
                               "— clear them out along with it, or leave them alone?",
            };
            ask.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Clear the iterations too", "Removes every iteration's elements and worksets before this import runs.");
            ask.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Leave the iterations alone", "This import runs as usual; the iterations stay untouched.");
            ask.DefaultButton = TaskDialogResult.CommandLink2;       // after the links exist: Revit throws otherwise
            return ask.Show() == TaskDialogResult.CommandLink1;
        }
    }
}
