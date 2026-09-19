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
        /// <summary>A grid line more than this many degrees off the roof's axes is not used (the bays are a rectangular grid).</summary>
        public const double MaxSkewDeg = 5.0;

        public static StructureInputs ToInputs(GoldbeckPayload payload)
        {
            var roof = payload.roof_context;
            if (roof == null || roof.length_m <= 0f || roof.width_m <= 0f)
                throw new InvalidOperationException("The layout has no roof size (roof_context length/width).");

            var inputs = new StructureInputs { RoofLength = roof.length_m, RoofWidth = roof.width_m };

            // Build-ups and plants are read exactly as the wind and rain analyses read them.
            var garden = WindLayoutAdapter.ToInputs(payload);
            foreach (var z in garden.Zones) inputs.Items.Add(StructureModel.ZoneItem(z));
            foreach (var p in garden.Plants)
                if (string.Equals(p.Form, "tree", StringComparison.OrdinalIgnoreCase)) inputs.Items.Add(StructureModel.TreeItem(p));

            var courts = 0;
            var activities = 0;
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
                        inputs.Items.Add(StructureModel.CourtItem(p.id ?? "field_" + courts, p.label ?? "Sports field " + courts, f != null && f.sport != null ? f.sport : p.label,
                            bb.top_left_x_m, bb.top_left_y_m, bb.width_m, bb.height_m, seats));
                    }
                    else if (string.Equals(p.category, "activity", StringComparison.OrdinalIgnoreCase))
                    {
                        activities++;
                        inputs.Items.Add(StructureModel.ActivityItem(p.id ?? "activity_" + activities, p.label ?? "Activity " + activities,
                            bb.top_left_x_m, bb.top_left_y_m, bb.width_m, bb.height_m));
                    }
                }
            }

            if (payload.entry_points != null)
                foreach (var e in payload.entry_points) inputs.Entries.Add(new double[] { e.x_m, e.y_m });

            var pathWidth = payload.design_rules != null && payload.design_rules.circulation_width_m > 0f ? payload.design_rules.circulation_width_m : 1.0;
            if (payload.circulation_paths != null)
            {
                foreach (var c in payload.circulation_paths)
                {
                    if (c.points_m == null || c.points_m.Length < 2) continue;
                    var path = new PathInput { WidthM = pathWidth };
                    foreach (var pt in c.points_m) path.Points.Add(new double[] { pt.x_m, pt.y_m });
                    inputs.Paths.Add(path);
                }
            }

            ReadStructure(payload.structure, inputs);
            return inputs;
        }

        static void ReadStructure(StructureData s, StructureInputs inputs)
        {
            if (s == null) return;
            inputs.GridSource = s.source ?? "";
            if (s.deck_capacity_kn_m2 > 0f) inputs.CapacityKnM2 = s.deck_capacity_kn_m2;

            var skewed = 0;
            if (s.grid_lines != null)
            {
                foreach (var g in s.grid_lines)
                {
                    if (g.start_m == null || g.end_m == null) continue;
                    double dx = g.end_m.x_m - g.start_m.x_m, dy = g.end_m.y_m - g.start_m.y_m;
                    if (Math.Sqrt(dx * dx + dy * dy) < 1e-6) continue;

                    var vertical = Math.Abs(dy) >= Math.Abs(dx);
                    var offAxisDeg = Math.Atan2(vertical ? Math.Abs(dx) : Math.Abs(dy), vertical ? Math.Abs(dy) : Math.Abs(dx)) * 180.0 / Math.PI;
                    if (offAxisDeg > MaxSkewDeg) { skewed++; continue; }

                    var line = new GridLineInput { Name = g.name ?? "", Position = vertical ? (g.start_m.x_m + (double)g.end_m.x_m) / 2.0 : (g.start_m.y_m + (double)g.end_m.y_m) / 2.0 };
                    (vertical ? inputs.VerticalLines : inputs.HorizontalLines).Add(line);
                }
            }
            if (skewed > 0)
                inputs.Notes.Add(skewed + " grid line" + (skewed == 1 ? " is" : "s are") + " more than " + MaxSkewDeg.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                                 " degrees off the roof's axes and was not used: the bays are a rectangular grid.");

            var outside = 0;
            if (s.columns != null)
            {
                foreach (var c in s.columns)
                {
                    if (c.x_m < -0.5 || c.x_m > inputs.RoofLength + 0.5 || c.y_m < -0.5 || c.y_m > inputs.RoofWidth + 0.5) { outside++; continue; }
                    inputs.Columns.Add(new double[] { c.x_m, c.y_m });
                }
            }
            if (outside > 0)
                inputs.Notes.Add(outside + " column" + (outside == 1 ? " lies" : "s lie") + " outside the roof and was left out.");
        }
    }
}
