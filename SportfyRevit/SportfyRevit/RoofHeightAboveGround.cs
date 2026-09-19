using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace SportfyRevit
{
    /// <summary>
    /// How high a pushed roof stands above the ground, which the wind analysis needs (EN 1991-1-4 scales the roof's
    /// edge zones with the height). The Revit model knows the roof's elevation but not what "ground" is, so this
    /// picks a ground reference in order of trust and SAYS which one it used:
    ///
    ///   1. the topography under the roof, when the model has one;
    ///   2. the level that is the ground floor, found by its name (EG, Erdgeschoss, Ground, Level 0, Ebene 0 ...);
    ///   3. the level nearest the project's zero, from the levels below the roof.
    ///
    /// Revit-free on purpose (only numbers and names), so the choice can be tested without Revit; the few Revit
    /// calls that feed it live in PushRoofBoundaryCommand.
    /// </summary>
    internal static class RoofHeightAboveGround
    {
        internal sealed record LevelInfo(string Name, double ElevationM);

        internal sealed record Result(double HeightM, double GroundM, string Source);

        // Ground-floor names in the languages a Goldbeck project is likely to use.
        static readonly Regex GroundFloorName = new(
            @"^\s*(eg|e\.g\.|erdgeschoss|erdgeschoß|ground|ground floor|gf|level 0|level 00|ebene 0|ebene 00|ebene eg|niveau 0|rdc|0 ?og|±\s?0([.,]0+)?)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <param name="roofTopZM">Elevation of the roof's top face, in the model's own coordinates (metres).</param>
        /// <param name="topographyZM">Ground under the roof from the model's topography, or null.</param>
        /// <param name="levels">Every level in the project, elevations in the same coordinates as the roof.</param>
        /// <returns>The height and where it came from, or null when no sensible ground could be found.</returns>
        public static Result? Choose(double roofTopZM, double? topographyZM, IReadOnlyCollection<LevelInfo> levels)
        {
            if (topographyZM.HasValue)
                return Make(roofTopZM, topographyZM.Value, "topography under the roof");

            // Only levels below the roof can be the ground (a roof level, or a plant room above it, is not).
            var below = levels.Where(l => l.ElevationM < roofTopZM - 1.0).ToList();
            if (below.Count == 0) return null;

            var named = below.Where(l => GroundFloorName.IsMatch(l.Name ?? "")).OrderBy(l => Math.Abs(l.ElevationM)).FirstOrDefault();
            if (named != null) return Make(roofTopZM, named.ElevationM, $"ground floor level \"{named.Name}\"");

            var nearestZero = below.OrderBy(l => Math.Abs(l.ElevationM)).ThenBy(l => l.ElevationM).First();
            return Make(roofTopZM, nearestZero.ElevationM, $"level \"{nearestZero.Name}\" (nearest the project zero; no ground floor by name)");
        }

        static Result? Make(double roofTopZM, double groundM, string source)
        {
            var height = roofTopZM - groundM;
            // Under half a metre is a podium or a ground-level slab, not a building roof.
            return height < 0.5 ? null : new Result(Math.Round(height, 2), Math.Round(groundM, 3), source);
        }
    }
}
