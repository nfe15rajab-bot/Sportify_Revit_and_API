using System;
using System.Collections.Generic;
using System.Linq;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace SportfyRevit
{
    /// <summary>
    /// The PDF "template" for GenerateAnalysisReportCommand: one QuestPDF page
    /// template (header/content/footer) whose content column is built from
    /// whatever's actually available this session — roof summary, one card per
    /// published Analyze* result (a tone-coloured header, tiles, a bar against
    /// its reference where one exists, and a recommendations list built from
    /// the same Findings each command already collects), the placements
    /// schedule, and the two auto-exported diagram images. This is where an
    /// Analyze* command's numbers, charts and recommendations now live — each
    /// command's own dialog only says a result was published and points here,
    /// instead of repeating the same narrative in a popup. Every section is
    /// independently optional (missing data just means a shorter report),
    /// never a build failure. Tone colours are SvgChart's own (Ok/Warn/Bad/
    /// Neutral) so a reader sees the same palette here, on screen and in the
    /// Unity-rendered charts.
    /// </summary>
    internal static class AnalysisReportPdfBuilder
    {
        public static void Generate(
            string outputPath,
            SportifyLayout? layout,
            AnalysisResultPayload? results,
            string? circulationImagePath,
            string? axoImagePath)
        {
            QuestPDF.Settings.License = LicenseType.Community;

            Document.Create(container =>
            {
                container.Page(page =>
                {
                    page.Size(PageSizes.A4);
                    page.Margin(2, Unit.Centimetre);
                    page.DefaultTextStyle(x => x.FontSize(10).FontColor(Colors.Grey.Darken3));

                    page.Header().Column(col =>
                    {
                        col.Item().Text("Sportify — Analysis Report").FontSize(20).Bold().FontColor(Colors.Blue.Darken2);
                        col.Item().Text($"Generated {DateTime.Now:yyyy-MM-dd HH:mm}").FontSize(9).FontColor(Colors.Grey.Medium);
                        col.Item().PaddingTop(6).LineHorizontal(1).LineColor(Colors.Grey.Lighten1);
                    });

                    page.Content().PaddingTop(10).Column(col =>
                    {
                        col.Spacing(14);

                        if (layout?.RoofContext != null)
                            col.Item().Element(c => RoofSummarySection(c, layout));

                        col.Item().Element(c => AnalysisResultsSection(c, results));

                        if (layout?.Placements is { Count: > 0 })
                            col.Item().Element(c => PlacementsSection(c, layout.Placements));

                        if (circulationImagePath != null || axoImagePath != null)
                            col.Item().Element(c => DiagramsSection(c, circulationImagePath, axoImagePath));
                    });

                    page.Footer().AlignCenter().Text(x =>
                    {
                        x.Span("Sportify — school project · page ");
                        x.CurrentPageNumber();
                        x.Span(" / ");
                        x.TotalPages();
                    });
                });
            }).GeneratePdf(outputPath);
        }

        static void RoofSummarySection(IContainer container, SportifyLayout layout)
        {
            var roof = layout.RoofContext!;
            var rules = layout.DesignRules;

            container.Column(col =>
            {
                col.Item().Text("Roof Layout").FontSize(14).Bold();
                col.Item().PaddingTop(4).Row(row =>
                {
                    row.RelativeItem().Text($"Footprint: {roof.LengthM:0.#} x {roof.WidthM:0.#} m");
                    if (rules != null)
                        row.RelativeItem().Text($"Clearance {rules.ClearanceM:0.#}m · Setback {rules.BoundarySetbackM:0.#}m · Circulation {rules.CirculationWidthM:0.#}m");
                    row.RelativeItem().Text($"Entry points: {layout.EntryPoints?.Count ?? 0}");
                });
            });
        }

        // =========================================================================================== shared card building blocks

        static string ToneColor(string tone) => tone switch { "ok" => SvgChart.Ok, "warn" => SvgChart.Warn, "bad" => SvgChart.Bad, "prelim" => SvgChart.Warn, _ => SvgChart.Neutral };
        static string ToneBg(string tone) => tone switch { "ok" => "#e7f4ec", "warn" => "#fbf0dd", "bad" => "#fae4e3", "prelim" => "#fff4d6", _ => "#eef0f3" };

        /// <summary>One result card: a tone-coloured left bar and swatch stand in for the web app's icon (QuestPDF has no bundled icon font to lean
        /// on, exactly the reasoning SvgChart itself already follows), a title/subtitle, a chip on the right, then whatever the caller puts in the body.</summary>
        static void Card(ColumnDescriptor outer, string title, string? sub, string tone, string chip, Action<ColumnDescriptor> body)
        {
            // Not ShowEntire: a card's Assumptions/Recommendations/Inputs text is open-ended (whatever the analysis found), so a card can
            // legitimately be taller than one page — ShowEntire would then throw instead of just splitting it across a page break, and
            // losing the whole report over one long card is worse than an occasional split card.
            outer.Item().BorderLeft(3).BorderColor(ToneColor(tone)).Background(Colors.Grey.Lighten5).Padding(10).Column(card =>
            {
                card.Spacing(6);
                card.Item().Row(head =>
                {
                    head.ConstantItem(8).Height(8).Background(ToneColor(tone));
                    head.RelativeItem().PaddingLeft(6).Column(t =>
                    {
                        t.Item().Text(title).FontSize(12).Bold();
                        if (!string.IsNullOrEmpty(sub)) t.Item().Text(sub!).FontSize(8).FontColor(Colors.Grey.Medium);
                    });
                    head.ConstantItem(100).AlignRight().AlignMiddle().Background(ToneBg(tone)).PaddingVertical(2).PaddingHorizontal(6)
                        .AlignCenter().Text(chip.ToUpperInvariant()).FontSize(7.5f).Bold().FontColor(ToneColor(tone));
                });
                body(card);
            });
        }

        static void Tiles(ColumnDescriptor col, params (string Label, string Value, string? Sub, string? Tone)[] items)
        {
            col.Item().PaddingTop(2).Row(row =>
            {
                foreach (var it in items)
                {
                    row.RelativeItem().Background(Colors.White).Border(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(6).Column(t =>
                    {
                        t.Item().Text(it.Value).FontSize(13).Bold().FontColor(it.Tone != null ? ToneColor(it.Tone) : Colors.Grey.Darken3);
                        t.Item().Text(it.Label.ToUpperInvariant()).FontSize(6.5f).Bold().FontColor(Colors.Grey.Medium);
                        if (!string.IsNullOrEmpty(it.Sub)) t.Item().Text(it.Sub!).FontSize(7.5f).FontColor(Colors.Grey.Medium);
                    });
                }
            });
        }

        /// <summary>A value against `max`, with an optional reference-line mark (a limit, a minimum, the 100% line) drawn as a thin bar underneath.</summary>
        static void Bar(ColumnDescriptor col, string label, double value, double max, double? refValue, string tone, string valueText)
        {
            if (max <= 0) return;
            var pct = (float)Math.Max(0, Math.Min(100, value / max * 100));
            float? refPct = refValue.HasValue ? (float)Math.Max(0, Math.Min(100, refValue.Value / max * 100)) : null;

            col.Item().PaddingTop(2).Column(b =>
            {
                b.Item().Row(r =>
                {
                    r.RelativeItem().Text(label).FontSize(8);
                    r.ConstantItem(120).AlignRight().Text(valueText).FontSize(8).Bold();
                });
                b.Item().PaddingTop(2).Height(9).Row(barRow =>
                {
                    barRow.RelativeItem(pct).Background(ToneColor(tone));
                    barRow.RelativeItem(100 - pct).Background(Colors.Grey.Lighten3);
                });
                if (refPct.HasValue)
                {
                    b.Item().PaddingTop(1).Height(3).Row(refRow =>
                    {
                        refRow.RelativeItem(refPct.Value).Background(Colors.Grey.Darken2);
                        refRow.RelativeItem(100 - refPct.Value);
                    });
                }
            });
        }

        static void BulletList(ColumnDescriptor col, string heading, IEnumerable<string?>? items, int take = 6)
        {
            var list = items?.Where(s => !string.IsNullOrWhiteSpace(s)).Take(take).ToList();
            if (list == null || list.Count == 0) return;
            col.Item().PaddingTop(3).Column(rec =>
            {
                rec.Item().Text(heading).FontSize(8).Bold().FontColor(Colors.Grey.Darken2);
                foreach (var f in list) rec.Item().Text($"•  {f}").FontSize(8).FontColor(Colors.Grey.Darken3);
            });
        }

        static void Recommendations(ColumnDescriptor col, IEnumerable<WindFindingDto>? findings, Func<WindFindingDto, bool>? include = null, int take = 5)
        {
            var texts = findings?.Where(f => include == null || include(f)).Select(f => f.Text);
            BulletList(col, "Recommendations", texts, take);
        }

        static void AssumptionsNote(ColumnDescriptor col, IEnumerable<string>? assumptions)
        {
            var list = assumptions?.Where(a => !string.IsNullOrWhiteSpace(a)).ToList();
            if (list == null || list.Count == 0) return;
            col.Item().PaddingTop(3).Text("Assumptions: " + string.Join(" ", list)).FontSize(7).Italic().FontColor(Colors.Grey.Medium);
        }

        /// <summary>What each input of a structural analysis was, and whether the designer confirmed it.</summary>
        static void InputsNote(ColumnDescriptor col, IEnumerable<AssumptionUseDto>? inputs)
        {
            var list = inputs?.ToList();
            if (list == null || list.Count == 0) return;
            var text = string.Join("; ", list.Select(i =>
                $"{i.Label}: {(i.Value ?? "").Replace("m2", "m²")} ({(i.State == "entered" ? "entered by the designer" : i.State == "accepted" ? "built-in, accepted" : "built-in, NOT CONFIRMED")})"));
            col.Item().PaddingTop(3).Text("Inputs: " + text).FontSize(7).FontColor(Colors.Grey.Medium);
        }

        static void PreliminaryNote(ColumnDescriptor col, bool preliminary, string? note)
        {
            if (!preliminary) return;
            col.Item().PaddingTop(2).Background("#fff4d6").Padding(6)
                .Text("PRELIMINARY — " + (note ?? "Some inputs are built-in values the designer has not confirmed.").Replace("PRELIMINARY: ", ""))
                .FontSize(8).FontColor("#7a4b00");
        }

        static void VideoNote(ColumnDescriptor col, string? videoPath)
        {
            if (string.IsNullOrEmpty(videoPath)) return;
            col.Item().PaddingTop(2).Text("Video: " + System.IO.Path.GetFileName(videoPath)).FontSize(7.5f).FontColor(Colors.Grey.Medium);
        }

        // =========================================================================================== the results section: one card per published check

        static void AnalysisResultsSection(IContainer container, AnalysisResultPayload? r)
        {
            container.Column(col =>
            {
                col.Spacing(10);
                col.Item().Text("Analysis Results").FontSize(14).Bold();

                var any = r != null && (r.FireSafety != null || r.Accessibility != null || r.Lca != null || r.CarbonImpact != null || r.BallTrajectory != null
                    || r.WindErosion != null || r.SoilPercolation != null || r.StructuralLoads != null || r.SunAndShading != null || r.DynamicAnalysis != null);
                if (!any)
                {
                    col.Item().Text("No Algorithmic Analysis checks have been run yet this session.").Italic();
                    return;
                }

                if (r!.FireSafety is { } fs) FireSafetyCard(col, fs);
                if (r.Accessibility is { } ac) AccessibilityCard(col, ac);
                if (r.Lca is { } lca) LcaCard(col, lca);
                if (r.CarbonImpact is { } ci) CarbonImpactCard(col, ci);
                if (r.BallTrajectory is { } bt) BallTrajectoryCard(col, bt);
                if (r.WindErosion is { } we) WindErosionCard(col, we);
                if (r.SoilPercolation is { } sp) SoilPercolationCard(col, sp);
                if (r.StructuralLoads is { } sl) StructuralLoadsCard(col, sl);
                if (r.SunAndShading is { } sn) SunShadeCard(col, sn);
                if (r.DynamicAnalysis is { } da) DynamicAnalysisCard(col, da);
            });
        }

        static void FireSafetyCard(ColumnDescriptor col, FireSafetyResultDto fs)
        {
            var tone = fs.UnreachableCount > 0 ? "bad" : fs.WithinLimit ? "ok" : "warn";
            var chip = fs.UnreachableCount > 0 ? "no route" : fs.WithinLimit ? "within limit" : "over limit";
            Card(col, "Fire Safety", "Travel-distance reference: MBO §35", tone, chip, body =>
            {
                Bar(body, "Longest route to an entry point", fs.MaxDistM, Math.Max(fs.MaxDistM, fs.MaxTravelDistanceM) * 1.15, fs.MaxTravelDistanceM,
                    fs.UnreachableCount > 0 ? "bad" : tone, $"{fs.MaxDistM:0.0} m / {fs.MaxTravelDistanceM:0.#} m limit");
                if (fs.UnreachableCount > 0) Tiles(body, ("Unreachable pieces", fs.UnreachableCount.ToString(), "no route to any entry", "bad"));

                var recs = new List<string>();
                if (fs.UnreachableCount > 0) recs.Add("Add or reposition an entry point so every piece has a walkable route.");
                else if (!fs.WithinLimit) recs.Add($"Shorten the longest route by {(fs.MaxDistM - fs.MaxTravelDistanceM):0.0} m — move the piece closer to an entry, or add another entry point nearby.");
                BulletList(body, "Recommendations", recs);
            });
        }

        static void AccessibilityCard(ColumnDescriptor col, AccessibilityResultDto ac)
        {
            var tone = ac.WidthOk && ac.ReachOk ? "ok" : "warn";
            Card(col, "Accessibility", "Wheelchair two-way passage reference", tone, tone == "ok" ? "meets reference" : "check needed", body =>
            {
                Bar(body, "Circulation width", ac.CurrentWidthM, Math.Max(ac.CurrentWidthM, ac.MinWidthM) * 1.3, ac.MinWidthM,
                    ac.WidthOk ? "ok" : "bad", $"{ac.CurrentWidthM:0.0} m / {ac.MinWidthM:0.#} m reference");
                Tiles(body, ("Every piece reachable", ac.ReachOk ? "Yes" : "No", "from an entry point", ac.ReachOk ? "ok" : "bad"));

                var recs = new List<string>();
                if (!ac.WidthOk) recs.Add($"Widen circulation to at least {ac.MinWidthM:0.#} m for two-way wheelchair passage.");
                if (!ac.ReachOk) recs.Add("Add or move entry points so every piece is reachable.");
                BulletList(body, "Recommendations", recs);
                body.Item().PaddingTop(3).Text("Not covered here: tactile/contrast guidance at decision points (DIN 32984) and child-scaled equipment with fall-safety surfacing (DIN EN 1176/1177) — both need a visual walkthrough, not just the layout geometry.")
                    .FontSize(7).Italic().FontColor(Colors.Grey.Medium);
            });
        }

        static void LcaCard(ColumnDescriptor col, LcaResultDto lca)
        {
            if (lca.CoveredCount == 0)
            {
                Card(col, "LCA Estimate", null, "neutral", "no data", body =>
                {
                    body.Item().Text($"None of the {lca.TotalCount} piece(s) have both a reference material picked and embodied-carbon data filled in yet.").FontSize(8.5f);
                    BulletList(body, "Recommendations", new[] { "Pick a reference material for each piece and add missing embodied-carbon figures from the Data tab." });
                });
                return;
            }
            var tone = lca.MissingCount > 0 ? "warn" : "ok";
            Card(col, "LCA Estimate", null, tone, lca.MissingCount > 0 ? $"{lca.MissingCount} missing" : "complete", body =>
            {
                Tiles(body,
                    ("Embodied carbon", $"~{Math.Round(lca.TotalKg):#,0} kg", "CO2e, A1-A3, illustrative", null),
                    ("Pieces covered", $"{lca.CoveredCount} / {lca.TotalCount}", lca.MissingCount > 0 ? $"{lca.MissingCount} missing data" : "all covered", tone));
                if (lca.MissingCount > 0)
                    BulletList(body, "Recommendations", new[] { $"Fill in missing reference materials or embodied-carbon figures for {lca.MissingCount} piece(s) in the Data tab." });
            });
        }

        static void CarbonImpactCard(ColumnDescriptor col, CarbonImpactResultDto ci)
        {
            Card(col, "Carbon Impact", "Kinetic-energy-harvesting ceiling — illustrative", "neutral", "estimate", body =>
            {
                Tiles(body,
                    ("Estimated daily use", $"{ci.EstimatedDailyWh:0.#} Wh", $"{ci.EstimatedDailyWh / 1000:0.##} kWh/day", null),
                    ("Active surface area", $"{ci.ActiveSurfaceAreaM2:0.#} m²", null, null));
                body.Item().PaddingTop(3).Text("Assumes piezoelectric-capable flooring throughout — today's material data doesn't track that property, so this is an upper bound, not a prediction for the materials actually picked.")
                    .FontSize(7).Italic().FontColor(Colors.Grey.Medium);
            });
        }

        static void BallTrajectoryCard(ColumnDescriptor col, BallTrajectoryResultDto bt)
        {
            var tone = bt.CrossingCount == 0 ? "ok" : "warn";
            Card(col, "Ball Trajectories", bt.CaseStudy, tone, bt.CrossingCount == 0 ? "clear" : $"{bt.CrossingCount} crossing(s)", body =>
            {
                Tiles(body,
                    ("Shots simulated", bt.ShotsSimulated.ToString(), null, null),
                    ("Boundary crossings", bt.CrossingCount.ToString(), null, bt.CrossingCount == 0 ? "ok" : "warn"));

                if (bt.SweptShots > 0)
                {
                    var fenceTone = bt.PercentLeavingRoof == 0 ? "ok" : "warn";
                    Bar(body, "Stray shots leaving the roof (swept)", bt.PercentLeavingRoof, 100, null, fenceTone, $"{bt.PercentLeavingRoof:0.#}% of {bt.SweptShots}");
                    var fenceTexts = bt.Fences is { Count: > 0 }
                        ? bt.Fences.Select(f => $"{f.Edge} edge, {f.FromM:0.#}–{f.ToM:0.#} m along it, {f.HeightM:0.#} m high").ToList()
                        : new List<string> { "No fence needed at the swept percentage above." };
                    BulletList(body, "Roof-Edge Fences", fenceTexts);
                }
                VideoNote(body, bt.VideoPath);
            });
        }

        static void WindErosionCard(ColumnDescriptor col, WindErosionResultDto we)
        {
            var tone = we.TreesFailing > 0 || we.ZonesUpliftFlagged > 0 ? "bad" : we.TreesMarginal > 0 ? "warn" : "ok";
            Card(col, "Wind & Erosion", we.CaseStudy, tone, tone == "bad" ? "action needed" : tone == "warn" ? "marginal" : "holds", body =>
            {
                Tiles(body,
                    ("Wind zone", we.WindZone ?? "—", we.WindZoneSource, null),
                    ("Peak pressure", $"{we.PeakPressurePa:0} Pa", $"at {we.RoofHeightM:0.#} m" + (we.RoofHeightAssumed ? " (assumed)" : ""), null),
                    ("Trees failing", $"{we.TreesFailing} / {we.TreesChecked}", we.TreesMarginal > 0 ? $"{we.TreesMarginal} marginal" : null, we.TreesFailing > 0 ? "bad" : we.TreesMarginal > 0 ? "warn" : "ok"),
                    ("Zones lifting", $"{we.ZonesUpliftFlagged} / {we.ZonesChecked}", $"{we.PercentPlantedAreaUpliftFlagged:0.#}% of planted area", we.ZonesUpliftFlagged > 0 ? "bad" : "ok"));
                Bar(body, "Erosion risk (planted area)", we.PercentPlantedAreaErosionFlagged, 100, null, we.PercentPlantedAreaErosionFlagged > 0 ? "warn" : "ok",
                    $"{we.PercentPlantedAreaErosionFlagged:0.#}%, bare medium moves from {we.LowestBareOnsetMs:0.#} m/s");
                Recommendations(body, we.Findings);
                VideoNote(body, we.VideoPath);
                AssumptionsNote(body, we.Assumptions);
            });
        }

        static void SoilPercolationCard(ColumnDescriptor col, SoilPercolationResultDto sp)
        {
            var tone = sp.ZonesSaturatedInCloudburst > 0 || sp.ZonesBelowTarget > 0 ? "warn" : "ok";
            Card(col, "Rain & Percolation", sp.CaseStudy, tone, sp.ZonesSaturatedInCloudburst > 0 ? $"{sp.ZonesSaturatedInCloudburst} fill up" : sp.ZonesBelowTarget > 0 ? $"{sp.ZonesBelowTarget} under target" : "within target", body =>
            {
                Tiles(body,
                    ("Steady rain kept", $"{sp.SteadyRetainedPercent:0}%", "10 mm/h", null),
                    ("Heavy shower kept", $"{sp.HeavyShowerRetainedPercent:0}%", $"peak cut {sp.HeavyShowerPeakReductionPercent:0}%", null),
                    ("Cloudburst kept", $"{sp.CloudburstRetainedPercent:0}%", $"peak cut {sp.CloudburstPeakReductionPercent:0}%", sp.ZonesSaturatedInCloudburst > 0 ? "warn" : null),
                    ("Zones under target", sp.ZonesBelowTarget.ToString(), $"of {sp.ZonesChecked}", sp.ZonesBelowTarget > 0 ? "warn" : "ok"));
                Bar(body, "Cloudburst peak flow", sp.CloudburstPeakLps, Math.Max(sp.CloudburstPeakLps, sp.CloudburstReferencePeakLps) * 1.15, sp.CloudburstReferencePeakLps, "neutral",
                    $"{sp.CloudburstPeakLps:0.#} l/s / bare-roof {sp.CloudburstReferencePeakLps:0.#} l/s");
                Recommendations(body, sp.Findings);
                VideoNote(body, sp.VideoPath);
                AssumptionsNote(body, sp.Assumptions);
            });
        }

        static void StructuralLoadsCard(ColumnDescriptor col, StructuralLoadsResultDto sl)
        {
            var tone = sl.Preliminary ? "neutral" : sl.BaysOverCapacity > 0 ? "bad" : sl.BalanceStatus != "balanced" || sl.BaysMarginal > 0 ? "warn" : "ok";
            var chip = sl.Preliminary ? "preliminary" : sl.BaysOverCapacity > 0 ? $"{sl.BaysOverCapacity} over" : "within capacity";
            Card(col, "Structural Loads", sl.CaseStudy, tone, chip, body =>
            {
                PreliminaryNote(body, sl.Preliminary, sl.PreliminaryNote);
                Bar(body, $"Most loaded bay ({sl.WorstBay})", sl.PeakUtilisationPercent, Math.Max(sl.PeakUtilisationPercent, 100) * 1.1, 100,
                    sl.Preliminary ? "neutral" : sl.PeakUtilisationPercent > 100 ? "bad" : "ok",
                    $"{sl.PeakUtilisationPercent:0}% of {sl.DeckCapacityKnM2:0.#} kN/m²" + (sl.DeckCapacityAssumed ? " (assumed)" : ""));
                Tiles(body,
                    ("Permanent + imposed", $"{sl.PermanentLoadKn:0} + {sl.ImposedLoadKn:0} kN", $"{sl.RoofAreaM2:0} m², mean {sl.MeanLoadKnM2:0.0} kN/m²", null),
                    ("Bays over capacity", $"{sl.BaysOverCapacity} / {sl.BaysChecked}", sl.BaysMarginal > 0 ? $"{sl.BaysMarginal} marginal" : null, sl.BaysOverCapacity > 0 ? "bad" : "ok"),
                    ("Load balance", sl.BalanceStatus ?? "—", string.IsNullOrEmpty(sl.HeavySide) ? $"{Math.Abs(sl.LoadCentreOffsetXPercent):0.#}% / {Math.Abs(sl.LoadCentreOffsetYPercent):0.#}% off centre" : $"heavy side {sl.HeavySide}", sl.BalanceStatus == "balanced" ? "ok" : "warn"));
                Recommendations(body, sl.Findings, f => f.Kind != "grid");
                VideoNote(body, sl.VideoPath);
                InputsNote(body, sl.Inputs);
                AssumptionsNote(body, sl.Assumptions);
            });
        }

        static void SunShadeCard(ColumnDescriptor col, SunAndShadingResultDto sn)
        {
            var tone = sn.Preliminary ? "neutral" : sn.PeopleZonesTooSunnyAfter == 0 && sn.GardenZonesTooShadedAfter == 0 ? "ok" : "warn";
            var chip = sn.Preliminary ? "preliminary" : tone == "ok" ? "on target" : "check needed";
            var june = sn.Days?.FirstOrDefault();
            var sub = $"Latitude {sn.LatitudeDeg:0.#}°N{(sn.LatitudeAssumed ? " (assumed)" : "")}, roof turned {sn.NorthDeg:0}°{(sn.NorthAssumed ? " (assumed)" : "")}"
                + (june != null ? $" — 21 June: sun {june.SunriseH:0.#}–{june.SunsetH:0.#} h, roof mean {june.RoofMeanSunHours:0.#} h direct sun" : "");
            Card(col, "Sun & Shade", sub, tone, chip, body =>
            {
                PreliminaryNote(body, sn.Preliminary, sn.PreliminaryNote);
                Tiles(body,
                    ("People zones too sunny", $"{sn.PeopleZonesTooSunny} / {sn.PeopleZones}", $"target {sn.ShadeTargetPercent:0}% shade", sn.PeopleZonesTooSunny > 0 ? "warn" : "ok"),
                    ("Gardens too shaded", $"{sn.GardenZonesTooShaded} / {sn.GardenZones}", $"under {sn.GardenMinSunHours:0.#} h sun", sn.GardenZonesTooShaded > 0 ? "warn" : "ok"));
                if (sn.Pieces > 0)
                {
                    Tiles(body,
                        ("Shading equipment recommended", sn.Pieces.ToString(), $"+{sn.AddedLoadKn:0.#} kN on the deck", null),
                        ("Fixed after equipment", $"{sn.PeopleZonesTooSunnyAfter} sunny / {sn.GardenZonesTooShadedAfter} shaded", "remaining, of the above", sn.PeopleZonesTooSunnyAfter == 0 && sn.GardenZonesTooShadedAfter == 0 ? "ok" : "warn"));
                    if (sn.Equipment is { Count: > 0 })
                        BulletList(body, "Shading Equipment", sn.Equipment.Select(e =>
                            $"{e.Name} {e.WidthM:0.#}×{e.DepthM:0.#} m, {e.HeightM:0.#} m high, for {e.Zone}: shade {e.ShadeBeforePercent:0}% → {e.ShadeAfterPercent:0}%, +{e.AddedLoadKn:0.#} kN"));
                }
                Recommendations(body, sn.Findings, f => f.Kind != "equipment");
                VideoNote(body, sn.VideoPath);
                InputsNote(body, sn.Inputs);
                AssumptionsNote(body, sn.Assumptions);
            });
        }

        static void DynamicAnalysisCard(ColumnDescriptor col, DynamicAnalysisResultDto da)
        {
            var tone = da.Preliminary ? "neutral" : da.WorstCaseUtilisationPercent > 100 || da.BaysExceedingComfort > 0 ? "bad" : "ok";
            var chip = da.Preliminary ? "preliminary" : tone == "ok" ? "within limits" : "check needed";
            Card(col, "Dynamic Analysis", da.CaseStudy, tone, chip, body =>
            {
                PreliminaryNote(body, da.Preliminary, da.PreliminaryNote);

                body.Item().PaddingTop(2).Text("Crowds").FontSize(9).Bold();
                Tiles(body,
                    ("Peak crowd", $"{da.PeakPersons:0} people", $"at {da.PeakAtHour:0.0} h, \"{da.Schedule}\"", null),
                    ("Peak crowd load", $"{da.PeakCrowdKn:0} kN", $"{da.CrowdShareOfLoadPercent:0.#}% of the load", null),
                    ("Busiest bay", da.BusiestBay ?? "—", $"{da.BusiestBayPeakDensity:0.00} people/m²", null));

                body.Item().PaddingTop(4).Text("Weather").FontSize(9).Bold();
                Bar(body, $"Governing case \"{da.WorstCase}\" ({da.WorstCaseBay})", da.WorstCaseUtilisationPercent, Math.Max(da.WorstCaseUtilisationPercent, 100) * 1.1, 100,
                    da.Preliminary ? "neutral" : da.WorstCaseUtilisationPercent > 100 ? "bad" : "ok", $"{da.WorstCaseUtilisationPercent:0}% of {da.DeckCapacityKnM2:0.#} kN/m²");
                Tiles(body,
                    ("Snow", $"{da.SnowSkKnM2:0.00} kN/m²", $"zone {da.SnowZone}" + (da.SnowAssumed ? " (assumed)" : ""), null),
                    ("Cloudburst water", $"+{da.RainPeakAddedKn:0} kN", $"saturated {da.RainSaturatedKn:0} kN", null));
                if (da.Cases is { Count: > 0 })
                    BulletList(body, "Load Cases", da.Cases.Select(c => $"{c.Name}: {c.PeakUtilisationPercent:0}% ({c.BaysOverCapacity} bay(s) over)"));

                body.Item().PaddingTop(4).Text("Resonance").FontSize(9).Bold();
                Bar(body, $"Worst case — {da.WorstResonanceBay}, {da.WorstResonanceActivity}", da.WorstAccelerationG, Math.Max(da.WorstAccelerationG, da.WorstLimitG) * 1.15, da.WorstLimitG,
                    da.BaysExceedingComfort > 0 ? "bad" : "ok", $"{da.WorstAccelerationG:0.000} g / {da.WorstLimitG:0.00} g limit");
                Tiles(body,
                    ("Deck frequency", $"{da.LowestFrequencyHz:0.0}–{da.HighestFrequencyHz:0.0} Hz", da.FrequencyEstimated ? "estimated from the spans" : "given", null),
                    ("Bays exceeding comfort", $"{da.BaysExceedingComfort} / {da.BaysChecked}", null, da.BaysExceedingComfort > 0 ? "bad" : "ok"));

                Recommendations(body, da.Findings);
                VideoNote(body, da.VideoPath);
                InputsNote(body, da.Inputs);
                AssumptionsNote(body, da.Assumptions);
            });
        }

        // =========================================================================================== placements & diagrams (unchanged)

        static void PlacementsSection(IContainer container, List<PlacementDto> items)
        {
            container.Column(col =>
            {
                col.Item().Text("Component Schedule").FontSize(14).Bold();
                col.Item().PaddingTop(4).Table(table =>
                {
                    table.ColumnsDefinition(columns =>
                    {
                        columns.RelativeColumn(2);
                        columns.RelativeColumn(3);
                        columns.RelativeColumn(2);
                        columns.RelativeColumn(3);
                        columns.RelativeColumn(1);
                    });

                    table.Header(header =>
                    {
                        header.Cell().Element(HeaderCell).Text("Category");
                        header.Cell().Element(HeaderCell).Text("Label");
                        header.Cell().Element(HeaderCell).Text("Quality");
                        header.Cell().Element(HeaderCell).Text("Reference Material");
                        header.Cell().Element(HeaderCell).Text("Area m²");
                    });

                    foreach (var it in items)
                    {
                        var bb = it.BoundingBox;
                        double area = bb != null ? bb.WidthM * bb.HeightM : 0;
                        table.Cell().Element(BodyCell).Text(it.Category ?? "—");
                        table.Cell().Element(BodyCell).Text(it.Label ?? "—");
                        table.Cell().Element(BodyCell).Text(PlacementDataHelpers.GetQualityLevel(it) ?? "—");
                        table.Cell().Element(BodyCell).Text(PlacementDataHelpers.GetReferenceMaterialName(it) ?? "—");
                        table.Cell().Element(BodyCell).Text(area.ToString("0.0"));
                    }
                });
            });

            static IContainer HeaderCell(IContainer c) => c.DefaultTextStyle(x => x.Bold().FontSize(9)).PaddingVertical(4).BorderBottom(1).BorderColor(Colors.Grey.Darken1);
            static IContainer BodyCell(IContainer c) => c.DefaultTextStyle(x => x.FontSize(9)).PaddingVertical(3).BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2);
        }

        static void DiagramsSection(IContainer container, string? circulationImagePath, string? axoImagePath)
        {
            container.Column(col =>
            {
                col.Item().Text("Diagrams").FontSize(14).Bold();
                col.Item().PaddingTop(4).Row(row =>
                {
                    if (circulationImagePath != null)
                    {
                        row.RelativeItem().Column(c =>
                        {
                            c.Item().Text("Circulation Diagram").FontSize(9).Bold();
                            c.Item().PaddingTop(2).Image(circulationImagePath).FitWidth();
                        });
                    }
                    if (axoImagePath != null)
                    {
                        row.RelativeItem().Column(c =>
                        {
                            c.Item().Text("Massing Axonometric").FontSize(9).Bold();
                            c.Item().PaddingTop(2).Image(axoImagePath).FitWidth();
                        });
                    }
                });
            });
        }
    }
}
