using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Sportify.Simulation.Sun;

namespace SportfyRevit
{
    /// <summary>
    /// Sun and shade on the roof: the hours of direct sun every part of it gets on the summer solstice, the equinox and the winter solstice, which
    /// play areas and spectator zones are too sunny at midday and which gardens too shaded, and the shading equipment (pergolas, canopies, sails,
    /// parasols, a tree in a deep bed) that would fix it without taking the sun from the gardens, with its weight, the wind on it and what it does to
    /// the deck. It replaces the old "Sun & Shading" button, which only pointed at Revit's own sun tools.
    ///
    /// The numbers come from SunShadeCore.cs, compiled into this add-in from the Unity project's sources, so they need no Unity and appear at once;
    /// Unity only adds the video (the shadows sweeping the roof, the sun-hours building up, the equipment going up), offered afterwards when the
    /// Editor is installed. A screening model, not a daylight or thermal design: direct sun only, in solar time, without the neighbouring buildings;
    /// the site's latitude and orientation, the shade target and what the gardens need are inputs the designer enters or accepts, and the results say
    /// which they did (PRELIMINARY while any is unconfirmed).
    /// </summary>
    [Transaction(TransactionMode.ReadOnly)]
    public class AnalyzeSunShadeCommand : IExternalCommand
    {
        const string DialogTitle = "Sportify — Sun & Shade";
        const string BundledSampleFile = "sample_layout_roofgarden.json";
        const int VideoTimeoutMs = 600_000;

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            RoofBoundaryServer.TryGetLatestCombinedLayout(out var baseJson, out _);
            var usingBundledSample = false;

            var haveUnity = UnityHeadlessRunner.TryLocate(out var unity, out _);

            if (baseJson == null)
            {
                if (haveUnity) baseJson = UnityHeadlessRunner.ReadBundledSample(unity!, BundledSampleFile);
                usingBundledSample = baseJson != null;
                if (baseJson == null)
                {
                    message = "No layout to analyse yet. Import or push a layout from the Sportify web app first (Combine tab).";
                    return Result.Failed;
                }
            }

            var review = false;
            while (true)
            {
                // The values the analysis rests on that the layout cannot know: entered, accepted or left unconfirmed (which the results then say).
                var prepared = AnalysisAssumptionsDialog.Prepare(baseJson, "sun", DialogTitle, commandData.Application.MainWindowHandle, review);
                if (prepared.Cancelled) return Result.Cancelled;
                var layoutJson = prepared.Json;

                SunInputs inputs;
                try
                {
                    var layout = JsonSerializer.Deserialize<SportifyLayout>(layoutJson)
                                 ?? throw new InvalidOperationException("The layout was empty.");
                    inputs = SunLayoutAdapter.ToInputs(layout);
                }
                catch (Exception ex)
                {
                    message = $"Couldn't read the layout: {ex.Message}";
                    return Result.Failed;
                }

                if (inputs.Structure.Items.Count == 0)
                {
                    TaskDialog.Show(DialogTitle, "The layout has nothing on the roof yet (no sports field, activity, green-roof zone or tree). Place something in the Combine tab first.");
                    return Result.Cancelled;
                }

                var report = SunModel.Analyse(inputs);
                var caseStudy = SunModel.CaseStudy(inputs, report);
                AnalysisResultPublisher.PublishSunAndShading(BuildPublishedResult(report, caseStudy, null));

                var unityFree = haveUnity && !UnityHeadlessRunner.IsProjectOpenInUnity(unity!.ProjectDir);
                var choice = ShowSummary(report, caseStudy, usingBundledSample, haveUnity, unityFree);
                if (choice == SummaryChoice.Review) { review = true; continue; }
                if (choice == SummaryChoice.Video)
                {
                    var install = AnalysisMedia.EnsureUnity(DialogTitle, haveUnity ? unity : null, layoutJson, "sun_and_shading", AnalysisMedia.ProjectTitle(commandData));
                    if (install != null) RenderVideo(install, layoutJson, report, caseStudy);
                }
                else if (choice == SummaryChoice.Pdf)
                    AnalysisMedia.ExportPdf(DialogTitle, layoutJson, new[] { "sun_and_shading" }, AnalysisMedia.ProjectTitle(commandData));
                return Result.Succeeded;
            }
        }

