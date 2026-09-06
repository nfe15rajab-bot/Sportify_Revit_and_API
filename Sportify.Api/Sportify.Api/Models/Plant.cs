using System.Text.Json.Serialization;

namespace Sportify.Api.Models
{
    public class Plant
    {
        public int Id { get; set; }
        public string CommonName { get; set; } = string.Empty;
        public string ScientificName { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public string SunRequirement { get; set; } = string.Empty;
        public string DroughtTolerance { get; set; } = string.Empty;

        [JsonIgnore] public List<PlantPalette> Palettes { get; set; } = new();
    }
}
