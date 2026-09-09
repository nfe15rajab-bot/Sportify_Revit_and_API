namespace Sportify.Api.Models
{
    /// <summary>
    /// One tunable reference figure behind an Analysis-tab check (Fire
    /// Safety's max travel distance, Accessibility's wheelchair circulation
    /// width, Water Management's retention formula coefficients, Wind
    /// Exposure's edge-zone distance) — pulled by the frontend instead of
    /// living as a bare JS constant, so it's visible, cited, and editable
    /// from the Data tab rather than buried in analysisController.js.
    /// </summary>
    public class AnalysisParameter
    {
        public int Id { get; set; }
        public string Category { get; set; } = string.Empty; // "Fire Safety" | "Accessibility" | "Water Management" | "Wind Exposure"
        public string Key { get; set; } = string.Empty; // stable machine key the frontend looks up, e.g. "max_travel_distance_m"
        public string Label { get; set; } = string.Empty;
        public double Value { get; set; }
        public string Unit { get; set; } = string.Empty;
        public string? Description { get; set; }
        public string Authority { get; set; } = string.Empty;
        public string NormCode { get; set; } = string.Empty;
    }
}