        // ------------------------------------------------------------------ dialogs

        static SummaryChoice ShowSummary(SunReport report, string caseStudy, bool usingBundledSample, bool haveUnity, bool unityFree)
        {
            var s = report.summary;
            var body = new StringBuilder();
            if (usingBundledSample)
                body.AppendLine("No layout imported into this Revit session yet — analysed Unity's bundled roof-garden sample instead.").AppendLine();
            if (s.preliminary)
                body.AppendLine(s.preliminaryNote).AppendLine();
            body.AppendLine(caseStudy + (s.latitudeAssumed ? " (latitude assumed)" : "") + (s.northAssumed ? " (orientation assumed)" : "") + ".");
            body.AppendLine(AnalysisMedia.SeeReport);

            var dialog = new TaskDialog(DialogTitle)
            {
                MainInstruction = (s.preliminary ? "PRELIMINARY: " : "") +
                                  (s.peopleZonesTooSunny > 0 ? $"{s.peopleZonesTooSunny} people zone(s) too sunny at midday" + (report.equipment.Count > 0 ? $"; {report.equipment.Count} piece(s) of shading equipment recommended" : "")
                                  : s.gardenZonesTooShaded > 0 ? $"{s.gardenZonesTooShaded} garden(s) get less sun than they need"
                                  : "The sun and shade balance on this roof holds"),
                MainContent = body.ToString(),
                ExpandedContent = "Inputs used:\n" + AnalysisAssumptionsPatcher.DescribeInputs(report.assumptionUses),
                CommonButtons = TaskDialogCommonButtons.Close,
                DefaultButton = TaskDialogResult.Close,
            };

            AnalysisMedia.AddLinks(dialog, haveUnity, unityFree, "About two minutes.");
            dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink3, "Review the assumptions, then run again",
                s.preliminary ? "Enter the site, the orientation, the shade target and the sun the gardens need, or accept the built-in values, to lift the PRELIMINARY mark." : "Change a value, or the kind of equipment that may be recommended.");

