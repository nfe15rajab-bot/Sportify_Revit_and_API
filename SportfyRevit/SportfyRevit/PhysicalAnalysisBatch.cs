using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sportify.Simulation.Dynamics;
using Sportify.Simulation.Structure;
using Sportify.Simulation.Sun;
using Sportify.Simulation.Water;
using Sportify.Simulation.Wind;

namespace SportfyRevit
{
    /// <summary>What happened to one physical analysis when the batch sent them all to the web app.</summary>
    internal sealed class SentAnalysis
    {
        public string Key = "";
        public string Title = "";
        /// <summary>Computed and published.</summary>
        public bool Sent;
        /// <summary>One sentence of what it found, for the summary window.</summary>
        public string Headline = "";
        /// <summary>The result rests on inputs nobody confirmed (deck capacity, snow zone ...): the web app marks it PRELIMINARY.</summary>
        public bool Preliminary;
        /// <summary>A Unity recording of exactly these numbers was already there and stays with them.</summary>
        public bool VideoKept;
        /// <summary>There was a recording, but of other numbers (the layout or an input changed since): it is dropped, and a run of the Unity-based command films the new ones.</summary>
        public bool VideoDropped;
        /// <summary>Why it was not sent: nothing of that kind in the layout, or the layout could not be read.</summary>
        public string? Problem;
    }

    /// <summary>
    /// "Send Physical Analysis to Web App": every physical analysis that needs no Unity (wind and erosion, rain and percolation, static loads, dynamic analysis,
    /// sun and shade), run on the layout the web app pushed and published to it in one go, with no window in between. The numbers are the ones the individual
    /// commands compute (same cores, same readers, same published shape), so a result sent here and one from its own command are the same result.
    ///
    /// What it does not do: the ball trajectories (Unity's physics: run that command) and the videos (Unity). A recording that was made for exactly the numbers
    /// being published again stays with them; one made for other numbers is dropped rather than left beside figures it does not show.
    /// Revit-free on purpose, so Tools/ContractCheck can run it on the fixtures.
    /// </summary>
    internal static class PhysicalAnalysisBatch
    {
        /// <summary>Computes and publishes each analysis. Never throws for one analysis's sake: what could not be done says why in its own entry.</summary>
        public static List<SentAnalysis> Run(string layoutJson)
        {
            var results = new List<SentAnalysis>();
            SportifyLayout layout;
            try
            {
                layout = JsonSerializer.Deserialize<SportifyLayout>(layoutJson) ?? throw new InvalidOperationException("The layout was empty.");
            }
            catch (Exception ex)
            {
                foreach (var (key, title) in Analyses) results.Add(new SentAnalysis { Key = key, Title = title, Problem = "The layout could not be read: " + ex.Message });
                return results;
            }

            AnalysisResultPayload? before = null;
            if (RoofBoundaryServer.TryGetLatestAnalysisResults(out var beforeJson) && beforeJson != null)
            {
                try { before = JsonSerializer.Deserialize<AnalysisResultPayload>(beforeJson); } catch (Exception) { /* an unreadable earlier state: nothing to keep */ }
            }

            results.Add(Attempt("wind_erosion", "Wind and erosion", () =>
            {
                var inputs = WindLayoutAdapter.ToInputs(layout);
                if (inputs.Zones.Count == 0 && inputs.Plants.Count == 0) return Skip("no green-roof zones or plants in the layout");
                var report = WindModel.Analyse(inputs);
                var dto = AnalyzeWindErosionRiskCommand.BuildPublishedResult(report, WindModel.CaseStudy(inputs), null);
                var entry = KeepVideo(dto, before?.WindErosion, d => d.VideoPath, (d, v) => d.VideoPath = v);
                AnalysisResultPublisher.PublishWindErosion(dto);
                entry.Headline = $"{dto.TreesFailing} of {dto.TreesChecked} trees at risk, {dto.ZonesUpliftFlagged} of {dto.ZonesChecked} build-ups could lift";
                return entry;
            }));

            results.Add(Attempt("soil_percolation", "Rain and soil percolation", () =>
            {
                var wind = WindLayoutAdapter.ToInputs(layout);
                var inputs = new WaterInputs { RoofLength = wind.RoofLength, RoofWidth = wind.RoofWidth, RoofAreaM2 = wind.Shape.Area, Zones = wind.Zones };
                if (inputs.Zones.Count == 0) return Skip("no green-roof zones in the layout");
                var report = PercolationModel.Analyse(inputs);
                var dto = SimulateSoilPercolationCommand.BuildPublishedResult(report, SimulateSoilPercolationCommand.CaseStudy(inputs), null);
                var entry = KeepVideo(dto, before?.SoilPercolation, d => d.VideoPath, (d, v) => d.VideoPath = v);
                AnalysisResultPublisher.PublishSoilPercolation(dto);
                entry.Headline = $"a heavy shower is {dto.HeavyShowerRetainedPercent:0}% retained, a cloudburst {dto.CloudburstRetainedPercent:0}%; {dto.ZonesBelowTarget} of {dto.ZonesChecked} zones below target";
                return entry;
            }));

            results.Add(Attempt("structural_loads", "Structural loads", () =>
            {
                var inputs = StructureLayoutAdapter.ToInputs(layout);
                if (inputs.Items.Count == 0) return Skip("nothing on the roof weighs anything yet (no field, activity, green-roof zone or tree)");
                var report = StructureModel.Analyse(inputs);
                var dto = AnalyzeStructuralLoadsCommand.BuildPublishedResult(report, AnalyzeStructuralLoadsCommand.CaseStudy(inputs, report), null);
                var entry = KeepVideo(dto, before?.StructuralLoads, d => d.VideoPath, (d, v) => d.VideoPath = v);
                AnalysisResultPublisher.PublishStructuralLoads(dto);
                entry.Preliminary = dto.Preliminary;
                entry.Headline = $"{dto.BaysOverCapacity} of {dto.BaysChecked} bays over the deck capacity; the most loaded ({dto.WorstBay}) at {dto.PeakUtilisationPercent:0}%";
                return entry;
            }));

            results.Add(Attempt("dynamic_analysis", "Dynamic analysis", () =>
            {
                var inputs = DynamicLayoutAdapter.ToInputs(layout);
                if (!inputs.Structure.Items.Any(i => i.Persons > 0)) return Skip("nowhere for people to be yet (no field, activity or accessible garden)");
                var report = DynamicModel.Analyse(inputs);
                var dto = AnalyzeDynamicLoadsCommand.BuildPublishedResult(report, DynamicModel.CaseStudy(inputs, report), null);
                var entry = KeepVideo(dto, before?.DynamicAnalysis, d => d.VideoPath, (d, v) => d.VideoPath = v);
                AnalysisResultPublisher.PublishDynamicAnalysis(dto);
                entry.Preliminary = dto.Preliminary;
                entry.Headline = $"crowds peak at about {dto.PeakPersons:0} people around {dto.PeakAtHour:0} h; the governing case is {dto.WorstCase} in {dto.WorstCaseBay} at {dto.WorstCaseUtilisationPercent:0}% of the deck capacity";
                return entry;
            }));

            results.Add(Attempt("sun_and_shading", "Sun and shade", () =>
            {
                var inputs = SunLayoutAdapter.ToInputs(layout);
                if (inputs.Structure.Items.Count == 0) return Skip("nothing on the roof yet (no field, activity, green-roof zone or tree)");
                var report = SunModel.Analyse(inputs);
                var dto = AnalyzeSunShadeCommand.BuildPublishedResult(report, SunModel.CaseStudy(inputs, report), null);
                var entry = KeepVideo(dto, before?.SunAndShading, d => d.VideoPath, (d, v) => d.VideoPath = v);
                AnalysisResultPublisher.PublishSunAndShading(dto);
                entry.Preliminary = dto.Preliminary;
                entry.Headline = $"{dto.PeopleZonesTooSunny} of {dto.PeopleZones} people zones too sunny at midday ({dto.PeopleZonesTooSunnyAfter} with the shading), {dto.GardenZonesTooShaded} of {dto.GardenZones} gardens too shaded";
                return entry;
            }));

            return results;
        }

