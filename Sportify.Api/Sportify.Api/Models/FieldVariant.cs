using System.Text.Json.Serialization;

namespace Sportify.Api.Models
{
    /// <summary>
    /// One size tier (mini/standard/competition) of a sport's playing field,
    /// with the real dimensions the frontend's own field renderer already
    /// draws from (data.js's FIELDS table) — mirrored here so the Data tab
    /// can show the same verified DIN 18032 / federation figures instead of
    /// just a bare norm code.
    /// </summary>
    public class FieldVariant
    {
        public int Id { get; set; }
        public int SportId { get; set; }
        [JsonIgnore] public Sport? Sport { get; set; }

        public string Variant { get; set; } = string.Empty; // "mini" | "standard" | "competition"
        public double LengthM { get; set; }
        public double WidthM { get; set; }
        public double RunoffM { get; set; }
        public double HeightMinM { get; set; }
        public string Norm { get; set; } = string.Empty; // e.g. "FIBA / DIN 18032"
    }
}
