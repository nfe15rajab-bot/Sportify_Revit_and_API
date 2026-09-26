using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace SportfyRevit
{
    /// <summary>
    /// Flips AutoImportSync on/off. Revit's ribbon has no native checkable
    /// toggle-button widget, so this is a plain PushButton whose own label
    /// and tooltip are rewritten on each click to show current state —
    /// the standard way add-ins represent an on/off toggle on the ribbon.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class ToggleAutoImportCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var turningOn = !AutoImportSync.IsEnabled;
            if (turningOn)
            {
                var doc = commandData.Application.ActiveUIDocument?.Document;
                AutoImportSync.ClearIterationsToo = doc != null && !doc.IsFamilyDocument && IterationLedger.HasAny(doc) && AskClearIterations(doc);
            }
            AutoImportSync.SetEnabled(turningOn);
            AutoImportSync.UpdateButtonLabel();
            return Result.Succeeded;
        }

        /// <summary>Asked once, when Auto Import is turned on, only if this project actually has something from "Import Iterations as Design Options" to clear.</summary>
        private static bool AskClearIterations(Document doc)
        {
            var ask = new TaskDialog("Sportify — Auto Import")
            {
                MainInstruction = "This project also has Design-Option iterations imported earlier",
                MainContent = "\"Import Iterations as Design Options\" built one or more iterations on their own worksets. Auto Import is unrelated to those " +
                               "— clear them out on every sync from now on, or leave them alone?",
            };
            ask.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Clear the iterations on every sync",
                "Each auto-import first removes every iteration's elements and worksets, the same way it already replaces its own previous import.");
            ask.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Leave the iterations alone", "Auto Import runs as usual; the iterations stay untouched.");
            ask.DefaultButton = TaskDialogResult.CommandLink2;       // after the links exist: Revit throws otherwise
            return ask.Show() == TaskDialogResult.CommandLink1;
        }
    }
}
