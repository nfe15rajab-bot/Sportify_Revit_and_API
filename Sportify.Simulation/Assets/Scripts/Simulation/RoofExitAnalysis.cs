using System;
using System.Collections.Generic;

namespace Sportify.Simulation
{
    // This file deliberately uses nothing from UnityEngine: it is plain
    // arithmetic over recorded crossings, so it can be tested outside Unity.

    /// <summary>What one simulated stray shot did, as far as the roof edge is concerned.</summary>
    public class SweepOutcome
    {
        public int CourtIndex;
        public string Edge;               // top | bottom | left | right - null if the shot stayed on the roof
        public float Along;               // where on that edge it crossed (x for top/bottom, y for left/right), metres
        public float Height;              // how high it was when it crossed, metres
        public bool EnteredNeighbour;
        public bool EnteredCirculation;
    }

    [Serializable]
    public class RoofEdgeExit
    {
        public string edge;
        public int shots;
        public float percentOfShots;      // of ALL swept shots
        public float alongMinM;
        public float alongMaxM;
        public float heightP95M;          // 95% of the shots leaving over this edge were at or below this height
        public float heightMaxM;
        public List<int> courts = new List<int>();
    }

    [Serializable]
    public class FenceRecommendation
    {
        public string edge;
        public float fromM;               // along the edge
        public float toM;
        public float lengthM;
        public float heightM;             // stops 95% of the shots that leave over this edge
        public float fullHeightM;         // stops every one of them
        public float stopsPercentOfExits; // share of this edge's exits the recommended height actually stops
        public List<int> courts = new List<int>();
    }

    [Serializable]
    public class RoofExitReport
    {
        public bool ran;
        public string assumptions = "";
        public int shotsSwept;
        public int shotsLeavingRoof;
        public float percentLeavingRoof;
        public float percentLeavingAfterFences;   // shots still leaving OVER the recommended fences
        public float percentEnteringOtherCourts;
        public float percentEnteringCirculation;
        public List<RoofEdgeExit> byEdge = new List<RoofEdgeExit>();
        public List<FenceRecommendation> fences = new List<FenceRecommendation>();
    }

    /// <summary>
    /// Turns the swept stray shots into a percentage and a fence proposal.
    ///
    /// The percentage is the share of the SWEPT shot set that leaves the roof -
    /// a way to compare layouts and edges, not the chance that a real ball
    /// does. A fence is proposed for every edge a shot left over: it spans the
    /// stretch of edge the shots crossed (plus a margin), and is tall enough to
    /// catch 95% of them (the height that would catch all of them is reported
    /// alongside, since a few high lobs can ask for an impractical fence).
    /// </summary>
    public static class RoofExitAnalysis
    {
        public const float MarginM = 2.0f;        // added to each end of a fence
        public const float RoundingM = 0.5f;      // fences are proposed in half-metre steps
        public const float CoverageFraction = 0.95f;

        static readonly string[] EdgeOrder = { "top", "bottom", "left", "right" };

        public static RoofExitReport Analyse(IList<SweepOutcome> outcomes, float roofLengthM, float roofWidthM)
        {
            var report = new RoofExitReport { ran = true, shotsSwept = outcomes.Count };
            if (outcomes.Count == 0) return report;

            var total = (float)outcomes.Count;
            var leftOverFence = 0;
            var neighbour = 0;
            var circulation = 0;

            foreach (var o in outcomes)
            {
                if (o.EnteredNeighbour) neighbour++;
                if (o.EnteredCirculation) circulation++;
            }

            foreach (var edge in EdgeOrder)
            {
                var exits = new List<SweepOutcome>();
                foreach (var o in outcomes)
                    if (o.Edge == edge) exits.Add(o);
                if (exits.Count == 0) continue;

                var edgeLength = edge == "top" || edge == "bottom" ? roofLengthM : roofWidthM;

                var along = new List<float>();
                var heights = new List<float>();
                var courts = new SortedSet<int>();
                foreach (var e in exits)
                {
                    along.Add(e.Along);
                    heights.Add(e.Height);
                    courts.Add(e.CourtIndex);
                }
                heights.Sort();

                var alongMin = Min(along);
                var alongMax = Max(along);
                var p95 = Percentile(heights, CoverageFraction);
                var maxHeight = heights[heights.Count - 1];

                report.byEdge.Add(new RoofEdgeExit
                {
                    edge = edge,
                    shots = exits.Count,
                    percentOfShots = exits.Count / total * 100f,
                    alongMinM = alongMin,
                    alongMaxM = alongMax,
                    heightP95M = p95,
                    heightMaxM = maxHeight,
                    courts = new List<int>(courts),
                });

                var fence = new FenceRecommendation
                {
                    edge = edge,
                    fromM = Math.Max(0f, RoundDown(alongMin - MarginM)),
                    toM = Math.Min(edgeLength, RoundUp(alongMax + MarginM)),
                    heightM = RoundUp(p95),
                    fullHeightM = RoundUp(maxHeight),
                    courts = new List<int>(courts),
                };
                fence.lengthM = fence.toM - fence.fromM;

                var stopped = 0;
                foreach (var h in heights)
                {
                    if (h <= fence.heightM) stopped++;
                    else leftOverFence++;
                }
                fence.stopsPercentOfExits = stopped / (float)exits.Count * 100f;
                report.fences.Add(fence);

                report.shotsLeavingRoof += exits.Count;
            }

            report.percentLeavingRoof = report.shotsLeavingRoof / total * 100f;
            report.percentLeavingAfterFences = leftOverFence / total * 100f;
            report.percentEnteringOtherCourts = neighbour / total * 100f;
            report.percentEnteringCirculation = circulation / total * 100f;
            return report;
        }

        // Nearest-rank percentile of an ascending list.
        public static float Percentile(List<float> ascending, float fraction)
        {
            var rank = (int)Math.Ceiling(fraction * ascending.Count);
            var index = Math.Min(ascending.Count - 1, Math.Max(0, rank - 1));
            return ascending[index];
        }

        static float RoundUp(float v)
        {
            return (float)(Math.Ceiling(v / RoundingM - 1e-4) * RoundingM);
        }

        static float RoundDown(float v)
        {
            return (float)(Math.Floor(v / RoundingM + 1e-4) * RoundingM);
        }

        static float Min(List<float> values)
        {
            var m = values[0];
            foreach (var v in values) if (v < m) m = v;
            return m;
        }

        static float Max(List<float> values)
        {
            var m = values[0];
            foreach (var v in values) if (v > m) m = v;
            return m;
        }
    }
}
