using System;
using System.Collections.Generic;
using Sportify.Simulation.Wind;

namespace Sportify.Simulation.Structure
{
    /// <summary>
    /// Turns the layout the simulation loaded into the inputs of the structural load model. The Revit add-in has its own reader
    /// of the same JSON (SportfyRevit/StructureLayoutAdapter.cs), because JsonUtility and System.Text.Json don't share types:
    /// the two must agree, and the add-in's published numbers are checked against this run's.
    /// </summary>
    public static class StructureLayoutAdapter
    {
        /// <summary>A grid line within this many degrees of the roof's axes is taken as square to them; a line further off keeps its own two points (see the add-in's reader).</summary>
        public const double AxisToleranceDeg = 0.05;

        public static StructureInputs ToInputs(GoldbeckPayload payload)
        {
            var roof = payload.roof_context;
            if (roof == null || roof.length_m <= 0f || roof.width_m <= 0f)
                throw new InvalidOperationException("The layout has no roof size (roof_context length/width).");

            var inputs = new StructureInputs { RoofLength = InputQuantiser.Q(roof.length_m), RoofWidth = InputQuantiser.Q(roof.width_m), Outline = WindLayoutAdapter.OutlineOf(roof) };

            // Build-ups and plants are read exactly as the wind and rain analyses read them.
            var garden = WindLayoutAdapter.ToInputs(payload);
            foreach (var z in garden.Zones) inputs.Items.Add(StructureModel.ZoneItem(z));
            foreach (var p in garden.Plants)
                if (string.Equals(p.Form, "tree", StringComparison.OrdinalIgnoreCase)) inputs.Items.Add(StructureModel.TreeItem(p));

            var courts = 0;
            var activities = 0;
            var furnitureCount = 0;
            if (payload.placements != null)
            {
                foreach (var p in payload.placements)
                {
                    var bb = p.bounding_box;
                    if (bb == null || bb.width_m <= 0f || bb.height_m <= 0f) continue;

                    if (string.Equals(p.category, "field", StringComparison.OrdinalIgnoreCase))
                    {
                        courts++;
                        var f = p.parameters != null ? p.parameters.field : null;
                        var seats = f != null && f.capacity != null ? Math.Max(0, f.capacity.seats) : 0;
                        inputs.Items.Add(StructureModel.CourtItem(InputQuantiser.Or(p.id, "field_" + courts), InputQuantiser.Or(p.label, "Sports field " + courts), InputQuantiser.Or(f != null ? f.sport : null, p.label),
                            InputQuantiser.Q(bb.top_left_x_m), InputQuantiser.Q(bb.top_left_y_m), InputQuantiser.Q(bb.width_m), InputQuantiser.Q(bb.height_m), seats));
                    }
                    else if (string.Equals(p.category, "activity", StringComparison.OrdinalIgnoreCase))
                    {
                        activities++;
                        inputs.Items.Add(StructureModel.ActivityItem(InputQuantiser.Or(p.id, "activity_" + activities), InputQuantiser.Or(p.label, "Activity " + activities),
                            InputQuantiser.Q(bb.top_left_x_m), InputQuantiser.Q(bb.top_left_y_m), InputQuantiser.Q(bb.width_m), InputQuantiser.Q(bb.height_m)));
                    }
                    else if (string.Equals(p.category, "furniture", StringComparison.OrdinalIgnoreCase))
                    {
                        // a piece of furniture at its catalogue weight (a product with no weight is not a load)
                        var furniture = p.parameters != null ? p.parameters.furniture : null;
                        if (furniture != null && furniture.weight_kg > 0f)
                        {
                            furnitureCount++;
                            inputs.Items.Add(StructureModel.FurnitureItem(InputQuantiser.Or(p.id, "furniture_" + furnitureCount), InputQuantiser.Or(p.label, "Furniture " + furnitureCount),
                                InputQuantiser.Q(bb.top_left_x_m), InputQuantiser.Q(bb.top_left_y_m), InputQuantiser.Q(bb.width_m), InputQuantiser.Q(bb.height_m), InputQuantiser.Q(furniture.weight_kg)));
                        }
                    }
                }
            }

            if (payload.entry_points != null)
                foreach (var e in payload.entry_points) inputs.Entries.Add(new double[] { InputQuantiser.Q(e.x_m), InputQuantiser.Q(e.y_m) });

            var pathWidth = payload.design_rules != null && payload.design_rules.circulation_width_m > 0f ? payload.design_rules.circulation_width_m : 1.0;
            if (payload.circulation_paths != null)
            {
                foreach (var c in payload.circulation_paths)
                {
                    if (c.points_m == null || c.points_m.Length < 2) continue;
                    var path = new PathInput { WidthM = pathWidth };
                    foreach (var pt in c.points_m) path.Points.Add(new double[] { InputQuantiser.Q(pt.x_m), InputQuantiser.Q(pt.y_m) });
                    inputs.Paths.Add(path);
                }
            }

            ReadStructure(payload.structure, inputs);
            if (payload.analysis_assumptions != null && payload.analysis_assumptions.accepted != null)
                foreach (var key in payload.analysis_assumptions.accepted)
                    if (!string.IsNullOrEmpty(key) && !inputs.AcceptedAssumptions.Contains(key)) inputs.AcceptedAssumptions.Add(key);
            return inputs;
        }

