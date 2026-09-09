using System.Text.Json.Serialization;

namespace Sportify.Api.Models
{
    public class Provider
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string? Country { get; set; }
        public string? Specialty { get; set; }
        public string? Website { get; set; }
        public string? Category { get; set; } // e.g. "Sports flooring & surfaces", "Green roof systems", "Playground equipment", "Prefab hall construction"

        [JsonIgnore] public List<Sport> Sports { get; set; } = new();
        [JsonIgnore] public List<PlantPalette> Palettes { get; set; } = new();
    }
}
