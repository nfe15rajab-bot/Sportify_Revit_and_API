using Sportify.Simulation.Structure;
using Sportify.Simulation.Wind;

namespace Sportify.Simulation.Dynamics
{
    /// <summary>
    /// Turns the layout the simulation loaded into the inputs of the dynamic structural analysis. The Revit add-in has its own
    /// reader of the same JSON (SportfyRevit/DynamicLayoutAdapter.cs), because JsonUtility and System.Text.Json don't share
    /// types: the two must agree, and the add-in's published numbers are checked against this run's.
    /// </summary>
    public static class DynamicLayoutAdapter
    {
        public static DynamicInputs ToInputs(GoldbeckPayload payload)
        {
            var site = payload.site_conditions;
            var snow = site != null && !string.IsNullOrWhiteSpace(site.snow_zone) ? site.snow_zone.Trim() : null;
            var frequency = payload.structure != null && payload.structure.natural_frequency_hz > 0f ? payload.structure.natural_frequency_hz : (double?)null;

            return new DynamicInputs
            {
                Structure = StructureLayoutAdapter.ToInputs(payload),
                Wind = WindLayoutAdapter.ToInputs(payload),
                SnowZone = snow,
                SnowZoneSource = snow == null ? "" : "set by the designer",
                AltitudeM = site != null && site.altitude_set ? site.altitude_m : (double?)null,
                Schedule = site != null && !string.IsNullOrWhiteSpace(site.day_schedule) ? site.day_schedule : null,
                NaturalFrequencyHz = frequency,
            };
        }
    }
}
