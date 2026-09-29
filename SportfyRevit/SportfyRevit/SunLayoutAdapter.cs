using Sportify.Simulation.Sun;

namespace SportfyRevit
{
    /// <summary>
    /// Turns a Combine export into the inputs of the sun and shade model (Sportify.Simulation/Assets/Scripts/Simulation/Sun/SunShadeCore.cs,
    /// compiled into this add-in as well, so the numbers need no Unity): the pieces and plants exactly as the structural and wind analyses read
    /// them, plus the site's latitude and orientation, the roof's outline, the walls that cast shadows, the drains and openings the equipment
    /// must keep clear of, and the designer's shade target, garden need and choice of equipment. Revit-free on purpose.
    ///
    /// The Unity project has its own reader of the same JSON (SunLayoutAdapter in Sportify.Simulation): the two must agree.
    /// </summary>
    internal static class SunLayoutAdapter
    {
        public static SunInputs ToInputs(SportifyLayout layout)
        {
            var inputs = new SunInputs
            {
                Structure = StructureLayoutAdapter.ToInputs(layout),
                Wind = WindLayoutAdapter.ToInputs(layout),
            };

            // the site's latitude: a value typed into the assumptions wins over the map's; 0 means "not given"
            var a = layout.AnalysisAssumptions;
            var loc = layout.SiteLocation;
            if (a?.SiteLatitudeDeg is { } typed && typed != 0) inputs.LatitudeDeg = Math.Round(typed, 5);
            else if (loc != null && (loc.LatitudeDeg != 0 || loc.LongitudeDeg != 0)) inputs.LatitudeDeg = Math.Round(loc.LatitudeDeg, 5);

            var north = layout.SiteConditions is { NorthSet: true } ? layout.SiteConditions.NorthDeg : null;    // by the flag, as Unity's reader has it
            if (north.HasValue) inputs.NorthDeg = Math.Round(north.Value, 3);

            if (a?.ShadeTargetPercent is > 0) inputs.ShadeTargetPercent = Math.Round(a.ShadeTargetPercent.Value, 4);
            if (a?.GardenMinSunHours is > 0) inputs.GardenMinSunHours = Math.Round(a.GardenMinSunHours.Value, 4);
            if (!string.IsNullOrEmpty(a?.ShadeEquipment)) inputs.Equipment = a!.ShadeEquipment;

            var roof = layout.RoofContext;
            if (roof != null)
            {
                // the outline travels in Revit's convention (y up from the roof's minimum corner); the plan's y runs down
                if (roof.SourceBoundaryPolygon is { Count: >= 3 })
                    foreach (var p in roof.SourceBoundaryPolygon)
                        inputs.Outline.Add(new[] { Math.Round(p.XM, 3), Math.Round(roof.WidthM - p.YM, 3) });

                var f = roof.Features;
                if (f != null)
                {
                    foreach (var o in f.Obstacles)
                    {
                        inputs.Obstacles.Add(new ObstacleInput
                        {
                            Name = o.Name ?? "", X0 = Math.Round(o.StartM.XM, 3), Y0 = Math.Round(o.StartM.YM, 3), X1 = Math.Round(o.EndM.XM, 3), Y1 = Math.Round(o.EndM.YM, 3),
                            HeightM = Math.Round(o.HeightM, 3), ThicknessM = Math.Round(o.ThicknessM, 3),
                        });
                    }
                    // Plant on the roof shades it like a wall: a box is a wall along its longer side, as thick as its shorter one.
                    foreach (var q in f.Equipment)
                    {
                        if (q.HeightM <= 0.2) continue;
                        inputs.Obstacles.Add(BoxObstacle(q.Name, q.HeightM, q.XM, q.YM, q.WidthM, q.DepthM));
                    }
                    foreach (var d in f.Drains) inputs.Drains.Add(new[] { Math.Round(d.XM, 3), Math.Round(d.YM, 3) });
                    foreach (var o in f.Openings)
                    {
                        if (o.PolygonM.Count < 3) continue;
                        inputs.Openings.Add(o.PolygonM.Select(p => new[] { Math.Round(p.XM, 3), Math.Round(p.YM, 3) }).ToArray());
                    }
                }
            }

            // Trees and planters the designer placed (category "vegetation", or a garden block like Planter S/T that carries a plant) shade the
            // roof just like a pre-existing plant does: same wall treatment, using the plant's own mature height, not the planter box's.
            if (layout.Placements != null)
                foreach (var pl in layout.Placements)
                {
                    var veg = pl.Parameters?.Vegetation;
                    var bb = pl.BoundingBox;
                    if (veg == null || veg.HeightM <= 0.2 || bb == null) continue;
                    if (pl.Category != "vegetation" && pl.Category != "gardenBlock") continue;
                    var cx = bb.TopLeftXM + bb.WidthM / 2;
                    var cy = bb.TopLeftYM + bb.HeightM / 2;
                    inputs.Obstacles.Add(BoxObstacle(pl.Label, veg.HeightM, cx, cy, bb.WidthM, bb.HeightM));
                }
            return inputs;
        }

        static ObstacleInput BoxObstacle(string? name, double heightM, double x, double y, double w, double d)
        {
            x = Math.Round(x, 3); y = Math.Round(y, 3); w = Math.Round(w, 3); d = Math.Round(d, 3);
            var alongX = w >= d;
            return new ObstacleInput
            {
                Name = name ?? "", HeightM = Math.Round(heightM, 3), ThicknessM = alongX ? d : w,
                X0 = alongX ? Math.Round(x - w / 2, 3) : x, Y0 = alongX ? y : Math.Round(y - d / 2, 3),
                X1 = alongX ? Math.Round(x + w / 2, 3) : x, Y1 = alongX ? y : Math.Round(y + d / 2, 3),
            };
        }
    }
}
