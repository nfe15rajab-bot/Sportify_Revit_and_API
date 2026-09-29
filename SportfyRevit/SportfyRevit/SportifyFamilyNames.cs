using System.IO;

namespace SportfyRevit
{
    /// <summary>
    /// The names of the families Sportify generates (Revit-free, so Tools/AddinCheck checks them), in the convention of the families it builds (Moamen's:
    /// "Sportify - Padel single panoramic ...", and the furniture's size in brackets: "Sportify - HDS Stadtmobiliar B 50022 [180x60x80 cm]").
    /// </summary>
    internal static class SportifyFamilyNames
    {
        /// <summary>
        /// A generated block's family: "Sportify - Locker Room [1000x500 cm]", "Sportify - Badminton Standard Medium [1340x610 cm]", a garden with its
        /// build-up depth "Sportify - Garden Parcel Classic Medium [400x400 cm, d45]". Made from the quality_key (the web app's own id: "ACTIVITY_" + the
        /// activity, or sport + variant + quality), so two quality tiers of one sport never share a family. Until 2026-09-29 it was the bare key
        /// ("ACTIVITY_LOCKER_ROOM__1000x500").
        /// </summary>
        internal static string Generated(string qualityKey, int lengthCm, int widthCm, int? depthCm)
        {
            var words = qualityKey.Split(new[] { '_', ' ' }, StringSplitOptions.RemoveEmptyEntries).ToList();
            if (words.Count > 1 && string.Equals(words[0], "ACTIVITY", StringComparison.OrdinalIgnoreCase)) words.RemoveAt(0);
            var readable = string.Join(" ", words.Select(w => char.ToUpperInvariant(w[0]) + w.Substring(1).ToLowerInvariant()));
            var size = $"{lengthCm}x{widthCm} cm" + (depthCm is int d ? $", d{d}" : "");
            return Sanitize($"Sportify - {readable} [{size}]");
        }

        /// <summary>A family name is also its .rfa's file name in the cache: nothing a file name cannot hold.</summary>
        internal static string Sanitize(string name)
        {
            foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name.Trim();
        }
    }
}
