using Sportify.Simulation;
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
        /// <summary>
        /// A grid line within this many degrees of the roof's axes is taken as square to them (only where it crosses matters). A line further off
        /// keeps its own two points and cuts the roof at its own angle: the bays are then quadrilaterals.
        /// </summary>
        public const double AxisToleranceDeg = 0.05;

        public static StructureInputs ToInputs(SportifyLayout layout)
        {
            var roof = layout.RoofContext;
            if (roof == null || roof.LengthM <= 0 || roof.WidthM <= 0)
                throw new InvalidOperationException("The layout has no roof size (roof_context length/width).");

            var inputs = new StructureInputs { RoofLength = InputQuantiser.Q(roof.LengthM), RoofWidth = InputQuantiser.Q(roof.WidthM), Outline = WindLayoutAdapter.OutlineOf(roof) };

            // Build-ups and plants are read exactly as the wind and rain analyses read them.
            var garden = WindLayoutAdapter.ToInputs(layout);
            foreach (var z in garden.Zones) inputs.Items.Add(StructureModel.ZoneItem(z));
            foreach (var p in garden.Plants)
                if (string.Equals(p.Form, "tree", StringComparison.OrdinalIgnoreCase)) inputs.Items.Add(StructureModel.TreeItem(p));

            var courts = 0;
            var activities = 0;
            var furnitureCount = 0;
            foreach (var p in layout.Placements ?? new List<PlacementDto>())
            {
                var bb = p.BoundingBox;
                if (bb == null || bb.WidthM <= 0 || bb.HeightM <= 0) continue;

                if (string.Equals(p.Category, "field", StringComparison.OrdinalIgnoreCase))
                {
                    courts++;
                    var f = p.Parameters?.Field;
                    inputs.Items.Add(StructureModel.CourtItem(InputQuantiser.Or(p.Id, "field_" + courts), InputQuantiser.Or(p.Label, "Sports field " + courts), InputQuantiser.Or(f?.Sport, p.Label),
                        InputQuantiser.Q(bb.TopLeftXM), InputQuantiser.Q(bb.TopLeftYM), InputQuantiser.Q(bb.WidthM), InputQuantiser.Q(bb.HeightM), Math.Max(0, f?.Capacity?.Seats ?? 0)));
                }
                else if (string.Equals(p.Category, "activity", StringComparison.OrdinalIgnoreCase))
                {
                    activities++;
                    inputs.Items.Add(StructureModel.ActivityItem(InputQuantiser.Or(p.Id, "activity_" + activities), InputQuantiser.Or(p.Label, "Activity " + activities),
                        InputQuantiser.Q(bb.TopLeftXM), InputQuantiser.Q(bb.TopLeftYM), InputQuantiser.Q(bb.WidthM), InputQuantiser.Q(bb.HeightM)));
                }
                else if (string.Equals(p.Category, "furniture", StringComparison.OrdinalIgnoreCase))
                {
                    // a piece of furniture at its catalogue weight (a product with no weight is not a load); Unity's reader does the same
                    var furniture = p.Parameters?.Furniture;
                    if (furniture?.WeightKg is > 0)
                    {
                        furnitureCount++;
                        inputs.Items.Add(StructureModel.FurnitureItem(InputQuantiser.Or(p.Id, "furniture_" + furnitureCount), InputQuantiser.Or(p.Label, "Furniture " + furnitureCount),
                            InputQuantiser.Q(bb.TopLeftXM), InputQuantiser.Q(bb.TopLeftYM), InputQuantiser.Q(bb.WidthM), InputQuantiser.Q(bb.HeightM), InputQuantiser.Q(furniture.WeightKg.Value)));
                    }
                }
            }

            foreach (var e in layout.EntryPoints ?? new List<EntryPointDto>())
                inputs.Entries.Add(new[] { InputQuantiser.Q(e.XM), InputQuantiser.Q(e.YM) });

            var pathWidth = layout.DesignRules != null && layout.DesignRules.CirculationWidthM > 0 ? layout.DesignRules.CirculationWidthM : 1.0;
            foreach (var c in layout.CirculationPaths ?? new List<CirculationPathDto>())
            {
                if (c.PointsM == null || c.PointsM.Count < 2) continue;
                var path = new PathInput { WidthM = pathWidth };
                foreach (var pt in c.PointsM) path.Points.Add(new[] { InputQuantiser.Q(pt.XM), InputQuantiser.Q(pt.YM) });
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

            var slanted = 0;
            foreach (var g in s.GridLines ?? new List<GridLineDto>())
            {
                if (g.StartM == null || g.EndM == null) continue;
                double x0 = Math.Round(g.StartM.XM, 4), y0 = Math.Round(g.StartM.YM, 4), x1 = Math.Round(g.EndM.XM, 4), y1 = Math.Round(g.EndM.YM, 4);
                double dx = x1 - x0, dy = y1 - y0;
                if (Math.Sqrt(dx * dx + dy * dy) < 1e-6) continue;

                var vertical = Math.Abs(dy) >= Math.Abs(dx);
                var offAxisDeg = Math.Atan2(vertical ? Math.Abs(dx) : Math.Abs(dy), vertical ? Math.Abs(dy) : Math.Abs(dx)) * 180.0 / Math.PI;

                var line = new GridLineInput { Name = g.Name ?? "" };
                if (offAxisDeg <= AxisToleranceDeg)
                    line.Position = vertical ? (x0 + x1) / 2.0 : (y0 + y1) / 2.0;
                else
                {
                    slanted++;
                    line.HasGeometry = true; line.X0 = x0; line.Y0 = y0; line.X1 = x1; line.Y1 = y1;
                    // where it crosses the middle of the roof: what orders it among its neighbours
                    line.Position = vertical ? x0 + (inputs.RoofWidth / 2.0 - y0) * dx / dy : y0 + (inputs.RoofLength / 2.0 - x0) * dy / dx;
                }
                (vertical ? inputs.VerticalLines : inputs.HorizontalLines).Add(line);
            }
            if (slanted > 0)
                inputs.Notes.Add(slanted + " grid line" + (slanted == 1 ? " is" : "s are") + " not square to the roof's edges (more than " + AxisToleranceDeg.ToString(System.Globalization.CultureInfo.InvariantCulture) + " degrees off): the bays between them are quadrilaterals, not rectangles.");

            var outside = 0;
            foreach (var c in s.Columns ?? new List<StructuralColumnDto>())
            {
                double cx = InputQuantiser.Q(c.XM), cy = InputQuantiser.Q(c.YM);
                if (cx < -0.5 || cx > inputs.RoofLength + 0.5 || cy < -0.5 || cy > inputs.RoofWidth + 0.5) { outside++; continue; }
                if (!inputs.Shape.IsRectangle && !inputs.Shape.Contains(cx, cy) && inputs.Shape.DistanceToEdge(cx, cy) > 0.5) { outside++; continue; }   // in the notch of an L
                inputs.Columns.Add(new[] { cx, cy });
            }
            if (outside > 0)
                inputs.Notes.Add(outside + " column" + (outside == 1 ? " lies" : "s lie") + " outside the roof and was left out.");
        }
    }
}
