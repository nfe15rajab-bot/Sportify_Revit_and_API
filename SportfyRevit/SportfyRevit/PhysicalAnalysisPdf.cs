using System.Globalization;
using System.IO;
using System.Text.Json;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using Sportify.Simulation.Dynamics;
using Sportify.Simulation.Structure;
using Sportify.Simulation.Sun;
using Sportify.Simulation.Water;
using Sportify.Simulation.Wind;

namespace SportfyRevit
{
    internal sealed class PdfChart
    {
        public string Svg = "";
        public float Aspect = 2.5f;
        public string Caption = "";
    }

    internal sealed class PdfSection
    {
        public string Key = "", Title = "", Headline = "", CaseStudy = "";
        public bool Preliminary;
        public string PreliminaryNote = "";
        public List<(string Label, string Value)> Facts = new();
        public List<PdfChart> Charts = new();
        public List<string> Findings = new();
        public List<(string Label, string Value, string State)> Inputs = new();
        public List<string> Assumptions = new();
    }

    internal sealed class PdfExport
    {
        public string? Path;
        public List<PdfSection> Sections = new();
        public List<(string Title, string Reason)> Skipped = new();
        public string? Error;
        public bool Ok => Path != null && Error == null;
    }

    /// <summary>
    /// The physical analyses as a PDF of charts, for a person with no Unity: the same numbers the 3D videos and the web app show (wind and erosion, rain and soil
    /// percolation, static loads, dynamic analysis, sun and shade), drawn as bay plans, bars and curves and written to a dedicated folder. Nothing here needs Unity or
    /// Revit: the cores are the ones the add-in and Unity share, and the charts are plain SVG that QuestPDF lays onto the pages.
    /// Revit-free on purpose, so Tools/ContractCheck can build it on the fixtures.
    /// </summary>
    internal static class PhysicalAnalysisPdf
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        static string F(double v, string f = "0.#") => v.ToString(f, Inv);
        static string Pct(double ratio) => (ratio * 100).ToString("0", Inv) + "%";

        public static readonly string[] AllKeys = { "structural_loads", "dynamic_analysis", "wind_erosion", "soil_percolation", "sun_and_shading" };

        /// <summary>Where the PDFs go: the "Physical analysis" folder of the workspace (by default Documents\Sportify\Physical analysis).</summary>
        public static string DefaultFolder() => SportifyWorkspace.PathFor("analysis");

