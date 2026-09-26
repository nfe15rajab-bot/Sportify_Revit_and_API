using Sportify.Simulation;
using System;
using System.Collections.Generic;
using System.Linq;
using Sportify.Simulation.Wind;

namespace SportfyRevit
{
    /// <summary>
    /// Turns a Combine export into the inputs of the wind and erosion model
    /// (Sportify.Simulation/Assets/Scripts/Simulation/Wind/WindAnalysisCore.cs, compiled into this
    /// add-in as well, so the numbers need no Unity). Revit-free on purpose: it reads only the
    /// layout DTOs, so it can be tested without Revit.
    ///
    /// The Unity project has its own reader of the same JSON (WindLayoutAdapter in
    /// Sportify.Simulation), because JsonUtility and System.Text.Json don't share types. The two
    /// must agree; the wind results the add-in publishes are cross-checked against Unity's.
    /// </summary>
    internal static class WindLayoutAdapter
    {
        /// <summary>
        /// The roof's outline in the plan's own coordinates (y DOWN), from the pushed boundary polygon (which keeps y UP: see PushRoofBoundaryCommand),
        /// or null when the roof has none. Rounded to 0.1 mm so Unity's reader (floats) and this one (doubles) give the very same polygon.
        /// </summary>
        internal static List<double[]>? OutlineOf(RoofContextDto roof)
        {
            var poly = roof.SourceBoundaryPolygon;
            if (poly == null || poly.Count < 3) return null;
            return poly.Select(p => new[] { Math.Round(p.XM, 4), Math.Round(roof.WidthM - p.YM, 4) }).ToList();
        }

        /// <summary>A zone's outline as the model takes it (rounded like every input, see InputQuantiser), or null for the rectangle: fewer than three points is no outline. Unity's reader does the same.</summary>
        internal static List<double[]>? PointsOf(List<PointDto>? points)
        {
            if (points == null || points.Count < 3) return null;
            return points.Select(p => new[] { InputQuantiser.Q(p.XM), InputQuantiser.Q(p.YM) }).ToList();
        }

        public static WindInputs ToInputs(SportifyLayout layout)
        {
            var roof = layout.RoofContext;
            if (roof == null || roof.LengthM <= 0 || roof.WidthM <= 0)
                throw new InvalidOperationException("The layout has no roof size (roof_context length/width).");

            var inputs = new WindInputs
            {
                RoofLength = roof.LengthM,
                RoofWidth = roof.WidthM,
                RoofElevationM = roof.WorldOriginZM,
                RoofHeightAboveGroundM = roof.HeightAboveGroundM,
                RoofHeightSource = roof.HeightSource,
                Outline = OutlineOf(roof),
            };

            var site = layout.SiteConditions;
            if (site != null)
            {
                if (site.WindZone is >= 1 and <= 4)
                {
                    inputs.WindZone = site.WindZone.Value.ToString();
                    inputs.WindZoneSource = site.WindZoneSource;
                }
                if (site.NorthSet) inputs.NorthDeg = site.NorthDeg;     // by the flag, as Unity's reader has it
            }

            var assemblies = new Dictionary<string, AssemblyInput>(StringComparer.OrdinalIgnoreCase);
            foreach (var a in layout.Assemblies ?? new List<AssemblyDto>())
            {
                if (!string.IsNullOrEmpty(a.Key)) assemblies[a.Key!] = ToAssembly(a);
            }

            var zoneNumber = 0;

            // Ground zones drawn in the Combine tab.
            foreach (var z in layout.Zones ?? new List<ZoneDto>())
            {
                var bb = z.BoundingBox;
                if (bb == null || bb.WidthM <= 0 || bb.HeightM <= 0) continue;

                AssemblyInput? assembly = null;
                if (!string.IsNullOrEmpty(z.AssemblyKey)) assemblies.TryGetValue(z.AssemblyKey!, out assembly);

                zoneNumber++;
                inputs.Zones.Add(new ZoneInput
                {
                    Id = InputQuantiser.Or(z.Id, "zone_" + zoneNumber),
                    Label = "Green roof " + zoneNumber,
                    X = bb.TopLeftXM,
                    Y = bb.TopLeftYM,
                    Width = bb.WidthM,
                    Height = bb.HeightM,
                    Points = PointsOf(z.Points),
                    Assembly = assembly ?? Unknown(z.AssemblyKey),
                });
            }

            foreach (var p in layout.Placements ?? new List<PlacementDto>())
            {
                var bb = p.BoundingBox;
                if (bb == null) continue;

                // Garden parcels from older exports carry their build-up inside the placement.
                if (string.Equals(p.Category, "garden", StringComparison.OrdinalIgnoreCase) && bb.WidthM > 0 && bb.HeightM > 0)
                {
                    zoneNumber++;
                    inputs.Zones.Add(new ZoneInput
                    {
                        Id = InputQuantiser.Or(p.Id, "garden_" + zoneNumber),
                        Label = "Green roof " + zoneNumber,
                        X = bb.TopLeftXM,
                        Y = bb.TopLeftYM,
                        Width = bb.WidthM,
                        Height = bb.HeightM,
                        Assembly = GardenAssembly(p),
                    });
                    continue;
                }

                var veg = p.Parameters?.Vegetation;
                if (veg != null && string.Equals(p.Category, "vegetation", StringComparison.OrdinalIgnoreCase))
                {
                    var species = !string.IsNullOrWhiteSpace(veg.BotanicalName) ? veg.BotanicalName!
                                : !string.IsNullOrWhiteSpace(veg.CommonName) ? veg.CommonName!
                                : InputQuantiser.Or(p.Label, "plant");
                    inputs.Plants.Add(new PlantInput
                    {
                        Id = InputQuantiser.Or(p.Id, "plant_" + (inputs.Plants.Count + 1)),
                        Species = species,
                        Form = string.IsNullOrWhiteSpace(veg.Form) ? (veg.HeightM >= 2.5 ? "tree" : "shrub") : veg.Form!,
                        X = bb.TopLeftXM + bb.WidthM / 2.0,
                        Y = bb.TopLeftYM + bb.HeightM / 2.0,
                        HeightM = veg.HeightM,
                        CrownM = veg.CrownM > 0 ? veg.CrownM : Math.Max(bb.WidthM, bb.HeightM),
                        MinSubstrateMm = veg.MinSubstrateMm,
                    });
                }
            }

            return InputQuantiser.Apply(inputs);
        }

