using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Sportify.Simulation
{
    /// <summary>A non-court placement (garden bed, activity) drawn for context only.</summary>
    public class ContextPatch
    {
        public string Category;
        public string Label;
        public float XMin, XMax, YMin, YMax;
    }

    public static class LayoutLoader
    {
        const string SampleFileName = "sample_layout_gardenBoundary.json";
        public const string RoofGardenSampleFileName = "sample_layout_roofgarden.json";

        // DIN 18032 style clear height used when a placement doesn't carry one.
        public const float DefaultClearHeightM = 9f;

        public static string LastPath { get; private set; }

        public static bool IsBundledSample =>
            LastPath != null && (Path.GetFileName(LastPath) == SampleFileName || Path.GetFileName(LastPath) == RoofGardenSampleFileName);

        public static bool IsRoofGardenSample =>
            LastPath != null && Path.GetFileName(LastPath) == RoofGardenSampleFileName;

        /// <param name="defaultSample">Bundled sample to use when no -layoutFile is given (default: the sports sample).</param>
        public static GoldbeckPayload Load(string overridePath = null, string defaultSample = null)
        {
            var path = ResolvePath(overridePath, defaultSample ?? SampleFileName);
            LastPath = path;

            var payload = JsonUtility.FromJson<GoldbeckPayload>(File.ReadAllText(path));
            var roof = payload?.roof_context;
            if (roof == null || roof.length_m <= 0f || roof.width_m <= 0f)
                throw new InvalidDataException(
                    "\"" + path + "\" is not a Sportify layout export (roof_context length/width missing).");

            return payload;
        }

        static string ResolvePath(string overridePath, string defaultSample)
        {
            if (!string.IsNullOrEmpty(overridePath)) return overridePath;

            foreach (var arg in System.Environment.GetCommandLineArgs())
            {
                if (arg.StartsWith("-layoutFile="))
                    return arg.Substring("-layoutFile=".Length);
            }

            return Path.Combine(Application.streamingAssetsPath, defaultSample);
        }

        /// <summary>
        /// The activity catalogue's ball courts (parameters.activity.category "court") and the sport whose ball and shots they are flown with
        /// (ShotScenario.Lookup). Bocce is left out on purpose: its balls are rolled along the ground, which a flight simulation says nothing about.
        /// </summary>
        static readonly Dictionary<string, string> ActivityCourtSports = new Dictionary<string, string>
        {
            { "padel_court", "padel" },
            { "pickleball_court", "pickleball" },
            { "ping_pong", "table tennis" },
            { "streetbasketball_3x3", "basketball" },
            { "badminton_outdoor", "badminton" },
            { "teqball_table", "football" },          // played with a size-5 football: flown with the futsal shots, on the long side
            { "multipurpose_court", "multi-purpose" }, // the generic ball
        };

        /// <summary>The sport an activity placement is flown as, or null when it is not a ball court (a playground, a fitness rig, bocce ...).</summary>
        public static string ActivityCourtSport(Placement p)
        {
            var a = p?.parameters?.activity;
            if (p == null || p.category != "activity" || a == null || a.category != "court" || string.IsNullOrEmpty(a.type_id)) return null;
            return ActivityCourtSports.TryGetValue(a.type_id.Trim(), out var sport) ? sport : null;
        }

        /// <summary>Ball courts of the activity catalogue that the simulation does not fly, by label (bocce), for the report to name.</summary>
        public static List<string> SkippedActivityCourts(GoldbeckPayload payload)
        {
            var skipped = new List<string>();
            if (payload?.placements == null) return skipped;
            foreach (var p in payload.placements)
            {
                var a = p?.parameters?.activity;
                if (p.category == "activity" && a != null && a.category == "court" && ActivityCourtSport(p) == null)
                    skipped.Add(string.IsNullOrEmpty(p.label) ? a.type_id : p.label);
            }
            return skipped;
        }

        public static List<CourtInstance> ExtractCourts(GoldbeckPayload payload)
        {
            var courts = new List<CourtInstance>();
            if (payload?.placements == null) return courts;

            var index = 0;
            foreach (var p in payload.placements)
            {
                // A court of the activity catalogue (padel, ping pong, pickleball ...): its footprint is its bounding box, it carries no
                // run-off or clear height of its own.
                var activitySport = ActivityCourtSport(p);
                if (activitySport != null && p.bounding_box != null)
                {
                    var abb = p.bounding_box;
                    courts.Add(new CourtInstance
                    {
                        Index = index++,
                        Sport = activitySport,
                        Norm = p.parameters.activity.norm,
                        RunoffM = 0f,
                        MinHeightM = DefaultClearHeightM,
                        RotationDeg = p.transform?.rotation_deg ?? 0f,
                        XMin = abb.top_left_x_m,
                        XMax = abb.top_left_x_m + abb.width_m,
                        YMin = abb.top_left_y_m,
                        YMax = abb.top_left_y_m + abb.height_m,
                    });
                    continue;
                }

                var field = p.parameters?.field;
                if (p.category != "field" || field?.dimensions == null || p.bounding_box == null) continue;

                var bb = p.bounding_box;
                var d = field.dimensions;

                courts.Add(new CourtInstance
                {
                    Index = index++,
                    Sport = string.IsNullOrEmpty(field.sport) ? "field" : field.sport.Trim(),
                    Norm = field.norm,
                    RunoffM = d.runoff_m,
                    MinHeightM = d.min_height_m > 0f ? d.min_height_m : DefaultClearHeightM,
                    RotationDeg = p.transform?.rotation_deg ?? 0f,
                    XMin = bb.top_left_x_m,
                    XMax = bb.top_left_x_m + bb.width_m,
                    YMin = bb.top_left_y_m,
                    YMax = bb.top_left_y_m + bb.height_m,
                });
            }

            return courts;
        }

        public static List<ContextPatch> ExtractContext(GoldbeckPayload payload)
        {
            var patches = new List<ContextPatch>();
            if (payload?.placements == null) return patches;

            foreach (var p in payload.placements)
            {
                if (p.category == "field" || p.category == "vegetation" || p.bounding_box == null || ActivityCourtSport(p) != null) continue;

                var bb = p.bounding_box;
                patches.Add(new ContextPatch
                {
                    Category = p.category,
                    Label = p.label,
                    XMin = bb.top_left_x_m,
                    XMax = bb.top_left_x_m + bb.width_m,
                    YMin = bb.top_left_y_m,
                    YMax = bb.top_left_y_m + bb.height_m,
                });
            }

            return patches;
        }
    }
}
