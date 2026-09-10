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

            var fod = new FileSaveDialog("CSV file (*.csv)|*.csv");
            fod.Title = "Save Sportify component schedule";
            if (fod.Show() != ItemSelectionDialogResult.Confirmed)
                return Result.Cancelled;

            string path;
            try
            {
                path = ModelPathUtils.ConvertModelPathToUserVisiblePath(fod.GetSelectedModelPath());
            }
            catch (Exception ex)
            {
                TaskDialog.Show(title, "Couldn't resolve the save location: " + ex.Message);
                return Result.Failed;
            }
            if (!path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)) path += ".csv";

            var sb = new StringBuilder();
            sb.AppendLine("Category,Label,QualityLevel,ReferenceMaterial,ReferenceProvider,AreaM2,QualityKey");
            foreach (var it in items)
            {
                var bb = it.BoundingBox;
                double areaM2 = bb != null ? bb.WidthM * bb.HeightM : 0;

                sb.AppendLine(string.Join(",",
                    CsvField(it.Category),
                    CsvField(it.Label),
                    CsvField(PlacementDataHelpers.GetQualityLevel(it)),
                    CsvField(PlacementDataHelpers.GetReferenceMaterialName(it)),
                    CsvField(PlacementDataHelpers.GetReferenceProviderName(it)),
                    areaM2.ToString("0.00"),
                    CsvField(it.Parameters?.QualityKey)));
            }

            try
            {
                File.WriteAllText(path, sb.ToString());
            }
            catch (Exception ex)
            {
                TaskDialog.Show(title, "Couldn't write the CSV file: " + ex.Message);
                return Result.Failed;
            }

            TaskDialog.Show(title, $"Exported {items.Count} component(s) to:\n{path}");
            return Result.Succeeded;
        }

        private static string CsvField(string? value)
        {
            value ??= "";
            bool needsQuoting = value.Contains(',') || value.Contains('"') || value.Contains('\n');
            return needsQuoting ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
        }
    }
}
