namespace SportfyRevit
{
    /// <summary>
    /// How a placed family is made to sit on the footprint the web app gave its piece (Revit-free, so Tools/AddinCheck checks it).
    ///
    /// The import puts a family's INSERTION POINT on the piece's centre (FamilyPlacementBuilder.PlaceFamilyInstance). That is right for the families
    /// Sportify builds itself (a padel court, a generated block: centred on their origin), and wrong for a family authored with its origin at a corner or
    /// along one edge, or drawn with its long side along the other axis - the design team's families are drawn however their authors drew them. Such a
    /// piece landed half its size away from where the web plan shows it, or turned across it (found 2026-09-29 in the Goldbeck model: the sports roof's
    /// pieces "not in the right place"). So after placing, the instance is measured: turned a quarter when its long side lies across the piece's long side,
    /// then moved so the middle of its geometry is the middle of the footprint.
    /// </summary>
    internal static class PlacementFit
    {
        /// <summary>A side counts as the long one only when it is this much longer than the other: a nearly square family keeps the turn it was given.</summary>
        internal const double ElongatedRatio = 1.2;

        /// <summary>Closer than this (metres) the geometry is already centred, and nothing is moved.</summary>
        internal const double CentredWithinM = 0.005;

        /// <summary>
        /// Whether the family must be turned a quarter more than the piece is, so its long side lies along the piece's long side.
        /// pieceAlongX / pieceAlongY: the piece's size in its OWN frame (before its rotation on the roof); familyX / familyY: the family's size as placed
        /// with no rotation. False when either of them is roughly square (no long side to line up) or when the long sides already agree.
        /// </summary>
        internal static bool NeedsQuarterTurn(double pieceAlongX, double pieceAlongY, double familyX, double familyY)
        {
            if (pieceAlongX <= 0 || pieceAlongY <= 0 || familyX <= 0 || familyY <= 0) return false;
            bool pieceElongated = Math.Max(pieceAlongX, pieceAlongY) >= ElongatedRatio * Math.Min(pieceAlongX, pieceAlongY);
            bool familyElongated = Math.Max(familyX, familyY) >= ElongatedRatio * Math.Min(familyX, familyY);
            if (!pieceElongated || !familyElongated) return false;
            return (pieceAlongX > pieceAlongY) != (familyX > familyY);
        }

        /// <summary>
        /// The piece's size in its own frame from the exported footprint, which is the size AFTER its rotation on the roof (a 90 or 270 degree piece has
        /// its width and height swapped there).
        /// </summary>
        internal static (double AlongX, double AlongY) OwnSize(double footprintWidthM, double footprintHeightM, double rotationDeg)
        {
            double r = ((rotationDeg % 180) + 180) % 180;
            bool quarter = Math.Abs(r - 90) < 45;
            return quarter ? (footprintHeightM, footprintWidthM) : (footprintWidthM, footprintHeightM);
        }

        /// <summary>
        /// The move (plan metres) that puts the middle of the placed geometry's box on the target point, or null when it is already there. A move longer than
        /// maxShiftM is refused (null): a box that far off is not the piece's geometry being off-centre but something else in the family (a far-away
        /// reference, a huge clearance zone), and the insertion point is the better guess.
        /// </summary>
        internal static (double Dx, double Dy)? Recentre(double targetX, double targetY, double boxMinX, double boxMinY, double boxMaxX, double boxMaxY, double maxShiftM)
        {
            double dx = targetX - (boxMinX + boxMaxX) / 2, dy = targetY - (boxMinY + boxMaxY) / 2;
            double d = Math.Sqrt(dx * dx + dy * dy);
            if (d < CentredWithinM || d > maxShiftM) return null;
            return (dx, dy);
        }
    }
}
