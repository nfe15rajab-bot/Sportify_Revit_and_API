namespace Sportify.Api.Models
{
    /// <summary>
    /// A single quantitative planning rule for a supporting facility
    /// (changing rooms, showers, ventilation, spectator seating) — the
    /// things a roof-sports-hall design needs beyond the playing field
    /// itself. Not sport-specific, so it isn't a child of Sport; Category
    /// groups related rows for display (e.g. all "Changing rooms" rules).
    /// </summary>
    public class FacilityGuideline
    {
        public int Id { get; set; }
        public string Category { get; set; } = string.Empty; // "Changing rooms" | "Showers & sanitary" | "Ventilation" | "Spectator seating — community hall" | "Spectator seating — stadium (FIFA)"
        public string Title { get; set; } = string.Empty;
        public string Requirement { get; set; } = string.Empty; // the actual figure/rule, in plain language
        public string Authority { get; set; } = string.Empty; // "DIN" | "FIFA"
        public string NormCode { get; set; } = string.Empty; // e.g. "DIN 18032-1"
    }
}
