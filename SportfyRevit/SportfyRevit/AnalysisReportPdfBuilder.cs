using System;
using System.Collections.Generic;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace SportfyRevit
{
    /// <summary>
    /// The PDF "template" for GenerateAnalysisReportCommand: one QuestPDF page
    /// template (header/content/footer) whose content column is built from
    /// whatever's actually available this session — roof summary, published
    /// Analyze* results as a table, a couple of real bar-chart comparisons for
    /// the "current vs reference" checks, the placements schedule, and the two
    /// auto-exported diagram images. Every section is independently optional
    /// (missing data just means a shorter report), never a build failure.
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

            var resultRows = BuildResultRows(results);
            var chartRows = BuildChartRows(results);

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
                        col.Spacing(16);

                        if (layout?.RoofContext != null)
                            col.Item().Element(c => RoofSummarySection(c, layout));

                        col.Item().Element(c => AnalysisResultsSection(c, resultRows));

                        if (chartRows.Count > 0)
                            col.Item().Element(c => ChartsSection(c, chartRows));

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

        record ResultRow(string Name, string Detail, bool? Ok);

        static List<ResultRow> BuildResultRows(AnalysisResultPayload? r)
        {
            var rows = new List<ResultRow>();
            if (r == null) return rows;

            if (r.FireSafety is { } fs)
                rows.Add(new ResultRow("Fire Safety", $"{fs.MaxDistM:0.0} m travel distance (ref {fs.MaxTravelDistanceM:0.#} m), {fs.UnreachableCount} unreachable piece(s)", fs.WithinLimit && fs.UnreachableCount == 0));
            if (r.Accessibility is { } ac)
                rows.Add(new ResultRow("Accessibility", $"Circulation width {ac.CurrentWidthM:0.0} m (min {ac.MinWidthM:0.#} m)", ac.WidthOk && ac.ReachOk));
            if (r.WaterManagement is { } wm)
                rows.Add(new ResultRow("Water Management", $"{wm.TotalAreaM2:0.#} m² garden, {wm.AvgDepthCm:0} cm buildup, ~{wm.RetentionPercent:0}% retention", null));
            if (r.Lca is { } lca)
                rows.Add(new ResultRow("LCA", $"~{lca.TotalKg:0.#} kg CO2e ({lca.CoveredCount}/{lca.TotalCount} pieces with a reference material)", lca.TotalCount == 0 ? null : lca.CoveredCount == lca.TotalCount));
            if (r.LiveLoads is { } ll)
                rows.Add(new ResultRow("Live Loads", $"{ll.WorstCaseKnPerM2:0.00} kN/m² worst case (ref {ll.ReferenceKnPerM2:0.#} kN/m²)", ll.WithinReference));
            if (r.CarbonImpact is { } ci)
                rows.Add(new ResultRow("Carbon Impact", $"~{ci.EstimatedDailyWh:0.#} Wh/day over {ci.ActiveSurfaceAreaM2:0.#} m² active surface", null));
            if (r.SunAndShading is { } ss)
                rows.Add(new ResultRow("Sun & Shading", $"Configured for {ss.ConfiguredForDateTime ?? "—"}", ss.LocationConfigured));
            if (r.BallTrajectory is { } bt)
            {
                rows.Add(new ResultRow("Ball Trajectories",
                    $"{bt.ShotsSimulated} stray shots simulated, {bt.CrossingCount} boundary crossing(s)" +
                    (string.IsNullOrEmpty(bt.VideoPath) ? "" : $" — video: {System.IO.Path.GetFileName(bt.VideoPath)}"),
                    bt.CrossingCount == 0));

                if (bt.SweptShots > 0)
                {
                    var fences = bt.Fences is { Count: > 0 }
                        ? "fences: " + string.Join("; ", bt.Fences.Select(f => $"{f.Edge} edge {f.FromM:0.#}–{f.ToM:0.#} m × {f.HeightM:0.#} m"))
                        : "no fence needed";
                    rows.Add(new ResultRow("Roof-Edge Fences",
                        $"{bt.PercentLeavingRoof:0.#}% of {bt.SweptShots} swept stray shots leave the roof — {fences}",
                        bt.PercentLeavingRoof == 0));
                }
            }

            if (r.WindErosion is { } we)
            {
                rows.Add(new ResultRow("Wind & Erosion",
                    $"{we.TreesFailing} of {we.TreesChecked} trees would be blown over, {we.ZonesUpliftFlagged} of {we.ZonesChecked} zones lift " +
                    $"({we.PercentPlantedAreaUpliftFlagged:0.#}% of the planted area); bare substrate moves from {we.LowestBareOnsetMs:0.#} m/s" +
                    (string.IsNullOrEmpty(we.VideoPath) ? "" : $" — video: {System.IO.Path.GetFileName(we.VideoPath)}"),
                    we.TreesFailing == 0 && we.ZonesUpliftFlagged == 0));

                if (we.Findings is { Count: > 0 })
                {
                    rows.Add(new ResultRow("Wind & Erosion Fixes",
                        string.Join(" ", we.Findings.Take(4).Select(f => f.Text)) +
                        (we.Findings.Count > 4 ? $" (+{we.Findings.Count - 4} more)" : "") +
                        " Screening estimate, not a structural design.",
                        null));
                }

                if (we.Assumptions is { Count: > 0 })
                    rows.Add(new ResultRow("Wind & Erosion Assumptions", string.Join(" ", we.Assumptions), null));
            }

            if (r.SoilPercolation is { } sp)
            {
                rows.Add(new ResultRow("Rain & Percolation",
                    $"Roof keeps {sp.SteadyRetainedPercent:0}% / {sp.HeavyShowerRetainedPercent:0}% / {sp.CloudburstRetainedPercent:0}% of a steady rain / heavy shower / cloudburst; " +
                    $"cloudburst peak {sp.CloudburstPeakLps:0.#} l/s against {sp.CloudburstReferencePeakLps:0.#} l/s with no green layers; " +
                    $"{sp.ZonesBelowTarget} of {sp.ZonesChecked} build-ups keep under the 60% aim in a heavy shower" +
                    (string.IsNullOrEmpty(sp.VideoPath) ? "" : $" — video: {System.IO.Path.GetFileName(sp.VideoPath)}"),
                    sp.ZonesBelowTarget == 0));

                if (sp.Findings is { Count: > 0 })
                    rows.Add(new ResultRow("Rain & Percolation Fixes", string.Join(" ", sp.Findings.Take(4).Select(f => f.Text)) + " Screening estimate, not a hydrological design.", null));

                if (sp.Assumptions is { Count: > 0 })
                    rows.Add(new ResultRow("Rain & Percolation Assumptions", string.Join(" ", sp.Assumptions), null));
            }

            if (r.StructuralLoads is { } sl)
            {
                rows.Add(new ResultRow("Structural Loads",
                    $"{sl.PermanentLoadKn:0} kN permanent + {sl.ImposedLoadKn:0} kN imposed on {sl.RoofAreaM2:0} m² ({sl.MeanLoadKnM2:0.0} kN/m² mean); " +
                    $"most loaded bay {sl.WorstBay} at {sl.PeakUtilisationPercent:0}% of the {sl.DeckCapacityKnM2:0.#} kN/m² deck capacity" + (sl.DeckCapacityAssumed ? " (placeholder capacity)" : "") + "; " +
                    $"{sl.BaysOverCapacity} of {sl.BaysChecked} bays over, {sl.BaysMarginal} marginal; load centre {Math.Abs(sl.LoadCentreOffsetXPercent):0.#}% (length) / {Math.Abs(sl.LoadCentreOffsetYPercent):0.#}% (width) off the structure's centre: {sl.BalanceStatus}" +
                    (string.IsNullOrEmpty(sl.HeavySide) ? "" : $", heavy side {sl.HeavySide}") +
                    (string.IsNullOrEmpty(sl.VideoPath) ? "" : $" — video: {System.IO.Path.GetFileName(sl.VideoPath)}"),
                    sl.BaysOverCapacity == 0 && sl.BalanceStatus == "balanced"));

                if (sl.Findings is { Count: > 0 })
                    rows.Add(new ResultRow("Structural Loads Advice", string.Join(" ", sl.Findings.Where(f => f.Kind != "grid").Take(5).Select(f => f.Text)) + " Screening estimate, not a structural verification.", null));

                if (sl.Assumptions is { Count: > 0 })
                    rows.Add(new ResultRow("Structural Loads Assumptions", string.Join(" ", sl.Assumptions), null));
            }

            if (r.DynamicAnalysis is { } da)
            {
                rows.Add(new ResultRow("Dynamic: Crowds",
                    $"Day \"{da.Schedule}\": busiest at {da.PeakAtHour:0.0} h with about {da.PeakPersons:0} people ({da.PeakCrowdKn:0} kN, {da.CrowdShareOfLoadPercent:0.#}% of the load); most crowded bay {da.BusiestBay} at {da.BusiestBayPeakDensity:0.00} people/m²; " +
                    $"the crowd moves the load's centre by at most {da.MaxLoadCentreShiftPercent:0.##}%" + (string.IsNullOrEmpty(da.VideoPath) ? "" : $" — video: {System.IO.Path.GetFileName(da.VideoPath)}"),
                    null));
                rows.Add(new ResultRow("Dynamic: Weather",
                    $"Governing case \"{da.WorstCase}\" in {da.WorstCaseBay} at {da.WorstCaseUtilisationPercent:0}% of the {da.DeckCapacityKnM2:0.#} kN/m² deck capacity; snow zone {da.SnowZone}{(da.SnowAssumed ? " (assumed)" : "")} sk {da.SnowSkKnM2:0.00} kN/m²; " +
                    $"a cloudburst adds up to {da.RainPeakAddedKn:0} kN of water (saturated: {da.RainSaturatedKn:0} kN)" +
                    (da.Cases is { Count: > 0 } ? "; " + string.Join(", ", da.Cases.Select(c => $"{c.Name} {c.PeakUtilisationPercent:0}% ({c.BaysOverCapacity} over)")) : ""),
                    da.WorstCaseUtilisationPercent <= 100));
                rows.Add(new ResultRow("Dynamic: Resonance",
                    $"Deck frequency {da.LowestFrequencyHz:0.0} to {da.HighestFrequencyHz:0.0} Hz ({(da.FrequencyEstimated ? "ESTIMATED from the spans" : "given")}); worst {da.WorstResonanceBay} under {da.WorstResonanceActivity}: {da.WorstAccelerationG:0.000} g against {da.WorstLimitG:0.00} g; " +
                    $"{da.BaysExceedingComfort} of {da.BaysChecked} bays exceed the comfort limit under some activity",
                    da.BaysExceedingComfort == 0));

                if (da.Findings is { Count: > 0 })
                    rows.Add(new ResultRow("Dynamic Analysis Advice", string.Join(" ", da.Findings.Take(6).Select(f => f.Text)) + " Screening estimate, not a structural verification or a vibration design.", null));

                if (da.Assumptions is { Count: > 0 })
                    rows.Add(new ResultRow("Dynamic Analysis Assumptions", string.Join(" ", da.Assumptions), null));
            }

            return rows;
        }

        static void AnalysisResultsSection(IContainer container, List<ResultRow> rows)
        {
            container.Column(col =>
            {
                col.Item().Text("Analysis Results").FontSize(14).Bold();

                if (rows.Count == 0)
                {
                    col.Item().PaddingTop(4).Text("No Analysis panel checks have been run yet this session.").Italic();
                    return;
                }

                col.Item().PaddingTop(4).Table(table =>
                {
                    table.ColumnsDefinition(columns =>
                    {
                        columns.RelativeColumn(2);
                        columns.RelativeColumn(4);
                        columns.RelativeColumn(1);
                    });

                    table.Header(header =>
                    {
                        header.Cell().Element(HeaderCell).Text("Check");
                        header.Cell().Element(HeaderCell).Text("Result");
                        header.Cell().Element(HeaderCell).Text("Status");
                    });

                    foreach (var row in rows)
                    {
                        table.Cell().Element(BodyCell).Text(row.Name).Bold();
                        table.Cell().Element(BodyCell).Text(row.Detail);
                        table.Cell().Element(BodyCell).Text(row.Ok == null ? "—" : row.Ok.Value ? "OK" : "CHECK")
                            .FontColor(row.Ok == null ? Colors.Grey.Medium : row.Ok.Value ? Colors.Green.Darken1 : Colors.Red.Darken1)
                            .Bold();
                    }
                });
            });

            static IContainer HeaderCell(IContainer c) => c.DefaultTextStyle(x => x.Bold()).PaddingVertical(4).BorderBottom(1).BorderColor(Colors.Grey.Darken1);
            static IContainer BodyCell(IContainer c) => c.PaddingVertical(4).BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2);
        }

        /// <summary>
        /// HigherIsBetter distinguishes a minimum-required reference (more is
        /// fine, e.g. circulation width) from a maximum-allowed one (more is
        /// the problem, e.g. travel distance or load) — without it, coloring
        /// by "value > reference = red" is backwards for the minimum case.
        /// </summary>
        record ChartRow(string Label, double Value, double Reference, string Unit, bool HigherIsBetter);

        static List<ChartRow> BuildChartRows(AnalysisResultPayload? r)
        {
            var rows = new List<ChartRow>();
            if (r == null) return rows;
            if (r.FireSafety is { } fs) rows.Add(new ChartRow("Fire Safety — travel distance", fs.MaxDistM, fs.MaxTravelDistanceM, "m", HigherIsBetter: false));
            if (r.Accessibility is { } ac) rows.Add(new ChartRow("Accessibility — circulation width", ac.CurrentWidthM, ac.MinWidthM, "m", HigherIsBetter: true));
            if (r.LiveLoads is { } ll) rows.Add(new ChartRow("Live Loads — worst case", ll.WorstCaseKnPerM2, ll.ReferenceKnPerM2, "kN/m²", HigherIsBetter: false));
            return rows;
        }

        static void ChartsSection(IContainer container, List<ChartRow> rows)
        {
            container.Column(col =>
            {
                col.Spacing(10);
                col.Item().Text("Key Comparisons (value vs. reference)").FontSize(14).Bold();

                foreach (var r in rows)
                {
                    double max = Math.Max(r.Value, r.Reference) * 1.15;
                    if (max <= 0) continue;
                    var valuePct = (float)(r.Value / max * 100);
                    var refPct = (float)(r.Reference / max * 100);
                    var isProblem = r.HigherIsBetter ? r.Value < r.Reference : r.Value > r.Reference;

                    col.Item().Column(inner =>
                    {
                        inner.Item().Text($"{r.Label}: {r.Value:0.##} {r.Unit} (ref {r.Reference:0.##} {r.Unit})").FontSize(9);
                        inner.Item().PaddingTop(2).Height(12).Row(barRow =>
                        {
                            barRow.RelativeItem(valuePct).Background(isProblem ? Colors.Red.Medium : Colors.Green.Medium);
                            barRow.RelativeItem(100 - valuePct).Background(Colors.Grey.Lighten3);
                        });
                        inner.Item().PaddingTop(1).Height(4).Row(refRow =>
                        {
                            refRow.RelativeItem(refPct).Background(Colors.Grey.Darken2);
                            refRow.RelativeItem(100 - refPct);
                        });
                    });
                }
            });
        }

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
