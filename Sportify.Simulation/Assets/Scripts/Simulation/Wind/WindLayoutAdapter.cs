using System;
using System.Collections.Generic;
using System.Linq;

namespace Sportify.Simulation.Wind
{
    /// <summary>
    /// Turns the layout the simulation loaded into the inputs of the wind and erosion model. The Revit
    /// add-in has its own reader of the same JSON (SportfyRevit/WindLayoutAdapter.cs), because
    /// JsonUtility and System.Text.Json don't share types: the two must agree, and the add-in's
    /// published numbers are checked against this run's.
    /// </summary>
    public static class WindLayoutAdapter
    {
        /// <summary>The roof's outline in the plan's own coordinates (y DOWN) from the pushed boundary polygon (y UP), or null. The add-in's reader does the same, to 0.1 mm.</summary>
        public static List<double[]> OutlineOf(RoofContext roof)
        {
            var poly = roof.source_boundary_polygon;
            if (poly == null || poly.Length < 3) return null;
            var list = new List<double[]>();
            foreach (var p in poly) list.Add(new[] { Math.Round((double)p.x_m, 4), Math.Round((double)roof.width_m - p.y_m, 4) });
            return list;
        }

        /// <summary>A zone's outline as the model takes it (rounded like every input, see InputQuantiser), or null for the rectangle: fewer than three points is no outline.</summary>
        internal static List<double[]> PointsOf(PointM[] points)
        {
            if (points == null || points.Length < 3) return null;
            var list = new List<double[]>();
            foreach (var p in points) list.Add(new double[] { InputQuantiser.Q(p.x_m), InputQuantiser.Q(p.y_m) });
            return list;
        }

        public static WindInputs ToInputs(GoldbeckPayload payload)
        {
            var roof = payload.roof_context;
            if (roof == null || roof.length_m <= 0f || roof.width_m <= 0f)
                throw new InvalidOperationException("The layout has no roof size (roof_context length/width).");     // as the add-in's reader says
            var inputs = new WindInputs
            {
                RoofLength = roof.length_m,
                RoofWidth = roof.width_m,
                Outline = OutlineOf(roof),
                RoofElevationM = roof.world_origin_z_m,
                RoofHeightAboveGroundM = roof.height_above_ground_m,
                RoofHeightSource = roof.height_source,
            };

            var site = payload.site_conditions;
            if (site != null)
            {
                if (site.wind_zone >= 1 && site.wind_zone <= 4)
                {
                    inputs.WindZone = site.wind_zone.ToString();
                    inputs.WindZoneSource = site.wind_zone_source;
                }
                if (site.north_set) inputs.NorthDeg = site.north_deg;
            }

            var assemblies = new Dictionary<string, AssemblyInput>(StringComparer.OrdinalIgnoreCase);
            if (payload.assemblies != null)
            {
                foreach (var a in payload.assemblies)
                    if (!string.IsNullOrEmpty(a.key)) assemblies[a.key] = ToAssembly(a);
            }

            var zoneNumber = 0;

            // Ground zones drawn in the Combine tab.
            if (payload.zones != null)
            {
                foreach (var z in payload.zones)
                {
                    var bb = z.bounding_box;
                    if (bb == null || bb.width_m <= 0f || bb.height_m <= 0f) continue;

                    AssemblyInput assembly = null;
                    if (!string.IsNullOrEmpty(z.assembly_key)) assemblies.TryGetValue(z.assembly_key, out assembly);

                    zoneNumber++;
                    inputs.Zones.Add(new ZoneInput
                    {
                        Id = InputQuantiser.Or(z.id, "zone_" + zoneNumber),
                        Label = "Green roof " + zoneNumber,
                        X = bb.top_left_x_m,
                        Y = bb.top_left_y_m,
                        Width = bb.width_m,
                        Height = bb.height_m,
                        Points = PointsOf(z.points),
                        Assembly = assembly ?? Unknown(z.assembly_key),
                    });
                }
            }

            if (payload.placements != null)
            {
                foreach (var p in payload.placements)
                {
                    var bb = p.bounding_box;
                    if (bb == null) continue;

                    // Garden parcels from older exports carry their build-up inside the placement.
                    if (p.category == "garden" && bb.width_m > 0f && bb.height_m > 0f)
                    {
                        zoneNumber++;
                        inputs.Zones.Add(new ZoneInput
                        {
                            Id = InputQuantiser.Or(p.id, "garden_" + zoneNumber),
                            Label = "Green roof " + zoneNumber,
                            X = bb.top_left_x_m,
                            Y = bb.top_left_y_m,
                            Width = bb.width_m,
                            Height = bb.height_m,
                            Assembly = GardenAssembly(p),
                        });
                        continue;
                    }

                    var veg = p.parameters != null ? p.parameters.vegetation : null;
                    if (veg != null && p.category == "vegetation")
                    {
                        var species = !string.IsNullOrWhiteSpace(veg.botanical_name) ? veg.botanical_name
                                    : !string.IsNullOrWhiteSpace(veg.common_name) ? veg.common_name
                                    : InputQuantiser.Or(p.label, "plant");
                        inputs.Plants.Add(new PlantInput
                        {
                            Id = InputQuantiser.Or(p.id, "plant_" + (inputs.Plants.Count + 1)),
                            Species = species,
                            Form = string.IsNullOrWhiteSpace(veg.form) ? (veg.height_m >= 2.5f ? "tree" : "shrub") : veg.form,
                            X = bb.top_left_x_m + bb.width_m / 2.0,
                            Y = bb.top_left_y_m + bb.height_m / 2.0,
                            HeightM = veg.height_m,
                            CrownM = veg.crown_m > 0f ? veg.crown_m : Math.Max(bb.width_m, bb.height_m),
                            MinSubstrateMm = veg.min_substrate_mm,
                        });
                    }
                }
            }

            return InputQuantiser.Apply(inputs);
        }

