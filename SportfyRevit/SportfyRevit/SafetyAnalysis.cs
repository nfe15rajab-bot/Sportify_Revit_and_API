namespace SportfyRevit
{
    internal sealed class FireSafetyOutcome
    {
        /// <summary>"empty" (no pieces), "no-entries", "fail" (some piece has no walkable route), "ok".</summary>
        public string Status { get; init; } = "ok";
        public double MaxDistM { get; init; }
        public double MaxTravelDistanceM { get; init; }
        public bool WithinLimit { get; init; }
        public int UnreachableCount { get; init; }
    }

    internal sealed class AccessibilityOutcome
    {
        public string Status { get; init; } = "ok";
        public bool WidthOk { get; init; }
        public bool ReachOk { get; init; }
        public double CurrentWidthM { get; init; }
        public double MinWidthM { get; init; }
    }

    /// <summary>
    /// The decisions behind the Fire Safety and Accessibility analyses, without the dialogs: longest walk to an entry against the reference distance, circulation width against the
    /// reference width, every piece reachable. The commands call this; Tools/AnalysisParity runs it on every fixture layout against the web app's analyzeFireSafety() and
    /// analyzeAccessibility() (analysisController.js), which are the same decisions on the same circulation engine (CirculationCheck compares that).
    /// </summary>
    internal static class SafetyAnalysis
    {
        public static FireSafetyOutcome Fire(SportifyLayout layout, double maxTravelDistanceM)
        {
            if (layout.Placements == null || layout.Placements.Count == 0) return new FireSafetyOutcome { Status = "empty", MaxTravelDistanceM = maxTravelDistanceM };
            if (layout.EntryPoints == null || layout.EntryPoints.Count == 0) return new FireSafetyOutcome { Status = "no-entries", MaxTravelDistanceM = maxTravelDistanceM };

            var (distancesM, unreachable) = CirculationEngine.ComputeTravelDistances(layout);
            if (unreachable.Count > 0)
                return new FireSafetyOutcome { Status = "fail", MaxTravelDistanceM = maxTravelDistanceM, UnreachableCount = unreachable.Count };

            double maxDist = distancesM.Count > 0 ? distancesM.Values.Max() : 0;
            return new FireSafetyOutcome { Status = "ok", MaxDistM = maxDist, MaxTravelDistanceM = maxTravelDistanceM, WithinLimit = maxDist <= maxTravelDistanceM };
        }

        public static AccessibilityOutcome Access(SportifyLayout layout, double minWidthM)
        {
            if (layout.Placements == null || layout.Placements.Count == 0) return new AccessibilityOutcome { Status = "empty", MinWidthM = minWidthM };
            double currentWidth = layout.DesignRules?.CirculationWidthM ?? 0;
            var (_, unreachable) = CirculationEngine.ComputeTravelDistances(layout);
            return new AccessibilityOutcome
            {
                Status = "ok",
                WidthOk = currentWidth >= minWidthM,
                ReachOk = (layout.EntryPoints?.Count ?? 0) > 0 && unreachable.Count == 0,
                CurrentWidthM = currentWidth,
                MinWidthM = minWidthM,
            };
        }
    }
}
