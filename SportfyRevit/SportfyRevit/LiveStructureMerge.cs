using System.Globalization;
using System.Text.Json.Nodes;

namespace SportfyRevit
{
    /// <summary>
    /// What LiveStructure does with the structure it read from the model (Revit-free, so Tools/AddinCheck checks it): the model wins over the
    /// structure the layout carries; what the designer typed stays (deck capacity, natural frequency: the model does not know them). It is only taken
    /// when the model's roof has the layout's size, so a layout made for another roof never gets this roof's grid. Anything unexpected leaves the layout
    /// as it was: the analysis then says, as before, which grid it used.
    /// </summary>
    internal static class LiveStructureMerge
    {
        /// <summary>How close the model's roof must be to the layout's roof, in metres, to be taken as the same roof.</summary>
        internal const double SameRoofToleranceM = 0.1;

        /// <param name="layoutJson">the layout the analysis was given</param>
        /// <param name="pushedRoofJson">what PushRoofBoundaryCommand.Build made for the active roof ({"roof": {"structure": ...}})</param>
        /// <param name="roofLengthM">that roof's size in its own plan frame</param>
        internal static string Merge(string layoutJson, string pushedRoofJson, double roofLengthM, double roofWidthM, out string note)
        {
            note = "";
            if (JsonNode.Parse(layoutJson) is not JsonObject root) return layoutJson;

            var rc = root["roof_context"] as JsonObject;
            var layoutLength = Num(rc?["length_m"]);
            var layoutWidth = Num(rc?["width_m"]);
            if (layoutLength == null || layoutWidth == null ||
                Math.Abs(layoutLength.Value - roofLengthM) > SameRoofToleranceM || Math.Abs(layoutWidth.Value - roofWidthM) > SameRoofToleranceM)
            {
                note = string.Format(CultureInfo.InvariantCulture,
                    "Structure: the active roof in Revit ({0:0.##} x {1:0.##} m) is not the layout's roof ({2:0.##} x {3:0.##} m), so the structure the layout carries is used. " +
                    "Push the layout's roof to read its own.", roofLengthM, roofWidthM, layoutLength ?? 0, layoutWidth ?? 0);
                return layoutJson;
            }

            var live = (JsonNode.Parse(pushedRoofJson) as JsonObject)?["roof"]?["structure"] as JsonObject;
            if (live == null || (Count(live, "grid_lines") + Count(live, "columns") + Count(live, "beams") + Count(live, "walls")) == 0)
            {
                note = "Structure: the Revit model has no grid lines, columns, beams or walls under this roof" +
                       (root["structure"] is JsonObject ? ", so the structure the layout carries is used." : ", so a regular grid is assumed.");
                return layoutJson;
            }

            var merged = (JsonObject)live.DeepClone();
            merged["source"] = "revit";
            if (root["structure"] is JsonObject old)
            {
                foreach (var key in new[] { "deck_capacity_kn_m2", "natural_frequency_hz" })
                    if (old[key] != null) merged[key] = old[key]!.DeepClone();
            }
            root["structure"] = merged;

            note = $"Structure read from the Revit model just now: {Count(merged, "grid_lines")} grid line(s), {Count(merged, "columns")} column(s), " +
                   $"{Count(merged, "beams")} beam segment(s) and {Count(merged, "walls")} wall(s) under the roof.";
            return root.ToJsonString();
        }

        static double? Num(JsonNode? n)
        {
            try { return n?.GetValue<double>(); } catch { return null; }
        }

        static int Count(JsonObject o, string key) => o[key] is JsonArray a ? a.Count : 0;
    }
}
