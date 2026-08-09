namespace Sportify.Api.Models
{
    public class SavedConfiguration
    {
        public int Id { get; set; }
        public int UserId { get; set; }
        public string ConfigJson { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}