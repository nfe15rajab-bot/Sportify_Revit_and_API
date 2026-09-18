using System.Text.Json.Serialization;

namespace Sportify.Api.Models
{
    /// <summary>
    /// One provider's named roof build-up — ZinCo Roof Garden, Bauder
    /// EXTENSIVE Lightweight Sedum. A designer does not compose a green roof
    /// layer by layer: a manufacturer sells a named system with a fixed
    /// build-up, and specifying one means naming that system.
    ///
    /// Two tables rather than one, because six layers cannot live in a row and
    /// their ORDER is part of the specification — vegetation on top,
    /// waterproofing at the bottom, and a drawing that reverses them is wrong.
    ///
    /// Maps one-to-one onto a Revit floor type, which is the same idea Revit
    /// already has: a layered assembly with a material and thickness per layer.
    /// </summary>
    public class RoofAssembly
    {
        public int Id { get; set; }

        /// <summary>Stable key the web app and the Revit exports reference, e.g. "zinco_roof_garden".</summary>
        public string Key { get; set; } = string.Empty;

        public string Provider { get; set; } = string.Empty;
        public string? ProviderCountry { get; set; }
        public string SystemName { get; set; } = string.Empty;

        /// <summary>"extensive", "intensive" or "walkway" — what the system is for.</summary>
        public string Category { get; set; } = string.Empty;

        public string? Description { get; set; }

        // The three figures an engineer actually checks a deck against, and the
        // reason this data is worth having at all. Nullable on purpose: where a
        // manufacturer does not publish one it stays visibly empty rather than
        // carrying a guess, the same rule Material already follows for embodied
        // carbon.
        public double? BuildUpMm { get; set; }
        public double? SaturatedKgM2 { get; set; }
        public double? WaterStorageLM2 { get; set; }

        /// <summary>Where the published figures came from, so any of them can be traced.</summary>
        public string? SourceUrl { get; set; }

        public List<RoofAssemblyLayer> Layers { get; set; } = new();
    }

    /// <summary>
    /// One layer of a build-up. Ordered, because the sequence is the
    /// specification.
    /// </summary>
    public class RoofAssemblyLayer
    {
        public int Id { get; set; }

        public int RoofAssemblyId { get; set; }
        [JsonIgnore] public RoofAssembly? RoofAssembly { get; set; }

        /// <summary>Outermost first — vegetation at 0, waterproofing last.</summary>
        public int LayerOrder { get; set; }

        /// <summary>The manufacturer's own product name, e.g. "Floradrain FD 60 neo".</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>vegetation | substrate | filter | drainage | protection | root_barrier | waterproofing | wearing | bedding</summary>
        public string Function { get; set; } = string.Empty;

        public double ThicknessMm { get; set; }

        /// <summary>
        /// "published" if the manufacturer prints this thickness, "typical" if
        /// it is a normal value used so the build-up resolves to real geometry.
        /// Every layer needs a thickness because geometry needs one; this is how
        /// someone writing a tender knows which numbers came from the supplier.
        /// </summary>
        public string ThicknessSource { get; set; } = "typical";
    }
}
