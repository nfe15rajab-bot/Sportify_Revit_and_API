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
            AutoImportSync.SetEnabled(!AutoImportSync.IsEnabled);
            AutoImportSync.UpdateButtonLabel();
            return Result.Succeeded;
        }
    }
}