        static void ReadStructure(StructureData s, StructureInputs inputs)
        {
            if (s == null) return;
            inputs.GridSource = s.source ?? "";
            if (s.deck_capacity_kn_m2 > 0f) inputs.CapacityKnM2 = s.deck_capacity_kn_m2;

            var slanted = 0;
            if (s.grid_lines != null)
            {
                foreach (var g in s.grid_lines)
                {
                    if (g.start_m == null || g.end_m == null) continue;
                    double x0 = Math.Round((double)g.start_m.x_m, 4), y0 = Math.Round((double)g.start_m.y_m, 4), x1 = Math.Round((double)g.end_m.x_m, 4), y1 = Math.Round((double)g.end_m.y_m, 4);
                    double dx = x1 - x0, dy = y1 - y0;
                    if (Math.Sqrt(dx * dx + dy * dy) < 1e-6) continue;

                    var vertical = Math.Abs(dy) >= Math.Abs(dx);
                    var offAxisDeg = Math.Atan2(vertical ? Math.Abs(dx) : Math.Abs(dy), vertical ? Math.Abs(dy) : Math.Abs(dx)) * 180.0 / Math.PI;

                    var line = new GridLineInput { Name = g.name ?? "" };
                    if (offAxisDeg <= AxisToleranceDeg)
                        line.Position = vertical ? (x0 + x1) / 2.0 : (y0 + y1) / 2.0;
                    else
                    {
                        slanted++;
                        line.HasGeometry = true; line.X0 = x0; line.Y0 = y0; line.X1 = x1; line.Y1 = y1;
                        line.Position = vertical ? x0 + (inputs.RoofWidth / 2.0 - y0) * dx / dy : y0 + (inputs.RoofLength / 2.0 - x0) * dy / dx;
                    }
                    (vertical ? inputs.VerticalLines : inputs.HorizontalLines).Add(line);
                }
            }
            if (slanted > 0)
                inputs.Notes.Add(slanted + " grid line" + (slanted == 1 ? " is" : "s are") + " not square to the roof's edges (more than " + AxisToleranceDeg.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                                 " degrees off): the bays between them are quadrilaterals, not rectangles.");

            var outside = 0;
            if (s.columns != null)
            {
                foreach (var c in s.columns)
                {
                    double cx = InputQuantiser.Q(c.x_m), cy = InputQuantiser.Q(c.y_m);
                    if (cx < -0.5 || cx > inputs.RoofLength + 0.5 || cy < -0.5 || cy > inputs.RoofWidth + 0.5) { outside++; continue; }
                    if (!inputs.Shape.IsRectangle && !inputs.Shape.Contains(cx, cy) && inputs.Shape.DistanceToEdge(cx, cy) > 0.5) { outside++; continue; }
                    inputs.Columns.Add(new double[] { cx, cy });
                }
            }
            if (outside > 0)
                inputs.Notes.Add(outside + " column" + (outside == 1 ? " lies" : "s lie") + " outside the roof and was left out.");
        }
    }
}
