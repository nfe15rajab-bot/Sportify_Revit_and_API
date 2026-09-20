using Sportify.Simulation;
using Sportify.Simulation.Dynamics;

namespace SportfyRevit
{
    /// <summary>
    /// Turns a Combine export into the inputs of the dynamic structural analysis
    /// (Sportify.Simulation/Assets/Scripts/Simulation/Dynamics/DynamicLoadCore.cs, compiled into this add-in as well, so the
    /// numbers need no Unity): the structural inputs and the wind inputs exactly as those analyses read them, plus the site's
    /// snow zone and altitude, the day's schedule and the engineer's natural frequency. Revit-free on purpose.
    ///
    /// The Unity project has its own reader of the same JSON (DynamicLayoutAdapter in Sportify.Simulation): the two must agree.
    /// </summary>
    internal static class DynamicLayoutAdapter
    {
        public static DynamicInputs ToInputs(SportifyLayout layout)
        {
            var site = layout.SiteConditions;
            var frequency = layout.Structure?.NaturalFrequencyHz;
            var snow = string.IsNullOrWhiteSpace(site?.SnowZone) ? null : site!.SnowZone!.Trim();
            var assumptions = layout.AnalysisAssumptions;
            var slab = layout.RoofContext?.Features?.Slab?.StructuralThicknessM;

            return new DynamicInputs
            {
                Structure = StructureLayoutAdapter.ToInputs(layout),
                Wind = WindLayoutAdapter.ToInputs(layout),
                SnowZone = snow,
                SnowZoneSource = snow == null ? "" : "set by the designer",
                AltitudeM = site is { AltitudeSet: true, AltitudeM: double altitude } ? InputQuantiser.Q(altitude) : null,
                Schedule = string.IsNullOrWhiteSpace(site?.DaySchedule) ? null : site!.DaySchedule,
                NaturalFrequencyHz = frequency is > 0 ? InputQuantiser.Q(frequency.Value) : null,
                // the slab's structural thickness from the Revit push (to 0.1 mm, as Unity's reader has it)
                SlabDepthM = slab is > 0 ? Math.Round(slab.Value, 4) : null,
                WalkingLimitG = assumptions?.ComfortLimitWalkingG is > 0 ? assumptions.ComfortLimitWalkingG : null,
                RhythmicLimitG = assumptions?.ComfortLimitRhythmicG is > 0 ? assumptions.ComfortLimitRhythmicG : null,
            };
        }
    }
}
