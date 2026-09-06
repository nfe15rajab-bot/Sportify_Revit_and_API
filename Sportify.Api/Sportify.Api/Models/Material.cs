using System.Text.Json.Serialization;

namespace Sportify.Api.Models
{
    public class Material
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;

        [JsonIgnore] public List<Sport> Sports { get; set; } = new();
    }
}
