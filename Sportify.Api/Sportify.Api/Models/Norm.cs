using System.Text.Json.Serialization;

namespace Sportify.Api.Models
{
    /// <summary>
    /// A citation to an external authority — a DIN/federation sports standard
    /// (e.g. "DIN 18032", "FIBA") or a green-roof guideline (e.g. "FLL
    /// Guideline"). Both domains cite standards the same way, so they share
    /// one entity instead of two parallel ones.
    /// </summary>
    public class Norm
    {
        public int Id { get; set; }
        public string Code { get; set; } = string.Empty;
        public string Authority { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;

        [JsonIgnore] public List<Sport> Sports { get; set; } = new();
        [JsonIgnore] public List<PlantPalette> Palettes { get; set; } = new();
    }
}
