using System.Text;

namespace SportfyRevit
{
    /// <summary>
    /// The component schedule (CSV, opens in Excel), built from the layout JSON with no Revit call: placements don't carry structured Revit parameters, so this is built from
    /// the layout itself. Shared by the ribbon's Schedules button and the web app's Documents tab (through the add-in's local server), so both give the same file.
    /// </summary>
    internal static class ScheduleCsv
    {
        public const string Header = "Category,Label,QualityLevel,ReferenceMaterial,ReferenceProvider,AreaM2,QualityKey";

        public static string Build(SportifyLayout layout)
        {
            var sb = new StringBuilder();
            sb.AppendLine(Header);
            foreach (var it in layout.Placements ?? new List<PlacementDto>())
            {
                var bb = it.BoundingBox;
                double areaM2 = bb != null ? bb.WidthM * bb.HeightM : 0;
                sb.AppendLine(string.Join(",",
                    Field(it.Category),
                    Field(it.Label),
                    Field(PlacementDataHelpers.GetQualityLevel(it)),
                    Field(PlacementDataHelpers.GetReferenceMaterialName(it)),
                    Field(PlacementDataHelpers.GetReferenceProviderName(it)),
                    areaM2.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
                    Field(it.Parameters?.QualityKey)));
            }
            return sb.ToString();
        }

        public static string Field(string? value)
        {
            value ??= "";
            bool needsQuoting = value.Contains(',') || value.Contains('"') || value.Contains('\n');
            return needsQuoting ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
        }
    }
}
