using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Sportify.Simulation
{
    public static class LayoutLoader
    {
        public static GoldbeckPayload Load(string overridePath = null)
        {
            var path = ResolvePath(overridePath);
            var json = File.ReadAllText(path);
            return JsonUtility.FromJson<GoldbeckPayload>(json);
        }

        static string ResolvePath(string overridePath)
        {
            if (!string.IsNullOrEmpty(overridePath)) return overridePath;

            foreach (var arg in System.Environment.GetCommandLineArgs())
            {
                if (arg.StartsWith("-layoutFile="))
                    return arg.Substring("-layoutFile=".Length);
            }

            return Path.Combine(Application.streamingAssetsPath, "sample_layout_gardenBoundary.json");
        }

        public static List<CourtInstance> ExtractCourts(GoldbeckPayload payload)
        {
            var courts = new List<CourtInstance>();
            var index = 0;

            foreach (var p in payload.placements)
            {
                if (p.category != "field" || p.parameters?.field?.dimensions == null) continue;

                var bb = p.bounding_box;
                var d = p.parameters.field.dimensions;
                var centerX = bb.top_left_x_m + bb.width_m / 2f;
                var centerZ = bb.top_left_y_m + bb.height_m / 2f;

                courts.Add(new CourtInstance
                {
                    Index = index++,
                    Sport = p.parameters.field.sport,
                    Norm = p.parameters.field.norm,
                    RunoffM = d.runoff_m,
                    MinHeightM = d.min_height_m,
                    Center = new Vector3(centerX, 0f, centerZ),
                    Size = new Vector3(bb.width_m, d.min_height_m, bb.height_m),
                    RotationDeg = p.transform?.rotation_deg ?? 0f,
                });
            }

            return courts;
        }
    }
}