        /// <summary>Total build-up thickness for drawing, in metres.</summary>
        public static double TotalThicknessM(AssemblyInput a)
        {
            return a == null ? 0.0 : a.Layers.Sum(l => l.ThicknessMm) / 1000.0;
        }

        static AssemblyInput ToAssembly(AssemblyData a)
        {
            var result = new AssemblyInput
            {
                Key = a.key ?? "",
                System = ((a.provider ?? "") + " " + (a.system_name ?? "")).Trim(),
                SystemName = a.system_name ?? "",
                Category = InputQuantiser.Or(a.category, "extensive"),
                // Both figures or neither: the provider prints them together.
                SaturatedKgM2 = a.saturated_kg_m2 > 0f && a.water_storage_l_m2 > 0f ? a.saturated_kg_m2 : (double?)null,
                WaterStorageLM2 = a.saturated_kg_m2 > 0f && a.water_storage_l_m2 > 0f ? a.water_storage_l_m2 : (double?)null,
            };

            if (a.layers != null)
            {
                foreach (var l in a.layers.OrderBy(l => l.order))
                {
                    result.Layers.Add(new LayerInput
                    {
                        Function = l.function ?? "",
                        Name = l.name ?? "",
                        ThicknessMm = l.thickness_m * 1000.0,
                    });
                }
            }
            return result;
        }

        static AssemblyInput Unknown(string key)
        {
            return new AssemblyInput
            {
                Key = InputQuantiser.Or(key, "(none)"),
                System = string.IsNullOrEmpty(key) ? "no build-up chosen" : "unknown build-up \"" + key + "\"",
                Category = "extensive",
            };
        }

        static AssemblyInput GardenAssembly(Placement p)
        {
            var garden = p.parameters != null ? p.parameters.garden : null;
            if (garden != null && garden.assembly != null && !string.IsNullOrEmpty(garden.assembly.key))
                return ToAssembly(garden.assembly);

            var result = new AssemblyInput
            {
                Key = "legacy_" + (p.id ?? ""),
                System = InputQuantiser.Or(p.label, "garden parcel"),
                Category = "extensive",
            };

            if (garden != null && garden.layers != null)
            {
                foreach (var l in garden.layers)
                {
                    result.Layers.Add(new LayerInput
                    {
                        Function = WindModel.LegacyLayerFunction(l.layer_name, l.material),
                        Name = string.IsNullOrEmpty(l.material) ? (l.layer_name ?? "") : l.material,
                        ThicknessMm = l.thickness_m * 1000.0,
                    });
                }
            }

            if (result.Layers.Any(l => l.Function == "substrate" && l.ThicknessMm > 150)) result.Category = "intensive";
            return result;
        }
    }
}
