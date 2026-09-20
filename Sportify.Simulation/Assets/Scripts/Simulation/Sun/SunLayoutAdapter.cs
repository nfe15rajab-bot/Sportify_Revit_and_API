using System;
using System.Collections.Generic;
using Sportify.Simulation.Structure;
using Sportify.Simulation.Wind;

namespace Sportify.Simulation.Sun
{
    /// <summary>
    /// Turns the layout the simulation loaded into the inputs of the sun and shade model. The Revit add-in has its own reader of the same
    /// JSON (SportfyRevit/SunLayoutAdapter.cs), because JsonUtility and System.Text.Json don't share types: the two must agree, and the
    /// add-in's published numbers are checked against this run's.
    /// </summary>
    public static class SunLayoutAdapter
    {
        static double R(float v, int digits) { return Math.Round((double)v, digits); }

        public static SunInputs ToInputs(GoldbeckPayload payload)
        {
            var inputs = new SunInputs
            {
                Structure = StructureLayoutAdapter.ToInputs(payload),
                Wind = WindLayoutAdapter.ToInputs(payload),
            };

            // the site's latitude: a value typed into the assumptions wins over the map's; 0 means "not given"
            var a = payload.analysis_assumptions;
            var loc = payload.site_location;
            if (a != null && a.site_latitude_deg != 0f) inputs.LatitudeDeg = R(a.site_latitude_deg, 5);
            else if (loc != null && (loc.latitude_deg != 0f || loc.longitude_deg != 0f)) inputs.LatitudeDeg = R(loc.latitude_deg, 5);

            var site = payload.site_conditions;
            if (site != null && site.north_set) inputs.NorthDeg = R(site.north_deg, 3);

            if (a != null)
            {
                if (a.shade_target_percent > 0f) inputs.ShadeTargetPercent = R(a.shade_target_percent, 4);
                if (a.garden_min_sun_hours > 0f) inputs.GardenMinSunHours = R(a.garden_min_sun_hours, 4);
                if (!string.IsNullOrEmpty(a.shade_equipment)) inputs.Equipment = a.shade_equipment;
            }

            var roof = payload.roof_context;
            if (roof != null)
            {
                // the outline travels in Revit's convention (y up from the roof's minimum corner); the plan's y runs down
                if (roof.source_boundary_polygon != null && roof.source_boundary_polygon.Length >= 3)
                    foreach (var p in roof.source_boundary_polygon)
                        inputs.Outline.Add(new[] { R(p.x_m, 3), Math.Round(roof.width_m - (double)p.y_m, 3) });

                var f = roof.features;
                if (f != null)
                {
                    if (f.obstacles != null)
                        foreach (var o in f.obstacles)
                        {
                            if (o.start_m == null || o.end_m == null) continue;
                            inputs.Obstacles.Add(new ObstacleInput
                            {
                                Name = o.name ?? "", X0 = R(o.start_m.x_m, 3), Y0 = R(o.start_m.y_m, 3), X1 = R(o.end_m.x_m, 3), Y1 = R(o.end_m.y_m, 3),
                                HeightM = R(o.height_m, 3), ThicknessM = R(o.thickness_m, 3),
                            });
                        }
                    // Plant on the roof shades it like a wall: a box is a wall along its longer side, as thick as its shorter one.
                    if (f.equipment != null)
                        foreach (var q in f.equipment)
                        {
                            if (q.height_m <= 0.2f) continue;
                            double x = R(q.x_m, 3), y = R(q.y_m, 3), w = R(q.width_m, 3), d2 = R(q.depth_m, 3);
                            var alongX = w >= d2;
                            inputs.Obstacles.Add(new ObstacleInput
                            {
                                Name = q.name ?? "", HeightM = R(q.height_m, 3), ThicknessM = alongX ? d2 : w,
                                X0 = alongX ? Math.Round(x - w / 2, 3) : x, Y0 = alongX ? y : Math.Round(y - d2 / 2, 3),
                                X1 = alongX ? Math.Round(x + w / 2, 3) : x, Y1 = alongX ? y : Math.Round(y + d2 / 2, 3),
                            });
                        }
                    if (f.drains != null)
                        foreach (var d in f.drains) inputs.Drains.Add(new[] { R(d.x_m, 3), R(d.y_m, 3) });
                    if (f.openings != null)
                        foreach (var o in f.openings)
                        {
                            if (o.polygon_m == null || o.polygon_m.Length < 3) continue;
                            var poly = new List<double[]>();
                            foreach (var p in o.polygon_m) poly.Add(new[] { R(p.x_m, 3), R(p.y_m, 3) });
                            inputs.Openings.Add(poly.ToArray());
                        }
                }
            }
            return inputs;
        }
    }
}
