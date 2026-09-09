using System.Text.Json.Serialization;

namespace SportfyRevit
{
    /// <summary>
    /// Placeholder shape for whatever an Analysis/Unity Based Analysis
    /// command eventually posts to RoofBoundaryServer's future
    /// POST /analysis-results endpoint (not built yet — see
    /// RoofBoundaryServer.cs), mirroring SportifyLayoutDto.cs's
    /// one-JsonPropertyName-per-field style. Fields below are a guess at
    /// the minimum shape every result needs (which command, whether it
    /// succeeded, a human-readable summary) — extend per analysis as real
    /// ones get built; nothing here is wired to a real endpoint yet.
    /// </summary>
    internal class AnalysisResultDto
    {
        [JsonPropertyName("command")] public string? Command { get; set; } // e.g. "AnalyzeFireSafety"
        [JsonPropertyName("status")] public string? Status { get; set; }   // e.g. "ok" | "fail" | "not_available"
        [JsonPropertyName("summary")] public string? Summary { get; set; } // short human-readable result
        [JsonPropertyName("computed_at_utc")] public string? ComputedAtUtc { get; set; }
    }
}