        /// <summary>Builds the sections and writes the PDF. <paramref name="only"/> limits it to some analyses (by key); null = all five.</summary>
        public static PdfExport Export(string layoutJson, string outputFolder, string projectName, IEnumerable<string>? only = null)
        {
            var result = Build(layoutJson, only);
            if (result.Sections.Count == 0)
            {
                result.Error = "There is nothing to draw: " + string.Join(" ", result.Skipped.Select(s => s.Title + ": " + s.Reason));
                return result;
            }
            try
            {
                Directory.CreateDirectory(outputFolder);
                var stem = only != null && only.Count() == 1 ? result.Sections[0].Key : "physical_analysis";
                var safeName = string.Concat((projectName ?? "").Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_')).Trim();
                var file = Path.Combine(outputFolder, $"Sportify_{stem}{(safeName.Length > 0 ? "_" + safeName : "")}_{DateTime.Now:yyyyMMdd_HHmmss}.pdf");
                Render(result, projectName ?? "").GeneratePdf(file);
                result.Path = file;
            }
            catch (Exception ex)
            {
                result.Error = "The PDF could not be written: " + ex.Message;
            }
            return result;
        }

        /// <summary>The pages as PNG images, for looking at the report without a PDF reader (and for the tests to count pages).</summary>
        internal static IEnumerable<byte[]> PagesAsImages(PdfExport export, string projectName) => Render(export, projectName).GenerateImages();

        /// <summary>The sections only (no file): what each analysis gives, or why it gives nothing.</summary>
        public static PdfExport Build(string layoutJson, IEnumerable<string>? only = null)
        {
            var result = new PdfExport();
            SportifyLayout layout;
            try { layout = JsonSerializer.Deserialize<SportifyLayout>(layoutJson) ?? throw new InvalidOperationException("The layout was empty."); }
            catch (Exception ex)
            {
                result.Skipped.Add(("Layout", "could not be read: " + ex.Message));
                return result;
            }

            var wanted = new HashSet<string>(only ?? AllKeys);
            void Try(string key, string title, Func<PdfSection?> build)
            {
                if (!wanted.Contains(key)) return;
                try
                {
                    var section = build();
                    if (section == null) return;
                    section.Key = key; section.Title = title;
                    result.Sections.Add(section);
                }
                catch (Exception ex) { result.Skipped.Add((title, "could not be computed: " + ex.Message)); }
            }
            PdfSection? Skip(string title, string why) { result.Skipped.Add((title, why)); return null; }

            Try("structural_loads", "Structural loads", () =>
            {
                var inputs = StructureLayoutAdapter.ToInputs(layout);
                if (inputs.Items.Count == 0) return Skip("Structural loads", "nothing on the roof weighs anything yet (no field, activity, green-roof zone or tree)");
                return Structure(inputs, StructureModel.Analyse(inputs));
            });
            Try("dynamic_analysis", "Dynamic analysis", () =>
            {
                var inputs = DynamicLayoutAdapter.ToInputs(layout);
                if (!inputs.Structure.Items.Any(i => i.Persons > 0)) return Skip("Dynamic analysis", "nowhere for people to be yet (no field, activity or accessible garden)");
                var report = DynamicModel.Analyse(inputs);
                return Dynamic(report, DynamicModel.CaseStudy(inputs, report));
            });
            Try("wind_erosion", "Wind and erosion", () =>
            {
                var inputs = WindLayoutAdapter.ToInputs(layout);
                if (inputs.Zones.Count == 0 && inputs.Plants.Count == 0) return Skip("Wind and erosion", "no green-roof zones or plants in the layout");
                return Wind(inputs, WindModel.Analyse(inputs), WindModel.CaseStudy(inputs));
            });
            Try("soil_percolation", "Rain and soil percolation", () =>
            {
                var wind = WindLayoutAdapter.ToInputs(layout);
                var inputs = new WaterInputs { RoofLength = wind.RoofLength, RoofWidth = wind.RoofWidth, RoofAreaM2 = wind.Shape.Area, Zones = wind.Zones };
                if (inputs.Zones.Count == 0) return Skip("Rain and soil percolation", "no green-roof zones in the layout");
                return Rain(PercolationModel.Analyse(inputs), SimulateSoilPercolationCommand.CaseStudy(inputs));
            });
            Try("sun_and_shading", "Sun and shade", () =>
            {
                var inputs = SunLayoutAdapter.ToInputs(layout);
                if (inputs.Structure.Items.Count == 0) return Skip("Sun and shade", "nothing on the roof yet (no field, activity, green-roof zone or tree)");
                var report = SunModel.Analyse(inputs);
                return Sun(inputs, report, SunModel.CaseStudy(inputs, report));
            });

            result.Sections.Sort((a, b) => Array.IndexOf(AllKeys, a.Key).CompareTo(Array.IndexOf(AllKeys, b.Key)));
            return result;
        }

        // ------------------------------------------------------------------------------------------------------------ the analyses

        static void Uses(PdfSection s, IEnumerable<AssumptionUse> uses)
        {
            foreach (var u in uses) s.Inputs.Add((u.label, u.value, u.state));
        }

        static PdfSection Structure(StructureInputs inputs, StructureReport r)
        {
            var sum = r.summary;
            var s = new PdfSection
            {
                CaseStudy = StructureModel.CaseStudy(inputs, r), Preliminary = sum.preliminary, PreliminaryNote = sum.preliminaryNote,
                Headline = $"{sum.baysOver} of {sum.baysChecked} bays over the deck capacity; the most loaded ({sum.worstBay}) at {Pct(sum.peakUtilisation)}",
            };
            s.Facts.Add(("Roof area", F(sum.roofAreaM2, "0") + " m²"));
            s.Facts.Add(("Permanent load", F(sum.deadKn, "0") + " kN"));
            s.Facts.Add(("Imposed load", F(sum.liveKn, "0") + " kN"));
            s.Facts.Add(("Mean / most loaded bay", $"{F(sum.meanKnM2, "0.0")} / {F(sum.peakBayKnM2, "0.0")} kN/m²"));
            s.Facts.Add(("Deck capacity", F(sum.capacityKnM2) + " kN/m²" + (!sum.capacityAssumed ? " (entered)" : sum.capacityAccepted ? " (built-in value, accepted)" : " (placeholder, not confirmed)")));
            s.Facts.Add(("Bays over / marginal / checked", $"{sum.baysOver} / {sum.baysMarginal} / {sum.baysChecked}"));
            s.Facts.Add(("Columns taking much more than average", sum.columnsChecked > 0 ? $"{sum.columnsHigh} of {sum.columnsChecked}" : "no columns in the layout"));
            s.Facts.Add(("People expected", F(sum.expectedPersons, "0")));
            s.Facts.Add(("Balance", $"{r.balance.status}" + (string.IsNullOrEmpty(r.balance.heavySide) ? "" : ", heavy on the " + r.balance.heavySide) + $" ({F(r.balance.leftSharePercent, "0")}% left / {F(r.balance.rightSharePercent, "0")}% right)"));

            var shapes = new List<PlanShape>();
            foreach (var b in r.bays)
            {
                var sh = new PlanShape { Fill = SvgChart.ForStatus(b.status), Label = Pct(b.utilisation) };
                if (b.polygon != null && b.polygon.Length >= 6)
                {
                    sh.Kind = "poly";
                    for (var i = 0; i + 1 < b.polygon.Length; i += 2) sh.Points.Add(new double[] { b.polygon[i], b.polygon[i + 1] });
                }
                else { sh.X = b.x0; sh.Y = b.y0; sh.W = b.x1 - b.x0; sh.H = b.y1 - b.y0; }
                shapes.Add(sh);
            }
            foreach (var c in r.columns) shapes.Add(new PlanShape { Kind = "circle", X = c.x, Y = c.y, W = 0.6, Fill = SvgChart.Ink, FillOpacity = 1 });
            s.Charts.Add(new PdfChart
            {
                Svg = SvgChart.Plan("Bay utilisation: load against the deck capacity", inputs.RoofLength, inputs.RoofWidth, inputs.Outline, shapes,
                    new[] { ("ok", SvgChart.Ok), ("marginal", SvgChart.Warn), ("over capacity", SvgChart.Bad) }),
                Aspect = SvgChart.PlanAspect(inputs.RoofLength, inputs.RoofWidth),
                Caption = "Each bay of the structural grid, coloured by how much of the deck capacity its load uses (permanent plus imposed). Dots are columns." + (r.summary.gridAssumed ? " The grid is an assumed one: no grid came from the model." : ""),
            });

            var bayLabels = r.bays.Select(b => b.label ?? "").ToList();
            var load = new ChartSeries { Name = "kN/m²", Y = r.bays.Select(b => (double)b.totalKnM2).ToList() };
            s.Charts.Add(new PdfChart
            {
                Svg = SvgChart.Bars("Load per bay", "kN/m²", bayLabels, new[] { load }, r.bays.Select(b => SvgChart.ForStatus(b.status)).ToList(), sum.capacityKnM2, "deck capacity " + F(sum.capacityKnM2) + (sum.capacityAssumed && !sum.capacityAccepted ? " (placeholder)" : "")),
                Aspect = 520f / 190f, Caption = "Permanent plus imposed load of every bay, against the deck capacity (dashed).",
            });

            var heavy = r.items.OrderByDescending(i => i.deadKn + i.liveKn).Take(10).ToList();
            if (heavy.Count > 1)
                s.Charts.Add(new PdfChart
                {
                    Svg = SvgChart.Bars("What weighs most", "kN", heavy.Select(i => i.label ?? i.id ?? "").ToList(),
                        new[] { new ChartSeries { Name = "permanent", Color = SvgChart.Neutral, Y = heavy.Select(i => (double)i.deadKn).ToList() }, new ChartSeries { Name = "imposed", Color = SvgChart.Blue, Y = heavy.Select(i => (double)i.liveKn).ToList() } }),
                    Aspect = 520f / 190f, Caption = "The pieces that put the most load on the roof: their permanent and imposed load.",
                });

            s.Findings.AddRange(r.recommendations.Where(x => x.kind != "fine").Select(x => x.text));
            Uses(s, r.assumptionUses);
            s.Assumptions.AddRange(r.assumptions);
            return s;
        }

        static PdfSection Dynamic(DynamicReport r, string caseStudy)
        {
            var sum = r.summary;
            var s = new PdfSection
            {
                CaseStudy = caseStudy, Preliminary = sum.preliminary, PreliminaryNote = sum.preliminaryNote,
                Headline = $"crowds peak at about {F(sum.peakPersons, "0")} people around {F(sum.peakAtHour, "0")} h; the governing case is {sum.worstCase} in {sum.worstCaseBay} at {Pct(sum.worstCaseUtilisation)} of the deck capacity",
            };
            s.Facts.Add(("Day", (r.crowd.scheduleName ?? r.crowd.schedule) + $" ({F(r.crowd.startHour, "0")}–{F(r.crowd.endHour, "0")} h)"));
            s.Facts.Add(("Peak crowd", $"{F(sum.peakPersons, "0")} people at {F(sum.peakAtHour, "0.0")} h, {F(r.crowd.peakCrowdKn, "0")} kN"));
            s.Facts.Add(("Crowd's share of the load", F(sum.crowdSharePercent, "0") + "%"));
            s.Facts.Add(("Most the crowd moves the load's centre", F(sum.maxShiftPercent, "0.0") + "% of the roof"));
            s.Facts.Add(("Snow", $"characteristic {F(sum.skKnM2, "0.00")} kN/m² on the ground" + (sum.snowAssumed ? " (zone assumed)" : "")));
            s.Facts.Add(("Governing load case", $"{sum.worstCase}, {sum.worstCaseBay}: {Pct(sum.worstCaseUtilisation)} of the deck capacity"));
            s.Facts.Add(("Bays over capacity in some case", sum.baysOverCapacity.ToString(Inv)));
            s.Facts.Add(("Deck's natural frequency", $"from {F(sum.lowestFrequencyHz, "0.0")} Hz" + (sum.frequencyEstimated ? " (estimated, good to about 25%)" : " (given)")));
            s.Facts.Add(("Worst resonance", $"{sum.worstResonanceActivity} in {sum.worstResonanceBay}: {F(sum.worstAccelerationG, "0.000")} g, {Pct(sum.worstResonanceRatio)} of the comfort limit"));

            var people = new ChartSeries { Name = "people on the roof", Color = SvgChart.Blue, X = r.crowd.samples.Select(x => (double)x.hour).ToList(), Y = r.crowd.samples.Select(x => (double)x.persons).ToList() };
            s.Charts.Add(new PdfChart
            {
                Svg = SvgChart.Lines("Crowd through the day", "hour of the day", "people", new[] { people }),
                Aspect = 520f / 190f, Caption = "How many people are on the roof through the chosen day (arrivals, the peak, departures), from the crowd simulation.",
            });

            var cases = r.weather.cases;
            s.Charts.Add(new PdfChart
            {
                Svg = SvgChart.Bars("Load cases: most loaded bay", "% of deck capacity", cases.Select(c => c.name ?? c.key ?? "").ToList(),
                    new[] { new ChartSeries { Name = "utilisation", Y = cases.Select(c => c.peakUtilisation * 100.0).ToList() } }, cases.Select(c => SvgChart.ForRatio(c.peakUtilisation)).ToList(), 100, "100% of the deck capacity"),
                Aspect = 520f / 190f, Caption = "The most loaded bay in each weather load case (with the crowd), against the deck capacity.",
            });

            var res = r.resonance;
            var lines = new List<ChartSeries>();
            var refs = new List<(double y, string label, string color)>();
            for (var i = 0; i < res.spectrum.Count; i++)
            {
                var color = SvgChart.Palette[i % SvgChart.Palette.Length];
                var sp = res.spectrum[i];
                lines.Add(new ChartSeries { Name = ActivityName(res, sp.activity), Color = color, X = Enumerable.Range(0, sp.accelerationG.Length).Select(k => (double)(res.spectrumFromHz + k * res.spectrumStepHz)).ToList(), Y = sp.accelerationG.Select(v => (double)v).ToList() });
                var act = res.activities.FirstOrDefault(a => a.key == sp.activity);
                if (act != null) refs.Add((act.limitG, "", color));
            }
            if (lines.Count > 0)
                s.Charts.Add(new PdfChart
                {
                    Svg = SvgChart.Lines("Resonance: acceleration against the deck's natural frequency", "the deck's natural frequency, Hz", "acceleration, g", lines,
                        (res.lowestFrequencyHz, res.highestFrequencyHz, "this roof's bays"), refs),
                    Aspect = 520f / 190f,
                    Caption = "What each kind of use does to the deck as its natural frequency changes (worst bay's loading). The shaded band is where this roof's bays lie; dashed lines are the comfort limits of each use.",
                });

            if (res.bays.Count > 0)
                s.Charts.Add(new PdfChart
                {
                    Svg = SvgChart.Bars("Resonance per bay: worst response against its comfort limit", "ratio (1 = the limit)", res.bays.Select(b => b.label ?? "").ToList(),
                        new[] { new ChartSeries { Name = "ratio", Y = res.bays.Select(b => (double)b.worstRatio).ToList() } }, res.bays.Select(b => SvgChart.ForRatio(b.worstRatio)).ToList(), 1, "comfort limit"),
                    Aspect = 520f / 190f, Caption = "For every bay, the worst of walking, court play and a jumping event, as a share of its comfort limit.",
                });

            s.Findings.AddRange(r.recommendations.Where(x => x.kind != "fine").Select(x => (string.IsNullOrEmpty(x.scenario) ? "" : Capital(x.scenario) + ": ") + x.text));
            Uses(s, r.assumptionUses);
            s.Assumptions.AddRange(r.assumptions);
            return s;
        }

        static string ActivityName(ResonanceReport res, string key) => res.activities.FirstOrDefault(a => a.key == key)?.name ?? key;
        static string Capital(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);

        static PdfSection Wind(WindInputs inputs, WindReport r, string caseStudy)
        {
            var sum = r.summary;
            var st = r.site;
            var s = new PdfSection
            {
                CaseStudy = caseStudy,
                Headline = $"{sum.plantsFailing} of {sum.plantsChecked} trees at risk, {sum.zonesUpliftFlagged} of {sum.zonesChecked} build-ups could lift",
            };
            s.Facts.Add(("Wind zone", $"{st.windZone}" + (string.IsNullOrEmpty(st.windZoneSource) ? "" : " (" + st.windZoneSource + ")") + (st.windZoneAssumed ? ", assumed" : "")));
            s.Facts.Add(("Basic wind speed / terrain", $"{F(st.basicWindSpeedMs)} m/s, terrain category {st.terrainCategory}"));
            s.Facts.Add(("Roof height above ground", $"{F(st.roofElevationM)} m" + (string.IsNullOrEmpty(st.roofHeightSource) ? "" : " (" + st.roofHeightSource + ")") + (st.roofElevationAssumed ? ", assumed" : "")));
            s.Facts.Add(("Peak pressure at the roof", F(st.peakPressureAtRoofPa, "0") + " Pa"));
            s.Facts.Add(("Trees checked / at risk / marginal", $"{sum.plantsChecked} / {sum.plantsFailing} / {sum.plantsMarginal}"));
            s.Facts.Add(("Build-ups that could lift", $"{sum.zonesUpliftFlagged} of {sum.zonesChecked} ({F(sum.percentPlantedAreaUpliftFlagged, "0")}% of the planted area)"));
            s.Facts.Add(("Growing medium that could blow away", $"{F(sum.percentPlantedAreaBareErodes, "0")}% of the planted area while bare; loose substrate moves from {F(sum.lowestBareOnsetMs, "0")} m/s"));

            var shapes = new List<PlanShape>();
            foreach (var z in r.zones)
            {
                var g = inputs.Zones.FirstOrDefault(x => x.Id == z.id);
                if (g == null) continue;
                shapes.Add(new PlanShape { X = g.X, Y = g.Y, W = g.Width, H = g.Height, Fill = SvgChart.ForStatus(z.upliftStatus), Label = z.upliftStatus == "unknown" ? "?" : Pct(z.upliftUtilisationMax), FillOpacity = 0.75 });
            }
            foreach (var p in r.plants)
                shapes.Add(new PlanShape { Kind = "circle", X = p.xM, Y = p.yM, W = Math.Max(0.8, p.crownM), Fill = SvgChart.ForStatus(p.status), Stroke = SvgChart.Ink, StrokeWidthM = 0.1, FillOpacity = 0.95 });
            s.Charts.Add(new PdfChart
            {
                Svg = SvgChart.Plan("Uplift of the build-ups and wind on the trees", inputs.RoofLength, inputs.RoofWidth, inputs.Outline, shapes,
                    new[] { ("ok", SvgChart.Ok), ("marginal", SvgChart.Warn), ("fails", SvgChart.Bad), ("not known", SvgChart.Neutral) }),
                Aspect = SvgChart.PlanAspect(inputs.RoofLength, inputs.RoofWidth),
                Caption = "Green-roof zones coloured by the worst uplift of the build-up (share of what would lift it, in the middle of each zone); circles are the trees, coloured by whether the wind could overturn them.",
            });

            if (r.zones.Count > 0)
                s.Charts.Add(new PdfChart
                {
                    Svg = SvgChart.Bars("Uplift of each build-up: worst point", "ratio (1 = it lifts)", r.zones.Select(z => z.label ?? z.id ?? "").ToList(),
                        new[] { new ChartSeries { Name = "uplift", Y = r.zones.Select(z => (double)z.upliftUtilisationMax).ToList() } }, r.zones.Select(z => SvgChart.ForStatus(z.upliftStatus)).ToList(), 1, "lifts"),
                    Aspect = 520f / 190f, Caption = "Wind suction against the weight of the build-up at the worst point of each zone (near the roof edge).",
                });
            if (r.plants.Count > 0)
                s.Charts.Add(new PdfChart
                {
                    Svg = SvgChart.Bars("Trees: overturning against resistance", "ratio (1 = overturns)", r.plants.Select(p => p.species ?? p.id ?? "").ToList(),
                        new[] { new ChartSeries { Name = "overturning", Y = r.plants.Select(p => (double)p.utilisation).ToList() } }, r.plants.Select(p => SvgChart.ForStatus(p.status)).ToList(), 1, "overturns"),
                    Aspect = 520f / 190f, Caption = "Overturning moment of the wind on each tree's crown against what its root ball and substrate resist.",
                });

            s.Findings.AddRange(r.recommendations.Select(x => x.text));
            s.Assumptions.AddRange(r.assumptions);
            return s;
        }

        static PdfSection Rain(PercolationReport r, string caseStudy)
        {
            var sum = r.summary;
            var s = new PdfSection
            {
                CaseStudy = caseStudy,
                Headline = $"a heavy shower is {F(sum.heavyShowerRetainedPercent, "0")}% retained, a cloudburst {F(sum.cloudburstRetainedPercent, "0")}%; {sum.zonesBelowTarget} of {sum.zonesChecked} zones below target",
            };
            s.Facts.Add(("Green roof area", F(sum.greenAreaM2, "0") + " m² in " + sum.zonesChecked + " zones (" + F(sum.otherAreaM2, "0") + " m² other)"));
            foreach (var sc in r.scenarios) s.Facts.Add((sc.name, $"{F(sc.intensityMmH, "0")} mm/h for {F(sc.durationMin, "0")} min = {F(sc.depthMm, "0.0")} mm"));
            s.Facts.Add(("Heavy shower", $"{F(sum.heavyShowerRetainedPercent, "0")}% kept, peak flow cut by {F(sum.heavyShowerPeakReductionPercent, "0")}%"));
            s.Facts.Add(("Cloudburst", $"{F(sum.cloudburstRetainedPercent, "0")}% kept, peak flow cut by {F(sum.cloudburstPeakReductionPercent, "0")}%"));
            s.Facts.Add(("Zones below the retention target / full in a cloudburst", $"{sum.zonesBelowTarget} / {sum.zonesSaturatedInCloudburst}"));

            foreach (var ev in r.roof)
            {
                var step = r.seriesStepS / 60.0;
                var xs = Enumerable.Range(0, ev.flowLps.Length).Select(i => i * step).ToList();
                s.Charts.Add(new PdfChart
                {
                    Svg = SvgChart.Lines($"{ev.scenario}: runoff from the roof", "minutes from the start of the rain", "litres per second",
                        new[]
                        {
                            new ChartSeries { Name = "with the green roof", Color = SvgChart.Ok, X = xs, Y = ev.flowLps.Select(v => (double)v).ToList() },
                            new ChartSeries { Name = "bare roof, the same rain", Color = SvgChart.Neutral, Dashed = true, X = xs, Y = ev.referenceLps.Select(v => (double)v).ToList() },
                        }, null, null, 520, 190),
                    Aspect = 520f / 190f,
                    Caption = $"{F(ev.rainMm, "0.0")} mm of rain: {F(ev.retainedPercent, "0")}% stays on the roof, the peak drops from {F(ev.referencePeakLps, "0.0")} to {F(ev.peakFlowLps, "0.0")} l/s" + (ev.peakDelayMin > 0.05 ? $" and comes {F(ev.peakDelayMin, "0")} min later." : "."),
                });
            }

            if (r.zones.Count > 0 && r.scenarios.Count > 0)
            {
                var series = r.scenarios.Select((sc, k) => new ChartSeries
                {
                    Name = sc.name, Color = SvgChart.Palette[k % SvgChart.Palette.Length],
                    Y = r.zones.Select(z => (double)(z.scenarios.FirstOrDefault(x => x.scenario == sc.name)?.retainedPercent ?? 0)).ToList(),
                }).ToList();
                s.Charts.Add(new PdfChart
                {
                    Svg = SvgChart.Bars("Rain kept by each build-up", "% of the rain", r.zones.Select(z => z.label ?? z.id ?? "").ToList(), series, null, null, ""),
                    Aspect = 520f / 190f, Caption = "How much of each rain event every green-roof zone keeps, from its substrate and water-storing drainage layer.",
                });
            }

            s.Findings.AddRange(r.recommendations.Where(x => x.kind != "fine").Select(x => x.text));
            s.Assumptions.AddRange(r.assumptions);
            return s;
        }

        static PdfSection Sun(SunInputs inputs, SunReport r, string caseStudy)
        {
            var sum = r.summary;
            var s = new PdfSection
            {
                CaseStudy = caseStudy, Preliminary = sum.preliminary, PreliminaryNote = sum.preliminaryNote,
                Headline = $"{sum.peopleZonesTooSunny} of {sum.peopleZones} people zones too sunny at midday ({sum.peopleZonesTooSunnyAfter} with the shading), {sum.gardenZonesTooShaded} of {sum.gardenZones} gardens too shaded",
            };
            s.Facts.Add(("Site", $"latitude {F(sum.latitudeDeg, "0.0")}° N" + (sum.latitudeAssumed ? " (assumed)" : "") + $", roof turned {F(sum.northDeg, "0")}°" + (sum.northAssumed ? " (assumed)" : "")));
            s.Facts.Add(("Targets", $"at most {F(100 - sum.shadeTargetPercent, "0")}% sun in people zones at midday (shade target {F(sum.shadeTargetPercent, "0")}%); gardens at least {F(sum.gardenMinSunHours)} h of sun"));
            s.Facts.Add(("People zones too sunny", $"{sum.peopleZonesTooSunny} of {sum.peopleZones}, {sum.peopleZonesTooSunnyAfter} after the shading"));
            s.Facts.Add(("Gardens too shaded", $"{sum.gardenZonesTooShaded} of {sum.gardenZones}, {sum.gardenZonesTooShadedAfter} after the shading"));
            s.Facts.Add(("Shading pieces", sum.pieces == 0 ? "none needed" : $"{sum.pieces}, weighing {F(sum.addedLoadKn, "0.0")} kN on the deck; wind on them up to {F(sum.peakWindPressurePa, "0")} Pa"));
            if (r.structure.ran) s.Facts.Add(("Deck with the pieces", $"most loaded bay {Pct(r.structure.peakUtilisationBefore)} → {Pct(r.structure.peakUtilisationAfter)}; bays over capacity {r.structure.baysOverBefore} → {r.structure.baysOverAfter}"));
            foreach (var d in r.days) s.Facts.Add((d.name, $"sunrise {Hhmm(d.sunriseH)}, sunset {Hhmm(d.sunsetH)}, noon elevation {F(d.noonElevationDeg, "0")}°, roof mean {F(d.roofMeanSunHours, "0.0")} h of sun"));

            var shapes = new List<PlanShape>();
            foreach (var it in inputs.Structure.Items)
            {
                var z = r.zones.FirstOrDefault(x => x.id == it.Id);
                if (it.Kind == LoadKind.Tree)     // a tree is its crown: a circle, and no label over the zone it stands in
                {
                    shapes.Add(new PlanShape { Kind = "circle", X = it.X + it.Width / 2, Y = it.Y + it.Height / 2, W = Math.Max(it.Width, it.Height), Fill = "#2f6b45", FillOpacity = 0.55, Stroke = "#2f6b45", StrokeWidthM = 0.08 });
                    continue;
                }
                shapes.Add(new PlanShape { X = it.X, Y = it.Y, W = it.Width, H = it.Height, Fill = z == null ? SvgChart.Faint : SvgChart.ForStatus(z.status), FillOpacity = 0.7, LabelColor = SvgChart.Ink, Label = it.Label ?? "" });
            }
            foreach (var e in r.equipment)
            {
                var sh = new PlanShape { Fill = SvgChart.Ink, FillOpacity = 0.25, Stroke = SvgChart.Ink, StrokeWidthM = 0.12, Dashed = true, LabelColor = SvgChart.Ink, LabelSize = 6, Label = "" };
                if (e.shape == "rect") { sh.X = e.x; sh.Y = e.y; sh.W = e.widthM; sh.H = e.depthM; }
                else { sh.Kind = "circle"; sh.X = e.x + e.widthM / 2; sh.Y = e.y + e.depthM / 2; sh.W = e.widthM; }
                shapes.Add(sh);
            }
            s.Charts.Add(new PdfChart
            {
                Svg = SvgChart.Plan("Sun and shade on the roof", inputs.Structure.RoofLength, inputs.Structure.RoofWidth, inputs.Structure.Outline, shapes,
                    new[] { ("ok", SvgChart.Ok), ("too sunny", SvgChart.Bad), ("too shaded", SvgChart.Blue), ("tree", "#2f6b45"), ("shading piece", SvgChart.Ink) }),
                Aspect = SvgChart.PlanAspect(inputs.Structure.RoofLength, inputs.Structure.RoofWidth),
                Caption = "Every zone coloured by what the sun does to it on 21 June (people zones too sunny at midday, gardens too shaded); dashed shapes are the shading pieces the analysis places.",
            });

            var zones = r.zones.Where(z => z.status != "n/a" || z.kind == "garden").ToList();
            if (zones.Count > 0)
            {
                s.Charts.Add(new PdfChart
                {
                    Svg = SvgChart.Bars("Hours of direct sun", "hours", zones.Select(z => z.label ?? z.id ?? "").ToList(), new[]
                    {
                        new ChartSeries { Name = "21 June", Color = "#e0a030", Y = zones.Select(z => (double)z.sunHoursJune).ToList() },
                        new ChartSeries { Name = "21 March", Color = SvgChart.Teal, Y = zones.Select(z => (double)z.sunHoursMarch).ToList() },
                        new ChartSeries { Name = "21 December", Color = SvgChart.Blue, Y = zones.Select(z => (double)z.sunHoursDecember).ToList() },
                    }, null, sum.gardenMinSunHours > 0 ? sum.gardenMinSunHours : (double?)null, "gardens need " + F(sum.gardenMinSunHours) + " h"),
                    Aspect = 520f / 190f, Caption = "Direct sun each zone gets on the three design days (the dashed line is what the gardens need in June).",
                });
                s.Charts.Add(new PdfChart
                {
                    Svg = SvgChart.Bars("Shade at midday in the heat window, 21 June", "% shaded", zones.Select(z => z.label ?? z.id ?? "").ToList(), new[]
                    {
                        new ChartSeries { Name = "now", Color = SvgChart.Neutral, Y = zones.Select(z => (double)z.peakShadePercent).ToList() },
                        new ChartSeries { Name = "with the shading pieces", Color = SvgChart.Ok, Y = zones.Select(z => (double)z.afterPeakShadePercent).ToList() },
                    }, null, sum.shadeTargetPercent, "shade target " + F(sum.shadeTargetPercent, "0") + "%"),
                    Aspect = 520f / 190f, Caption = "How much of each zone is in shade in the heat window, before and after the shading pieces, against the shade target for people zones.",
                });
            }

            s.Findings.AddRange(r.recommendations.Where(x => x.kind != "fine").Select(x => x.text));
            Uses(s, r.assumptionUses);
            s.Assumptions.AddRange(r.assumptions);
            return s;
        }

        static string Hhmm(double h) => ((int)h).ToString("00", Inv) + ":" + ((int)Math.Round((h - Math.Floor(h)) * 60) % 60).ToString("00", Inv);

        // ------------------------------------------------------------------------------------------------------------ the document

        static Document Render(PdfExport export, string projectName)
        {
            QuestPDF.Settings.License = LicenseType.Community;
            const string ink = "#222633", muted = "#5b6274";

            return Document.Create(doc =>
            {
                doc.Page(page =>
                {
                    Frame(page, projectName);
                    page.Content().Column(col =>
                    {
                        col.Spacing(10);
                        col.Item().Text("Physical analysis").FontSize(26).Bold().FontColor(ink);
                        col.Item().Text("Charts of what the analyses found").FontSize(13).FontColor(muted);
                        col.Item().PaddingTop(4).Text(text =>
                        {
                            text.Span("Project: ").Bold(); text.Span(projectName.Length > 0 ? projectName : "the layout pushed from the Sportify web app");
                            text.Line("");
                            text.Span("Made: ").Bold(); text.Span(DateTime.Now.ToString("yyyy-MM-dd HH:mm", Inv));
                        });

                        col.Item().PaddingTop(8).Text("What is in this report").FontSize(13).Bold();
                        foreach (var s in export.Sections)
                            col.Item().Text(t =>
                            {
                                t.Span(s.Title + ": ").Bold();
                                t.Span(s.Headline);
                                if (s.Preliminary) t.Span("  PRELIMINARY").Bold().FontColor("#b26a00");
                            });
                        foreach (var k in export.Skipped)
                            col.Item().Text(t => { t.Span(k.Title + ": ").Bold().FontColor(muted); t.Span("not drawn, " + k.Reason).FontColor(muted); });

                        if (export.Sections.Any(s => s.Preliminary))
                            col.Item().PaddingTop(6).Background("#fff4d6").Padding(8).Text("PRELIMINARY: some values these results rest on (deck capacity, snow zone, orientation ...) are built-in values nobody has confirmed. Enter or accept them in the web app's Structure and Site conditions tabs or in Revit, and run the report again.").FontColor("#7a4b00");

                        col.Item().PaddingTop(10).Text("These are the same numbers the 3D videos and the web app show, drawn as static charts so that no Unity is needed. They are screening models, not a structural, wind-engineering or hydrological verification; the assumptions behind each are listed with it. Ball trajectories are not included: they are simulated in Unity.")
                            .FontSize(9).FontColor(muted);
                    });
                });

                foreach (var s in export.Sections)
                    doc.Page(page =>
                    {
                        Frame(page, projectName);
                        page.Content().Column(col =>
                        {
                            col.Spacing(8);
                            col.Item().Text(s.Title).FontSize(20).Bold().FontColor(ink);
                            if (s.CaseStudy.Length > 0) col.Item().Text(s.CaseStudy).FontSize(9).FontColor(muted);
                            col.Item().Text(s.Headline).FontSize(11).SemiBold();
                            if (s.Preliminary)
                                col.Item().Background("#fff4d6").Padding(6).Text("PRELIMINARY — " + s.PreliminaryNote.Replace("PRELIMINARY: ", "")).FontSize(8.5f).FontColor("#7a4b00");

                            col.Item().Table(table =>
                            {
                                table.ColumnsDefinition(c => { c.RelativeColumn(2.3f); c.RelativeColumn(4); });
                                foreach (var f in s.Facts)
                                {
                                    table.Cell().BorderBottom(0.4f).BorderColor("#d8dbe3").PaddingVertical(2).Text(f.Label).FontSize(8.5f).FontColor(muted);
                                    table.Cell().BorderBottom(0.4f).BorderColor("#d8dbe3").PaddingVertical(2).Text(f.Value).FontSize(8.5f);
                                }
                            });

                            foreach (var c in s.Charts)
                                col.Item().ShowEntire().Column(cc =>
                                {
                                    cc.Item().AspectRatio(c.Aspect).Svg(c.Svg);
                                    if (c.Caption.Length > 0) cc.Item().PaddingTop(2).Text(c.Caption).FontSize(8).FontColor(muted);
                                });

                            if (s.Findings.Count > 0)
                            {
                                col.Item().PaddingTop(4).Text("What to change").FontSize(12).Bold();
                                foreach (var f in s.Findings) col.Item().Row(r => { r.ConstantItem(10).Text("•"); r.RelativeItem().Text(f).FontSize(8.5f); });
                            }

                            if (s.Inputs.Count > 0)
                            {
                                col.Item().PaddingTop(4).Text("What it rests on").FontSize(12).Bold();
                                col.Item().Table(table =>
                                {
                                    table.ColumnsDefinition(c => { c.RelativeColumn(3); c.RelativeColumn(2.5f); c.RelativeColumn(1.6f); });
                                    foreach (var i in s.Inputs)
                                    {
                                        table.Cell().BorderBottom(0.4f).BorderColor("#d8dbe3").PaddingVertical(2).Text(i.Label).FontSize(8.5f);
                                        table.Cell().BorderBottom(0.4f).BorderColor("#d8dbe3").PaddingVertical(2).Text(i.Value).FontSize(8.5f);
                                        table.Cell().BorderBottom(0.4f).BorderColor("#d8dbe3").PaddingVertical(2).Text(i.State == "unconfirmed" ? "not confirmed" : i.State).FontSize(8.5f).FontColor(i.State == "unconfirmed" ? "#b26a00" : muted);
                                    }
                                });
                            }

                            if (s.Assumptions.Count > 0)
                            {
                                col.Item().PaddingTop(4).Text("Assumptions and method").FontSize(12).Bold();
                                foreach (var a in s.Assumptions.Where(x => !x.StartsWith("Input - "))) col.Item().Row(r => { r.ConstantItem(10).Text("•").FontSize(7.5f); r.RelativeItem().Text(a).FontSize(7.5f).FontColor(muted); });
                            }
                        });
                    });
            });
        }

        static void Frame(PageDescriptor page, string projectName)
        {
            page.Size(PageSizes.A4);
            page.Margin(36);
            page.DefaultTextStyle(x => x.FontSize(9.5f).FontColor("#222633"));
            page.Header().PaddingBottom(8).Row(r =>
            {
                r.RelativeItem().Text("Sportify · Physical analysis" + (projectName.Length > 0 ? " · " + projectName : "")).FontSize(8).FontColor("#5b6274");
                r.ConstantItem(120).AlignRight().Text(DateTime.Now.ToString("yyyy-MM-dd", Inv)).FontSize(8).FontColor("#5b6274");
            });
            page.Footer().AlignCenter().Text(t => { t.Span("Page ").FontSize(8).FontColor("#5b6274"); t.CurrentPageNumber().FontSize(8).FontColor("#5b6274"); t.Span(" of ").FontSize(8).FontColor("#5b6274"); t.TotalPages().FontSize(8).FontColor("#5b6274"); });
        }
    }
}
