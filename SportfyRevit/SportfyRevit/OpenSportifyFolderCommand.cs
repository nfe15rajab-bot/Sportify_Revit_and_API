using System.Diagnostics;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace SportfyRevit
{
    /// <summary>
    /// Opens the Sportify folder (default Documents\Sportify Workspace, or the one chosen at install) in Explorer: the layouts, sport and garden data, the charts PDFs of the
    /// physical analyses, the videos, the analysis reports, the schedules and the diagrams, one subfolder each. The web app's Deliverables tab lists the same files.
    /// </summary>
    [Transaction(TransactionMode.ReadOnly)]
    public class OpenSportifyFolderCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                Process.Start(new ProcessStartInfo(SportifyWorkspace.EnsureCreated()) { UseShellExecute = true });
                return Result.Succeeded;
            }
            catch (System.Exception ex)
            {
                TaskDialog.Show("Sportify — Open Sportify folder", "The Sportify folder could not be opened: " + ex.Message);
                return Result.Failed;
            }
        }
    }
}
