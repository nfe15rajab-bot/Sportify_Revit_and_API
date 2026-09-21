namespace SportfyRevit
{
    /// <summary>The one rule about units that needs no Revit, so that Tools/AddinCheck can test it: whether a length unit is imperial.</summary>
    internal static class UnitRules
    {
        /// <summary>
        /// True for Revit's imperial length units (the ids look like "autodesk.unit.unit:feetFractionalInches-1.0.1", "...:inches-1.0.1", "...:feet-1.0.1", "...:fractionalInches-1.0.1"),
        /// false for metric ones (meters, millimeters, centimeters, decimeters) and for nothing at all.
        /// </summary>
        public static bool IsImperialUnitId(string? unitTypeId)
        {
            var id = (unitTypeId ?? "").ToLowerInvariant();
            return id.Contains("feet") || id.Contains("inch") || id.Contains("yard") || id.Contains("mile");
        }
    }
}
