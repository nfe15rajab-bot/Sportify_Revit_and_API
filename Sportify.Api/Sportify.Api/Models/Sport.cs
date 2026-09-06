namespace Sportify.Api.Models
{
    public class Sport
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;

        public List<Norm> Norms { get; set; } = new();
        public List<Material> Materials { get; set; } = new();
        public List<Provider> Providers { get; set; } = new();
    }
}
