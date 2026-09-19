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

        public static List<CourtInstance> ExtractCourts(GoldbeckPayload payload)
        {
            var courts = new List<CourtInstance>();
            if (payload?.placements == null) return courts;

            var index = 0;
            foreach (var p in payload.placements)
            {
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
                if (p.category == "field" || p.category == "vegetation" || p.bounding_box == null) continue;

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