            var result = dialog.Show();
            return result == TaskDialogResult.CommandLink3 ? SummaryChoice.Review : AnalysisMedia.Read(result);
        }

        static void RenderVideo(UnityHeadlessRunner.UnityInstall unity, string layoutJson, SunReport report, string caseStudy)
        {
            var run = AnalysisMedia.RunUnity(unity, new UnityHeadlessRunner.Request
            {
                ExecuteMethod = "Sportify.Simulation.Editor.BatchRunner.RunSunAnalysis",
                LayoutJson = layoutJson,
                ResultsFileName = "sun_results.json",
                VideoFileStem = "sun_shade",
                LogFileName = "unity_sun_batch.log",
                TimeoutMs = VideoTimeoutMs,
            }, "Rendering the sun and shade video");

            if (!run.Ok)
            {
                if (!run.Cancelled) TaskDialog.Show(DialogTitle, "The numbers above stand, but the video couldn't be rendered.\n\n" + run.Message);
                return;
            }

            SunResultsFile? results;
            try
            {
                results = JsonSerializer.Deserialize<SunResultsFile>(run.ResultsJson!, new JsonSerializerOptions { IncludeFields = true });
            }
            catch (Exception ex)
            {
                TaskDialog.Show(DialogTitle, $"The video was rendered, but Unity's results file couldn't be read: {ex.Message}");
                return;
            }

            if (results == null || !string.IsNullOrEmpty(results.Error))
            {
                TaskDialog.Show(DialogTitle, $"Unity's sun and shade analysis failed: {results?.Error ?? "empty results file"}");
                return;
            }

            var video = results.Video;
            var videoPath = video != null && !string.IsNullOrEmpty(video.FilePath) && File.Exists(video.FilePath) ? video.FilePath : null;
            if (videoPath != null) videoPath = SportifyWorkspace.Adopt("videos", videoPath);     // the workspace holds the video, not just Unity's Recordings folder
            var disagreement = results.Analysis != null ? Compare(report, results.Analysis) : "Unity's results carried no analysis to compare.";

            AnalysisResultPublisher.PublishSunAndShading(BuildPublishedResult(report, caseStudy, videoPath));

            var body = new StringBuilder();
            body.AppendLine(videoPath != null
                ? $"Video: {Path.GetFileName(videoPath)} ({video!.DurationS:0.#} s, {video.Width}×{video.Height})"
                : "Video: not recorded" + (string.IsNullOrEmpty(results.VideoError) ? "." : $" — {results.VideoError}"));
            if (disagreement != null)
                body.AppendLine().AppendLine("Note: " + disagreement + " The add-in's numbers are the ones published.");

            var dialog = new TaskDialog(DialogTitle)
            {
                MainInstruction = videoPath != null ? "The 3D video is ready" : "No video was recorded",
                MainContent = body.ToString(),
                CommonButtons = TaskDialogCommonButtons.Close,
                DefaultButton = TaskDialogResult.Close,
            };
            if (videoPath != null)
            {
                dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Play the video", videoPath);
                dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Show the video in its folder");
            }

            var choice = dialog.Show();
            if (videoPath == null) return;
            try
            {
                if (choice == TaskDialogResult.CommandLink1) Process.Start(new ProcessStartInfo(videoPath) { UseShellExecute = true });
                else if (choice == TaskDialogResult.CommandLink2) Process.Start("explorer.exe", $"/select,\"{videoPath}\"");
            }
            catch (Exception)
            {
                // Best-effort — the video is on disk either way.
            }
        }

        // ------------------------------------------------------------------ result plumbing

        internal static SunAndShadingResultDto BuildPublishedResult(SunReport report, string caseStudy, string? videoPath)
        {
            var s = report.summary;
            return new SunAndShadingResultDto
            {
                CaseStudy = caseStudy,
                VideoPath = videoPath,
                Preliminary = s.preliminary,
                PreliminaryNote = s.preliminaryNote,
                Inputs = AssumptionUseDto.From(report.assumptionUses),
                LatitudeDeg = Math.Round(s.latitudeDeg, 4),
                LatitudeAssumed = s.latitudeAssumed,
                NorthDeg = Math.Round(s.northDeg, 2),
                NorthAssumed = s.northAssumed,
                ShadeTargetPercent = Math.Round(s.shadeTargetPercent, 1),
                GardenMinSunHours = Math.Round(s.gardenMinSunHours, 2),
                PeopleZones = s.peopleZones,
                PeopleZonesTooSunny = s.peopleZonesTooSunny,
                PeopleZonesTooSunnyAfter = s.peopleZonesTooSunnyAfter,
                GardenZones = s.gardenZones,
                GardenZonesTooShaded = s.gardenZonesTooShaded,
                GardenZonesTooShadedAfter = s.gardenZonesTooShadedAfter,
                Pieces = s.pieces,
                AddedLoadKn = Math.Round(s.addedLoadKn, 1),
                PeakWindPressurePa = Math.Round(s.peakWindPressurePa, 0),
                Days = report.days.Select(d => new SunDayDto
                {
                    Name = d.name, SunriseH = Math.Round(d.sunriseH, 2), SunsetH = Math.Round(d.sunsetH, 2), NoonElevationDeg = Math.Round(d.noonElevationDeg, 1),
                    RoofMeanSunHours = Math.Round(d.roofMeanSunHours, 2), RoofMeanSunHoursAfter = Math.Round(d.roofMeanSunHoursAfter, 2),
                }).ToList(),
                Zones = report.zones.Select(z => new SunZoneDto
                {
                    Label = z.label, Kind = z.kind, AreaM2 = Math.Round(z.areaM2, 1),
                    SunHoursJune = Math.Round(z.sunHoursJune, 2), SunHoursMarch = Math.Round(z.sunHoursMarch, 2), SunHoursDecember = Math.Round(z.sunHoursDecember, 2),
                    PeakShadePercent = Math.Round(z.peakShadePercent, 1), Status = z.status, AfterSunHoursJune = Math.Round(z.afterSunHoursJune, 2),
                    AfterPeakShadePercent = Math.Round(z.afterPeakShadePercent, 1), AfterStatus = z.afterStatus,
                }).ToList(),
                Equipment = report.equipment.Select(p => new SunEquipmentDto
                {
                    Name = p.name, Key = p.key, XM = Math.Round(p.x, 2), YM = Math.Round(p.y, 2), WidthM = Math.Round(p.widthM, 2), DepthM = Math.Round(p.depthM, 2), HeightM = Math.Round(p.heightM, 2),
                    Zone = p.zoneLabel, ShadeBeforePercent = Math.Round(p.shadeBeforePercent, 1), ShadeAfterPercent = Math.Round(p.shadeAfterPercent, 1),
                    AddedLoadKn = Math.Round(p.addedLoadKn, 1), WindUpliftKn = Math.Round(p.windUpliftKn, 1), Note = p.note,
                }).ToList(),
                DeckAddedKn = Math.Round(report.structure.addedKn, 1),
                DeckPeakUtilisationBeforePercent = Math.Round(report.structure.peakUtilisationBefore * 100.0, 1),
                DeckPeakUtilisationAfterPercent = Math.Round(report.structure.peakUtilisationAfter * 100.0, 1),
                DeckBaysOverBefore = report.structure.baysOverBefore,
                DeckBaysOverAfter = report.structure.baysOverAfter,
                Findings = report.recommendations.Select(r => new WindFindingDto { Kind = r.kind, Target = r.target, Text = r.text }).ToList(),
                Assumptions = new List<string>(report.assumptions),
            };
        }

        /// <summary>Unity runs the same core on its own reading of the layout: the two must agree. Returns a description of a disagreement, or null.</summary>
        internal static string? Compare(SunReport addin, SunReport unity)
        {
            if (addin.zones.Count != unity.zones.Count || addin.days.Count != unity.days.Count)
                return "Unity's run has a different number of zones or days than the add-in's.";

            var problems = new List<string>();
            for (var i = 0; i < addin.zones.Count; i++)
            {
                if (Math.Abs(addin.zones[i].sunHoursJune - unity.zones[i].sunHoursJune) > 0.05f || Math.Abs(addin.zones[i].peakShadePercent - unity.zones[i].peakShadePercent) > 0.5f)
                    problems.Add($"{addin.zones[i].label} {addin.zones[i].sunHoursJune:0.#} h / {addin.zones[i].peakShadePercent:0}% vs {unity.zones[i].sunHoursJune:0.#} h / {unity.zones[i].peakShadePercent:0}%");
            }
            if (addin.equipment.Count != unity.equipment.Count)
                problems.Add($"pieces {addin.equipment.Count} vs {unity.equipment.Count}");
            else
                for (var i = 0; i < addin.equipment.Count; i++)
                    if (addin.equipment[i].key != unity.equipment[i].key || Math.Abs(addin.equipment[i].x - unity.equipment[i].x) > 0.05f || Math.Abs(addin.equipment[i].y - unity.equipment[i].y) > 0.05f)
                        problems.Add($"piece {i + 1}: {addin.equipment[i].key} at ({addin.equipment[i].x:0.#}, {addin.equipment[i].y:0.#}) vs {unity.equipment[i].key} at ({unity.equipment[i].x:0.#}, {unity.equipment[i].y:0.#})");
            return problems.Count == 0 ? null : "Unity's run differs from the add-in's (" + string.Join(", ", problems.Take(4)) + ").";
        }

        // ---- what Sportify.Simulation writes to Recordings/sun_results.json (camelCase, Unity JsonUtility) ----

        internal class SunVideoInfo
        {
            [JsonPropertyName("path")] public string? FilePath { get; set; }
            [JsonPropertyName("durationS")] public float DurationS { get; set; }
            [JsonPropertyName("width")] public int Width { get; set; }
            [JsonPropertyName("height")] public int Height { get; set; }
        }

        internal class SunResultsFile
        {
            [JsonPropertyName("caseStudy")] public string? CaseStudy { get; set; }
            [JsonPropertyName("message")] public string? Message { get; set; }
            [JsonPropertyName("analysis")] public SunReport? Analysis { get; set; }
            [JsonPropertyName("video")] public SunVideoInfo? Video { get; set; }
            [JsonPropertyName("videoError")] public string? VideoError { get; set; }
            [JsonPropertyName("error")] public string? Error { get; set; }
        }
    }
}
