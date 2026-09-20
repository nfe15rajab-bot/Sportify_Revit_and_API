using System.IO;
using System.Text;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace SportfyRevit
{
    /// <summary>
    /// CSV, not a native Revit Schedule view: placements don't carry
    /// structured Revit parameters yet (FamilyPlacementBuilder labels them
    /// with a TextNote, not a Comments/Mark field), so a real
    /// ViewSchedule would have nothing to group/filter by. Building
    /// straight from the synced layout JSON — the same source every other
    /// Analysis command already reads — sidesteps that entirely and needs
    /// no changes to Moamen's placement code. CSV opens fine in Excel and
    /// needs no new NuGet dependency for a real .xlsx writer.
    /// </summary>
    [Transaction(TransactionMode.ReadOnly)]
    public class GenerateSchedulesCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            const string title = "Sportify — Generate Schedules";

            var layout = AnalysisLayoutSource.GetLayout("Generate Schedules");
            if (layout == null) return Result.Cancelled;

            var items = layout.Placements ?? new List<PlacementDto>();
            if (items.Count == 0)
            {
                TaskDialog.Show(title, "Push a sport, activity, or garden piece to Combine and sync it before exporting a schedule.");
                return Result.Succeeded;
            }

            // saved to the Schedules folder of the workspace: no file dialog (the web app's Deliverables tab makes the same file)
            string path;
            try
            {
                path = SportifyWorkspace.UniquePath("schedules", $"Sportify_Schedule_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
                File.WriteAllBytes(path, new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes(ScheduleCsv.Build(layout))).ToArray());
            }
            catch (Exception ex)
            {
                TaskDialog.Show(title, "Couldn't write the CSV file: " + ex.Message);
                return Result.Failed;
            }

            TaskDialog.Show(title, $"Exported {items.Count} component(s) to:\n{path}");
            return Result.Succeeded;
        }
    }
}
