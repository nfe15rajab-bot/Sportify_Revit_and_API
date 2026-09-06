namespace Sportify.Api.Models
{
    public class PlantPalette
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;

        public List<Plant> Plants { get; set; } = new();
        public List<Norm> Norms { get; set; } = new();
    }
}