        static readonly (string Key, string Title)[] Analyses =
        {
            ("wind_erosion", "Wind and erosion"), ("soil_percolation", "Rain and soil percolation"), ("structural_loads", "Structural loads"),
            ("dynamic_analysis", "Dynamic analysis"), ("sun_and_shading", "Sun and shade"),
        };

        static SentAnalysis Skip(string why) => new SentAnalysis { Problem = "Not sent: " + why + "." };

        static SentAnalysis Attempt(string key, string title, Func<SentAnalysis> run)
        {
            SentAnalysis entry;
            try { entry = run(); }
            catch (Exception ex) { entry = new SentAnalysis { Problem = "Could not be computed: " + ex.Message }; }
            entry.Key = key;
            entry.Title = title;
            entry.Sent = entry.Problem == null;
            return entry;
        }

        /// <summary>
        /// A recording sits beside numbers, so it may stay only where those are the same numbers: the new result equals the published one in everything but the video
        /// path. Then the recording is put back on it (when the file is still there); otherwise it is left off, and the entry says a recording was dropped.
        /// </summary>
        static SentAnalysis KeepVideo<T>(T fresh, T? earlier, Func<T, string?> getVideo, Action<T, string?> setVideo) where T : class
        {
            var entry = new SentAnalysis();
            var video = earlier == null ? null : getVideo(earlier);
            if (string.IsNullOrEmpty(video)) return entry;

            if (File.Exists(video) && Fingerprint(fresh) == Fingerprint(earlier!))
            {
                setVideo(fresh, video);
                entry.VideoKept = true;
            }
            else entry.VideoDropped = true;
            return entry;
        }

        /// <summary>The result as JSON with its video path blanked: two results with the same fingerprint say the same thing.</summary>
        static string Fingerprint<T>(T dto) where T : class
        {
            var node = JsonSerializer.SerializeToNode(dto) as JsonObject;
            if (node != null && node.ContainsKey("video_path")) node["video_path"] = null;
            return node?.ToJsonString() ?? "";
        }
    }
}
