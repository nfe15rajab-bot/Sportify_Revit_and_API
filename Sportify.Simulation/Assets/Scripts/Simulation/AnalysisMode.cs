using System;

namespace Sportify.Simulation
{
    /// <summary>
    /// Which analysis this play-mode session runs. The scene is deliberately empty and each runner
    /// bootstraps itself, so they must agree on who goes: BatchRunner sets this before entering play
    /// mode. An environment variable, because it has to survive the domain reload that entering
    /// play mode triggers, and a command-line argument can't tell a hand-started run from a batch one.
    /// Nothing set means the ball trajectory simulation.
    /// </summary>
    public static class AnalysisMode
    {
        public const string EnvVar = "SPORTIFY_ANALYSIS";
        public const string Wind = "wind";
        public const string Percolation = "percolation";

        public static string Current => Environment.GetEnvironmentVariable(EnvVar);

        public static bool IsWind => string.Equals(Current, Wind, StringComparison.OrdinalIgnoreCase);
        public static bool IsPercolation => string.Equals(Current, Percolation, StringComparison.OrdinalIgnoreCase);
        public static bool IsBallSimulation => string.IsNullOrEmpty(Current);
    }
}
