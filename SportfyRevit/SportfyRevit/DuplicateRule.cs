namespace SportfyRevit
{
    /// <summary>
    /// Which of a roof's Sportify elements are duplicates (DuplicateCleanup; Revit-free so AddinCheck tests it). Two elements are the same placement
    /// when they are the same kind of piece (Sportify_QualityKey, else Sportify_Category and _Variant), of the same Revit category and type, standing in
    /// the same place (their box's middle, to a few centimetres): a leftover right under a reimported copy. The kind alone is not a placement: two ping
    /// pong tables of one layout share it (found 2026-09-30: the import kept one of the Goldbeck sports roof's two tables and deleted the other, as it
    /// did one of every repeated piece since 2026-09-29). What the import in progress just built is never taken, only earlier copies under it.
    /// </summary>
    internal static class DuplicateRule
    {
        internal readonly record struct Piece(long Id, string Key, bool JustBuilt);

        /// <summary>The key two copies of one placement share. `tolerance` in the same unit as x, y, z.</summary>
        internal static string Key(string? qualityKey, string? category, string? variant, string revitCategory, string typeName,
                                   double x, double y, double z, double tolerance)
        {
            var kind = string.IsNullOrEmpty(qualityKey) ? "loc:" + category + "|" + variant : "qk:" + qualityKey;
            return kind + "|" + revitCategory + "|" + typeName + "|" + Math.Round(x / tolerance) + "|" + Math.Round(y / tolerance) + "|" + Math.Round(z / tolerance);
        }

        /// <summary>The ids to delete: in a group with something just built, every earlier copy; in a group of earlier copies only, all but the newest.</summary>
        internal static List<long> ToDelete(IEnumerable<Piece> pieces)
        {
            var delete = new List<long>();
            foreach (var group in pieces.GroupBy(p => p.Key))
            {
                var list = group.ToList();
                if (list.Count < 2) continue;
                if (list.Any(p => p.JustBuilt)) { delete.AddRange(list.Where(p => !p.JustBuilt).Select(p => p.Id)); continue; }
                var keep = list.Max(p => p.Id);
                delete.AddRange(list.Where(p => p.Id != keep).Select(p => p.Id));
            }
            return delete;
        }
    }
}