        /// <summary>One line naming what was analysed, shared with the Unity run's title card.</summary>
        public static string Describe(SportifyLayout layout, WindInputs inputs)
        {
            return WindModel.CaseStudy(inputs);
        }

        static AssemblyInput ToAssembly(AssemblyDto a)
        {
            var result = new AssemblyInput
            {
                Key = a.Key ?? "",
                System = ((a.Provider ?? "") + " " + (a.SystemName ?? "")).Trim(),
                SystemName = a.SystemName ?? "",
                Category = InputQuantiser.Or(a.Category, "extensive"),
                SaturatedKgM2 = a.SaturatedKgM2,
                WaterStorageLM2 = a.WaterStorageLM2,
            };

            foreach (var l in (a.Layers ?? new List<AssemblyLayerDto>()).OrderBy(l => l.Order))
            {
                result.Layers.Add(new LayerInput
                {
                    Function = l.Function ?? "",
                    Name = l.Name ?? "",
                    ThicknessMm = l.ThicknessM * 1000.0,
                });
            }
            return result;
        }

        static AssemblyInput Unknown(string? key)
        {
            return new AssemblyInput
            {
                Key = InputQuantiser.Or(key, "(none)"),
                System = string.IsNullOrEmpty(key) ? "no build-up chosen" : "unknown build-up \"" + key + "\"",
                Category = "extensive",
            };
        }

        /// <summary>The build-up of a garden parcel: its provider system if it has one, else its named layers.</summary>
        static AssemblyInput GardenAssembly(PlacementDto p)
        {
            var garden = p.Parameters?.Garden;
            if (garden?.Assembly != null) return ToAssembly(garden.Assembly);

            var result = new AssemblyInput
            {
                Key = "legacy_" + (p.Id ?? ""),
                System = InputQuantiser.Or(p.Label, "garden parcel"),
                Category = "extensive",
            };

            var layers = garden?.Layers ?? new List<GardenLayerDto>();
            for (var i = 0; i < layers.Count; i++)
            {
                var l = layers[i];
                result.Layers.Add(new LayerInput
                {
                    Function = WindModel.LegacyLayerFunction(l.LayerName, l.Material),
                    Name = string.IsNullOrEmpty(l.Material) ? (l.LayerName ?? "") : l.Material!,
                    ThicknessMm = l.ThicknessM * 1000.0,
                });
            }

            if (result.Layers.Any(l => l.Function == "substrate" && l.ThicknessMm > 150)) result.Category = "intensive";
            return result;
        }
    }
}
