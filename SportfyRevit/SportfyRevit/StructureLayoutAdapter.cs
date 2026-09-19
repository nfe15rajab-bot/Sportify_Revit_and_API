using System;
using System.Collections.Generic;
using System.Linq;
using Sportify.Simulation.Structure;
using Sportify.Simulation.Wind;

namespace SportfyRevit
{
    /// <summary>
    /// Turns a Combine export into the inputs of the structural load model
    /// (Sportify.Simulation/Assets/Scripts/Simulation/Structure/StructuralLoadCore.cs, compiled into this add-in as well, so the
    /// numbers need no Unity). Revit-free on purpose: it reads only the layout DTOs, so it can be tested without Revit.
    ///
    /// The Unity project has its own reader of the same JSON (StructureLayoutAdapter in Sportify.Simulation): the two must agree,
    /// and the numbers this add-in publishes are cross-checked against Unity's.
    /// </summary>
    internal static class StructureLayoutAdapter
    {
        /// <summary>A grid line more than this many degrees off the roof's axes is not used (the bays are a rectangular grid).</summary>
        public const double MaxSkewDeg = 5.0;

        public static StructureInputs ToInputs(SportifyLayout layout)
        {
            var roof = layout.RoofContext;
            if (roof == null || roof.LengthM <= 0 || roof.WidthM <= 0)
                throw new InvalidOperationException("The layout has no roof size (roof_context length/width).");

            var inputs = new StructureInputs { RoofLength = roof.LengthM, RoofWidth = roof.WidthM };

            // Build-ups and plants are read exactly as the wind and rain analyses read them.
            var garden = WindLayoutAdapter.ToInputs(layout);
            foreach (var z in garden.Zones) inputs.Items.Add(StructureModel.ZoneItem(z));
            foreach (var p in garden.Plants)
                if (string.Equals(p.Form, "tree", StringComparison.OrdinalIgnoreCase)) inputs.Items.Add(StructureModel.TreeItem(p));

            var courts = 0;
            var activities = 0;
            foreach (var p in layout.Placements ?? new List<PlacementDto>())
            {
                var bb = p.BoundingBox;
                if (bb == null || bb.WidthM <= 0 || bb.HeightM <= 0) continue;

                if (string.Equals(p.Category, "field", StringComparison.OrdinalIgnoreCase))
                {
                    courts++;
                    var f = p.Parameters?.Field;
                    inputs.Items.Add(StructureModel.CourtItem(p.Id ?? "field_" + courts, p.Label ?? "Sports field " + courts, f?.Sport ?? p.Label,
                        bb.TopLeftXM, bb.TopLeftYM, bb.WidthM, bb.HeightM, Math.Max(0, f?.Capacity?.Seats ?? 0)));
                }
                else if (string.Equals(p.Category, "activity", StringComparison.OrdinalIgnoreCase))
                {
                    activities++;
                    inputs.Items.Add(StructureModel.ActivityItem(p.Id ?? "activity_" + activities, p.Label ?? "Activity " + activities,
                        bb.TopLeftXM, bb.TopLeftYM, bb.WidthM, bb.HeightM));
                }
            }

            foreach (var e in layout.EntryPoints ?? new List<EntryPointDto>())
                inputs.Entries.Add(new[] { e.XM, e.YM });

            var pathWidth = layout.DesignRules != null && layout.DesignRules.CirculationWidthM > 0 ? layout.DesignRules.CirculationWidthM : 1.0;
            foreach (var c in layout.CirculationPaths ?? new List<CirculationPathDto>())
            {
                if (c.PointsM == null || c.PointsM.Count < 2) continue;
                var path = new PathInput { WidthM = pathWidth };
                foreach (var pt in c.PointsM) path.Points.Add(new[] { pt.XM, pt.YM });
                inputs.Paths.Add(path);
            }

            ReadStructure(layout.Structure, inputs);
            foreach (var key in layout.AnalysisAssumptions?.Accepted ?? new List<string>())
                if (!string.IsNullOrEmpty(key) && !inputs.AcceptedAssumptions.Contains(key)) inputs.AcceptedAssumptions.Add(key);
            return inputs;
        }

        static void ReadStructure(StructureDto? s, StructureInputs inputs)
        {
            if (s == null) return;
            inputs.GridSource = s.Source ?? "";
            if (s.DeckCapacityKnM2 is > 0) inputs.CapacityKnM2 = s.DeckCapacityKnM2;

            var skewed = 0;
            foreach (var g in s.GridLines ?? new List<GridLineDto>())
            {
                if (g.StartM == null || g.EndM == null) continue;
                double dx = g.EndM.XM - g.StartM.XM, dy = g.EndM.YM - g.StartM.YM;
                if (Math.Sqrt(dx * dx + dy * dy) < 1e-6) continue;

                var vertical = Math.Abs(dy) >= Math.Abs(dx);
                var offAxisDeg = Math.Atan2(vertical ? Math.Abs(dx) : Math.Abs(dy), vertical ? Math.Abs(dy) : Math.Abs(dx)) * 180.0 / Math.PI;
                if (offAxisDeg > MaxSkewDeg) { skewed++; continue; }

                var line = new GridLineInput { Name = g.Name ?? "", Position = vertical ? (g.StartM.XM + g.EndM.XM) / 2.0 : (g.StartM.YM + g.EndM.YM) / 2.0 };
                (vertical ? inputs.VerticalLines : inputs.HorizontalLines).Add(line);
            }
            if (skewed > 0)
                inputs.Notes.Add(skewed + " grid line" + (skewed == 1 ? " is" : "s are") + " more than " + MaxSkewDeg + " degrees off the roof's axes and was not used: the bays are a rectangular grid.");

            var outside = 0;
            foreach (var c in s.Columns ?? new List<StructuralColumnDto>())
            {
                if (c.XM < -0.5 || c.XM > inputs.RoofLength + 0.5 || c.YM < -0.5 || c.YM > inputs.RoofWidth + 0.5) { outside++; continue; }
                inputs.Columns.Add(new[] { c.XM, c.YM });
            }
            if (outside > 0)
                inputs.Notes.Add(outside + " column" + (outside == 1 ? " lies" : "s lie") + " outside the roof and was left out.");
        }
    }
}
