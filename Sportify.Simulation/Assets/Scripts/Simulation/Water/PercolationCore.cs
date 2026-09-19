#nullable disable
using System;
using System.Collections.Generic;
using System.Globalization;
using Sportify.Simulation.Wind;

namespace Sportify.Simulation.Water
{
    // ---------------------------------------------------------------------------------------
    //  Rain on a green roof: how much stays in the build-up, how much runs off, and when.
    //
    //  Like WindAnalysisCore.cs this uses nothing from UnityEngine, so the numbers can be produced
    //  in the Revit add-in with no Unity installed, and tested outside Unity; the renderer only
    //  steps the same SoilColumn to draw water moving down it.
    //
    //  It is a SCREENING model, not a hydrological design. Each zone is one vertical column:
    //
    //    rain -> interception -> surface (2 mm) -> substrate cells -> drainage layer -> outlet
    //
    //  The substrate is a stack of 10 mm cells with a saturated water content (theta_s), a field
    //  capacity (theta_fc: what it holds against gravity) and a residual (theta_r). Gravity moves
    //  water only from the part of a cell above field capacity, at a rate that grows with how far
    //  above it is (the "tipping bucket" cascade used in simple green-roof and LID models). The
    //  drainage layer holds its retention cups full, then releases the rest through a linear
    //  reservoir. Where the provider prints a system's water storage (ZinCo does) the substrate's
    //  porosity is calibrated so the column's maximum storage matches it.
    //
    //  The rain scenarios are GENERIC (steady rain, heavy shower, cloudburst); they are not the
    //  site's design rainfall (KOSTRA-DWD), which would come from the site location the way the
    //  wind zone does. Every constant that is a judgement call is named below and repeated in the
    //  report's assumptions.
    //
    //  Units: millimetres of water over the zone (1 mm = 1 L/m2), seconds; flows in mm/h.
    // ---------------------------------------------------------------------------------------

    [Serializable]
    public class RainScenario
    {
        public string name;
        public string description;
        public float intensityMmH;
        public float durationMin;
        public float depthMm;
    }

    [Serializable]
    public class ZoneScenarioResult
    {
        public string scenario;
        public float rainMm;
        public float runoffMm;
        public float retainedPercent;
        public float peakRunoffMmH;
        public float peakReductionPercent;      // against the same rain on a bare roof
        public float firstRunoffMin;            // -1 = no runoff at all
        public float peakDelayMin;              // how much later the peak leaves the zone than on a bare roof
        public float maxSubstrateFillPercent;   // of the way from residual to saturated, worst cell
        public bool saturated;                  // some cell reached saturation: the column is full
        public float surfaceRunoffMm;           // rain that couldn't get in at the top
    }

    [Serializable]
    public class ZonePercolationResult
    {
        public string id;
        public string label;
        public string system;
        public string category;
        public float areaM2;
        public float substrateMm;
        public float storageAtSaturationMm;     // substrate at saturation + the drainage layer's cups
        public float storageAtFieldCapacityMm;  // what it keeps against gravity
        public string storageSource;            // "calibrated to the published water storage" | "estimated from the layers" | "paved / no substrate"
        public float publishedStorageMm;        // 0 when the provider prints none
        public List<ZoneScenarioResult> scenarios = new List<ZoneScenarioResult>();
        public float extraSubstrateForTargetMm; // added substrate that would bring the heavy shower to the target retention; -1 = not reachable, 0 = already there
        public float extraDrainageForTargetMm;  // the same with a thicker water-storing drainage layer instead
    }

    [Serializable]
    public class RoofScenarioResult
    {
        public string scenario;
        public float rainMm;
        public float runoffM3;
        public float referenceRunoffM3;         // the same rain on the same roof with no green layers
        public float retainedPercent;
        public float peakFlowLps;
        public float referencePeakLps;
        public float peakReductionPercent;
        public float peakDelayMin;
        public float firstRunoffMin;
        public float[] flowLps;                 // the hydrograph, one value per SeriesStepS
        public float[] referenceLps;
    }

    [Serializable]
    public class PercolationRecommendation
    {
        public string kind;                     // more-storage | check-outlets | surface | fine
        public string target;
        public string text;
    }

    [Serializable]
    public class PercolationSummary
    {
        public int zonesChecked;
        public float greenAreaM2;
        public float otherAreaM2;
        public float heavyShowerRetainedPercent;
        public float heavyShowerPeakReductionPercent;
        public float cloudburstRetainedPercent;
        public float cloudburstPeakReductionPercent;
        public int zonesBelowTarget;
        public int zonesSaturatedInCloudburst;
    }

    [Serializable]
    public class PercolationReport
    {
        public bool ran;
        public List<string> assumptions = new List<string>();
        public List<RainScenario> scenarios = new List<RainScenario>();
        public List<ZonePercolationResult> zones = new List<ZonePercolationResult>();
        public List<RoofScenarioResult> roof = new List<RoofScenarioResult>();
        public PercolationSummary summary = new PercolationSummary();
        public List<PercolationRecommendation> recommendations = new List<PercolationRecommendation>();
        public float seriesStepS;
    }

    public class WaterInputs
    {
        public double RoofLength, RoofWidth;
        public List<ZoneInput> Zones = new List<ZoneInput>();
    }

    /// <summary>The layers of one build-up as the column sees them, with their water parameters.</summary>
    public sealed class ColumnSpec
    {
        public double SubstrateMm;           // total thickness of the cells
        public double DrainageMm;            // thickness of the drainage layer (0 = none)
        public double ThetaS, ThetaFc, ThetaR, KsMmS;
        public double InterceptionMm;
        public double DepressionMm;          // storage on a surface with no substrate (paved, membrane)
        public bool HasSubstrate;
        public string StorageSource;
        public double PublishedStorageMm;
    }

    /// <summary>
    /// One vertical column of a build-up, stepped in time. The analysis runs it to the end of a scenario; the video
    /// steps it a few seconds per frame and draws its cells, so what is filmed is exactly what was computed.
    /// </summary>
    public sealed class SoilColumn
    {
        public readonly ColumnSpec Spec;
        public readonly int Cells;
        public readonly double CellMm;
        public readonly double[] Theta;              // per cell, top first
        public double SurfaceMm;                     // water lying on top
        public double InterceptionMm;                // held on the plants
        public double DrainageStoreMm;               // in the drainage layer
        public double RainMm, RunoffMm, SurfaceRunoffMm;
        public double LastRunoffMmS;                 // outflow rate in the last step

        readonly double _cupsMm;
        readonly double[] _flux;

        public SoilColumn(ColumnSpec spec)
        {
            Spec = spec;
            CellMm = PercolationModel.CellMm;
            Cells = spec.HasSubstrate ? Math.Max(1, (int)Math.Round(spec.SubstrateMm / CellMm)) : 0;
            Theta = new double[Cells];
            _flux = new double[Cells + 1];
            _cupsMm = PercolationModel.DrainageCupsMm(spec.DrainageMm);

            var start = spec.ThetaR + PercolationModel.AntecedentFraction * (spec.ThetaFc - spec.ThetaR);
            for (var i = 0; i < Cells; i++) Theta[i] = start;
            DrainageStoreMm = 0;
        }

        double CellDepth(int i)
        {
            return Cells == 0 ? 0 : Spec.SubstrateMm / Cells;
        }

        /// <summary>Water above what the column started with, in mm: what it is holding.</summary>
        public double StoredMm
        {
            get
            {
                var s = SurfaceMm + InterceptionMm + DrainageStoreMm;
                var start = Spec.ThetaR + PercolationModel.AntecedentFraction * (Spec.ThetaFc - Spec.ThetaR);
                for (var i = 0; i < Cells; i++) s += (Theta[i] - start) * CellDepth(i);
                return s;
            }
        }

        /// <summary>Average of the substrate cells' progress from residual to saturated, 0..1.</summary>
        public double Saturation(int cell)
        {
            var span = Spec.ThetaS - Spec.ThetaR;
            return span <= 0 ? 0 : Math.Max(0, Math.Min(1, (Theta[cell] - Spec.ThetaR) / span));
        }

        public double DrainageFill
        {
            get
            {
                var cap = _cupsMm + PercolationModel.DrainageOverflowMm;
                return cap <= 0 ? 0 : Math.Max(0, Math.Min(1, DrainageStoreMm / cap));
            }
        }

        /// <summary>Advances the column dtS seconds under rain of rainMmS mm per second.</summary>
        public void Step(double dtS, double rainMmS)
        {
            var rain = rainMmS * dtS;
            RainMm += rain;

            // Plants hold the first millimetre or so (and lose it to the air later; not modelled).
            var held = Math.Min(rain, Math.Max(0, Spec.InterceptionMm - InterceptionMm));
            InterceptionMm += held;
            SurfaceMm += rain - held;

            double outflow;
            if (Cells == 0)
            {
                // No substrate (a paved or membrane surface): a film stays, the rest goes straight to the drains.
                var over = Math.Max(0, SurfaceMm - Spec.DepressionMm);
                SurfaceMm -= over;
                DrainageStoreMm += over;
                outflow = ReleaseFromDrainage(dtS);
                Finish(outflow, dtS);
                return;
            }

            // The surface holds 2 mm; more than that is rain the top couldn't take.
            // Fluxes into each cell, from the state at the start of the step.
            var top = Theta[0];
            var room0 = (Spec.ThetaS - top) * CellDepth(0);
            _flux[0] = Math.Min(Math.Min(SurfaceMm, Spec.KsMmS * dtS), Math.Max(0, room0));
            for (var i = 1; i < Cells; i++)
            {
                var above = Theta[i - 1];
                var excess = Math.Max(0, above - Spec.ThetaFc);
                var rate = Spec.KsMmS * Math.Pow(excess / Math.Max(1e-9, Spec.ThetaS - Spec.ThetaFc), PercolationModel.DrainExponent);
                var available = excess * CellDepth(i - 1);
                var room = (Spec.ThetaS - Theta[i]) * CellDepth(i);
                _flux[i] = Math.Max(0, Math.Min(Math.Min(rate * dtS, available), room));
            }
            {
                var last = Theta[Cells - 1];
                var excess = Math.Max(0, last - Spec.ThetaFc);
                var rate = Spec.KsMmS * Math.Pow(excess / Math.Max(1e-9, Spec.ThetaS - Spec.ThetaFc), PercolationModel.DrainExponent);
                _flux[Cells] = Math.Max(0, Math.Min(rate * dtS, excess * CellDepth(Cells - 1)));
            }

            for (var i = 0; i < Cells; i++)
                Theta[i] += (_flux[i] - _flux[i + 1]) / CellDepth(i);
            SurfaceMm -= _flux[0];

            // What the top couldn't take beyond the 2 mm the surface holds runs off at once.
            var surfaceOver = Math.Max(0, SurfaceMm - PercolationModel.SurfaceStorageMm);
            SurfaceMm -= surfaceOver;
            SurfaceRunoffMm += surfaceOver;

            DrainageStoreMm += _flux[Cells];
            outflow = ReleaseFromDrainage(dtS) + surfaceOver;
            Finish(outflow, dtS);
        }

        double ReleaseFromDrainage(double dtS)
        {
            // The cups fill first; what is above them leaves through a linear reservoir.
            var above = Math.Max(0, DrainageStoreMm - _cupsMm);
            var released = Math.Min(above, above * dtS / PercolationModel.DrainageLagS);
            DrainageStoreMm -= released;
            return released;
        }

        void Finish(double outflowMm, double dtS)
        {
            RunoffMm += outflowMm;
            LastRunoffMmS = outflowMm / dtS;
        }

        /// <summary>The mass balance: rain in = runoff out + what the column now holds. Returns the difference (should be ~0).</summary>
        public double MassBalanceErrorMm()
        {
            return RainMm - RunoffMm - StoredMm;
        }
    }

    public static class PercolationModel
    {
        // --- constants that are judgement calls (all repeated in the report) ---
        public const double CellMm = 10.0;
        public const double DtS = 1.0;
        public const double SurfaceStorageMm = 2.0;
        public const double AntecedentFraction = 1.0;       // start: AT field capacity (wet, as after earlier rain: the cautious case for stormwater)
        public const double FieldCapacityOfSaturated = 0.70;
        public const double ResidualOfSaturated = 0.20;
        public const double DrainExponent = 2.5;            // drainage rate vs excess over field capacity
        public const double DrainageCupsPerMm = 0.20;       // mm of water kept per mm of drainage layer
        public const double DrainageOverflowMm = 6.0;       // free water the layer holds while it drains
        public const double DrainageLagS = 90.0;
        public const double BareRoofLagS = 30.0;
        public const double BareRoofFilmMm = 1.0;
        public const double SeriesStepS = 30.0;
        public const double TailMin = 60.0;                 // watch the roof drain down this long after the rain stops
        public const double TargetRetentionPercent = 60.0;  // heavy-shower retention a zone is advised to reach
        public const double MaxAddedThicknessMm = 200.0;

        public static List<RainScenario> DefaultScenarios()
        {
            return new List<RainScenario>
            {
                Scenario("Steady rain", "10 mm/h for 3 hours", 10, 180),
                Scenario("Heavy shower", "40 mm/h for 30 minutes", 40, 30),
                Scenario("Cloudburst", "108 mm/h (300 l/s per ha) for 10 minutes", 108, 10),
            };
        }

        static RainScenario Scenario(string name, string description, double mmH, double minutes)
        {
            return new RainScenario { name = name, description = description, intensityMmH = (float)mmH, durationMin = (float)minutes, depthMm = (float)(mmH * minutes / 60.0) };
        }

        public static double DrainageCupsMm(double drainageMm)
        {
            return drainageMm * DrainageCupsPerMm;
        }

        // ---------------------------------------------------------------- build-up -> column

        static bool IsSubstrateLike(LayerInput l)
        {
            return string.Equals(l.Function, "substrate", StringComparison.OrdinalIgnoreCase);
        }

        public static ColumnSpec SpecFor(AssemblyInput a)
        {
            var spec = new ColumnSpec { StorageSource = "paved / no substrate" };
            if (a == null) return spec;

            double substrate = 0, drainage = 0;
            foreach (var l in a.Layers)
            {
                if (IsSubstrateLike(l)) substrate += l.ThicknessMm;
                else if (string.Equals(l.Function, "drainage", StringComparison.OrdinalIgnoreCase)) drainage += l.ThicknessMm;
            }

            var cover = WindModel.Cover(a);
            spec.DrainageMm = drainage;
            spec.SubstrateMm = substrate;
            spec.HasSubstrate = substrate >= CellMm && cover != CoverKind.Paved;
            spec.DepressionMm = BareRoofFilmMm;
            spec.InterceptionMm = cover == CoverKind.Paved ? 0.0 : (cover == CoverKind.CoarseGravel ? 0.3 : 1.0);
            if (!spec.HasSubstrate) return spec;

            var intensive = string.Equals(a.Category, "intensive", StringComparison.OrdinalIgnoreCase);
            var thetaS = intensive ? 0.45 : 0.50;
            spec.KsMmS = intensive ? 0.2 : 0.5;
            spec.StorageSource = "estimated from the layers";

            // Where the provider prints the water storage of the whole system, calibrate the substrate's pore space to it.
            if (a.SaturatedKgM2.HasValue && a.WaterStorageLM2.HasValue && a.WaterStorageLM2.Value > 0)
            {
                spec.PublishedStorageMm = a.WaterStorageLM2.Value;
                var fromSubstrate = a.WaterStorageLM2.Value - DrainageCupsMm(drainage) - DrainageOverflowMm * (drainage > 0 ? 1 : 0);
                var calibrated = fromSubstrate / substrate;
                if (calibrated >= 0.30 && calibrated <= 0.65)
                {
                    thetaS = calibrated;
                    spec.StorageSource = "calibrated to the published water storage";
                }
            }

            spec.ThetaS = thetaS;
            spec.ThetaFc = FieldCapacityOfSaturated * thetaS;
            spec.ThetaR = ResidualOfSaturated * thetaS;
            return spec;
        }

        // ---------------------------------------------------------------- one column, one scenario

        sealed class Run
        {
            public double[] RunoffMmH;       // per series step
            public double RainMm, RunoffMm, SurfaceMm;
            public double FirstRunoffMin = -1;
            public double PeakMmH, PeakAtMin;
            public double MaxFill;
            public bool Saturated;
            public double MassError;
        }

        static Run Simulate(ColumnSpec spec, RainScenario sc)
        {
            var col = new SoilColumn(spec);
            var rainS = sc.durationMin * 60.0;
            var totalS = rainS + TailMin * 60.0;
            var steps = (int)Math.Ceiling(totalS / DtS);
            var perSample = (int)(SeriesStepS / DtS);
            var run = new Run { RunoffMmH = new double[(int)Math.Ceiling(totalS / SeriesStepS) + 1] };
            var rate = sc.intensityMmH / 3600.0;
            double sampleRunoff = 0;

            for (var s = 0; s < steps; s++)
            {
                var t = s * DtS;
                col.Step(DtS, t < rainS ? rate : 0.0);
                sampleRunoff += col.LastRunoffMmS * DtS;

                if (col.LastRunoffMmS * 3600.0 > 0.1 && run.FirstRunoffMin < 0) run.FirstRunoffMin = t / 60.0;

                for (var i = 0; i < col.Cells; i++)
                {
                    var f = col.Saturation(i);
                    if (f > run.MaxFill) run.MaxFill = f;
                    if (col.Theta[i] >= spec.ThetaS - 0.005) run.Saturated = true;
                }

                if ((s + 1) % perSample == 0)
                {
                    var idx = (s + 1) / perSample - 1;
                    if (idx < run.RunoffMmH.Length) run.RunoffMmH[idx] = sampleRunoff / SeriesStepS * 3600.0;
                    if (run.RunoffMmH[idx] > run.PeakMmH) { run.PeakMmH = run.RunoffMmH[idx]; run.PeakAtMin = (s + 1) * DtS / 60.0; }
                    sampleRunoff = 0;
                }
            }

            run.RainMm = col.RainMm;
            run.RunoffMm = col.RunoffMm;
            run.SurfaceMm = col.SurfaceRunoffMm;
            run.MassError = col.MassBalanceErrorMm();
            return run;
        }

        static Run SimulateBare(RainScenario sc)
        {
            // A bare roof: film, then the rain leaves through a short lag.
            var col = new BareRoofColumn();
            var rainS = sc.durationMin * 60.0;
            var totalS = rainS + TailMin * 60.0;
            var steps = (int)Math.Ceiling(totalS / DtS);
            var perSample = (int)(SeriesStepS / DtS);
            var run = new Run { RunoffMmH = new double[(int)Math.Ceiling(totalS / SeriesStepS) + 1] };
            var rate = sc.intensityMmH / 3600.0;
            double sampleRunoff = 0;

            for (var s = 0; s < steps; s++)
            {
                var t = s * DtS;
                var outflow = col.Step(DtS, t < rainS ? rate : 0.0);
                sampleRunoff += outflow;
                if (outflow / DtS * 3600.0 > 0.1 && run.FirstRunoffMin < 0) run.FirstRunoffMin = t / 60.0;
                if ((s + 1) % perSample == 0)
                {
                    var idx = (s + 1) / perSample - 1;
                    if (idx < run.RunoffMmH.Length) run.RunoffMmH[idx] = sampleRunoff / SeriesStepS * 3600.0;
                    if (run.RunoffMmH[idx] > run.PeakMmH) { run.PeakMmH = run.RunoffMmH[idx]; run.PeakAtMin = (s + 1) * DtS / 60.0; }
                    sampleRunoff = 0;
                }
            }
            run.RainMm = col.RainMm;
            run.RunoffMm = col.RunoffMm;
            return run;
        }

        /// <summary>A roof with nothing on it: a film of water, then a short lag to the drains.</summary>
        sealed class BareRoofColumn
        {
            double _film, _queue;
            public double RainMm, RunoffMm;

            public double Step(double dtS, double rainMmS)
            {
                var rain = rainMmS * dtS;
                RainMm += rain;
                _film += rain;
                var over = Math.Max(0, _film - BareRoofFilmMm);
                _film -= over;
                _queue += over;
                var released = Math.Min(_queue, _queue * dtS / BareRoofLagS);
                _queue -= released;
                RunoffMm += released;
                return released;
            }
        }

        // ---------------------------------------------------------------- the analysis

        public static PercolationReport Analyse(WaterInputs inputs)
        {
            var report = new PercolationReport { ran = true, seriesStepS = (float)SeriesStepS };
            report.scenarios = DefaultScenarios();
            report.assumptions.AddRange(Assumptions());

            double greenArea = 0;
            foreach (var z in inputs.Zones) greenArea += z.Width * z.Height;
            var roofArea = inputs.RoofLength * inputs.RoofWidth;
            var otherArea = Math.Max(0, roofArea - greenArea);

            var bare = new Run[report.scenarios.Count];
            for (var k = 0; k < bare.Length; k++) bare[k] = SimulateBare(report.scenarios[k]);

            // Roof totals per scenario, filled as the zones are run.
            var series = new double[report.scenarios.Count][];
            var reference = new double[report.scenarios.Count][];
            var totalRunoff = new double[report.scenarios.Count];
            var referenceRunoff = new double[report.scenarios.Count];
            var firstRunoff = new double[report.scenarios.Count];
            for (var k = 0; k < bare.Length; k++)
            {
                series[k] = new double[bare[k].RunoffMmH.Length];
                reference[k] = new double[bare[k].RunoffMmH.Length];
                firstRunoff[k] = double.MaxValue;
                // The bare-roof reference: every m2 of the roof, and the part outside the zones behaves like it too.
                for (var i = 0; i < series[k].Length; i++)
                {
                    reference[k][i] = roofArea * bare[k].RunoffMmH[i] / 3600.0;
                    series[k][i] = otherArea * bare[k].RunoffMmH[i] / 3600.0;
                }
                totalRunoff[k] = otherArea * bare[k].RunoffMm / 1000.0;
                referenceRunoff[k] = roofArea * bare[k].RunoffMm / 1000.0;
                if (otherArea > 0 && bare[k].FirstRunoffMin >= 0) firstRunoff[k] = bare[k].FirstRunoffMin;
            }

            var index = 0;
            foreach (var zone in inputs.Zones)
            {
                index++;
                var spec = SpecFor(zone.Assembly);
                var area = zone.Width * zone.Height;
                var result = new ZonePercolationResult
                {
                    id = zone.Id,
                    label = zone.Label,
                    system = zone.Assembly != null ? zone.Assembly.System : "no build-up",
                    category = zone.Assembly != null ? zone.Assembly.Category : "",
                    areaM2 = (float)area,
                    substrateMm = (float)(spec.HasSubstrate ? spec.SubstrateMm : 0),
                    storageSource = spec.StorageSource,
                    publishedStorageMm = (float)spec.PublishedStorageMm,
                    storageAtSaturationMm = (float)StorageAt(spec, spec.ThetaS),
                    storageAtFieldCapacityMm = (float)StorageAt(spec, spec.ThetaFc),
                    extraSubstrateForTargetMm = 0f,
                };

                for (var k = 0; k < report.scenarios.Count; k++)
                {
                    var sc = report.scenarios[k];
                    var run = Simulate(spec, sc);
                    result.scenarios.Add(ScenarioResult(sc, run, bare[k]));

                    for (var i = 0; i < series[k].Length; i++) series[k][i] += area * run.RunoffMmH[i] / 3600.0;
                    totalRunoff[k] += area * run.RunoffMm / 1000.0;
                    if (run.FirstRunoffMin >= 0 && run.FirstRunoffMin < firstRunoff[k]) firstRunoff[k] = run.FirstRunoffMin;
                }

                // Heavy shower retention below the target: how much more substrate would fix it?
                var heavy = result.scenarios[1];
                if (spec.HasSubstrate && heavy.retainedPercent < TargetRetentionPercent)
                {
                    result.extraSubstrateForTargetMm = (float)ExtraFor(spec, report.scenarios[1], true);
                    result.extraDrainageForTargetMm = (float)ExtraFor(spec, report.scenarios[1], false);
                }

                report.zones.Add(result);
            }

            for (var k = 0; k < report.scenarios.Count; k++)
            {
                var sc = report.scenarios[k];
                var peak = 0.0; var peakAt = 0;
                var refPeak = 0.0; var refPeakAt = 0;
                for (var i = 0; i < series[k].Length; i++)
                {
                    if (series[k][i] > peak) { peak = series[k][i]; peakAt = i; }
                    if (reference[k][i] > refPeak) { refPeak = reference[k][i]; refPeakAt = i; }
                }

                var rainM3 = roofArea * sc.depthMm / 1000.0;
                report.roof.Add(new RoofScenarioResult
                {
                    scenario = sc.name,
                    rainMm = sc.depthMm,
                    runoffM3 = (float)totalRunoff[k],
                    referenceRunoffM3 = (float)referenceRunoff[k],
                    retainedPercent = (float)(referenceRunoff[k] > 0 ? 100.0 * (1.0 - totalRunoff[k] / referenceRunoff[k]) : 0),
                    peakFlowLps = (float)peak,
                    referencePeakLps = (float)refPeak,
                    peakReductionPercent = (float)(refPeak > 0 ? 100.0 * (1.0 - peak / refPeak) : 0),
                    peakDelayMin = (float)((peakAt - refPeakAt) * SeriesStepS / 60.0),
                    firstRunoffMin = firstRunoff[k] == double.MaxValue ? -1f : (float)firstRunoff[k],
                    flowLps = ToFloats(series[k]),
                    referenceLps = ToFloats(reference[k]),
                });
            }

            Summarise(report, greenArea, otherArea);
            Recommend(report);
            return report;
        }

        static float[] ToFloats(double[] a)
        {
            var f = new float[a.Length];
            for (var i = 0; i < a.Length; i++) f[i] = (float)a[i];
            return f;
        }

        static double StorageAt(ColumnSpec spec, double theta)
        {
            if (!spec.HasSubstrate) return 0;
            var drainage = DrainageCupsMm(spec.DrainageMm) + (spec.DrainageMm > 0 ? DrainageOverflowMm : 0);
            return spec.SubstrateMm * theta + drainage;
        }

        static ZoneScenarioResult ScenarioResult(RainScenario sc, Run run, Run bare)
        {
            return new ZoneScenarioResult
            {
                scenario = sc.name,
                rainMm = (float)run.RainMm,
                runoffMm = (float)run.RunoffMm,
                retainedPercent = (float)(run.RainMm > 0 ? 100.0 * (1.0 - run.RunoffMm / bare.RunoffMm) : 0),
                peakRunoffMmH = (float)run.PeakMmH,
                peakReductionPercent = (float)(bare.PeakMmH > 0 ? 100.0 * (1.0 - run.PeakMmH / bare.PeakMmH) : 0),
                firstRunoffMin = (float)run.FirstRunoffMin,
                peakDelayMin = run.PeakMmH < 0.05 ? 0f : (float)(run.PeakAtMin - bare.PeakAtMin),   // no peak to speak of: nothing to delay
                maxSubstrateFillPercent = (float)(100.0 * run.MaxFill),
                saturated = run.Saturated,
                surfaceRunoffMm = (float)run.SurfaceMm,
            };
        }

        /// <summary>
        /// Extra thickness (mm, in steps of 10) of the substrate, or of the drainage layer, that brings the heavy shower to
        /// the target retention; -1 if 200 mm more isn't enough. The pore space is kept as it is (a thicker layer of the
        /// same material), so a published-storage calibration stays valid.
        /// </summary>
        static double ExtraFor(ColumnSpec spec, RainScenario heavy, bool substrate)
        {
            var bare = SimulateBare(heavy);
            for (var extra = 10.0; extra <= MaxAddedThicknessMm + 1e-9; extra += 10.0)
            {
                var thicker = new ColumnSpec
                {
                    SubstrateMm = spec.SubstrateMm + (substrate ? extra : 0.0),
                    DrainageMm = spec.DrainageMm + (substrate ? 0.0 : extra),
                    ThetaS = spec.ThetaS, ThetaFc = spec.ThetaFc, ThetaR = spec.ThetaR, KsMmS = spec.KsMmS,
                    InterceptionMm = spec.InterceptionMm, DepressionMm = spec.DepressionMm, HasSubstrate = true,
                    StorageSource = spec.StorageSource, PublishedStorageMm = spec.PublishedStorageMm,
                };
                var run = Simulate(thicker, heavy);
                if (100.0 * (1.0 - run.RunoffMm / bare.RunoffMm) >= TargetRetentionPercent) return extra;
            }
            return -1;
        }

        // ---------------------------------------------------------------- summary + advice

        static string F0(double v) { return v.ToString("0", CultureInfo.InvariantCulture); }
        static string F1(double v) { return v.ToString("0.#", CultureInfo.InvariantCulture); }

        static void Summarise(PercolationReport r, double green, double other)
        {
            var s = new PercolationSummary { zonesChecked = r.zones.Count, greenAreaM2 = (float)green, otherAreaM2 = (float)other };
            if (r.roof.Count >= 3)
            {
                s.heavyShowerRetainedPercent = r.roof[1].retainedPercent;
                s.heavyShowerPeakReductionPercent = r.roof[1].peakReductionPercent;
                s.cloudburstRetainedPercent = r.roof[2].retainedPercent;
                s.cloudburstPeakReductionPercent = r.roof[2].peakReductionPercent;
            }
            foreach (var z in r.zones)
            {
                if (z.substrateMm > 0 && z.scenarios.Count >= 3)
                {
                    if (z.scenarios[1].retainedPercent < TargetRetentionPercent) s.zonesBelowTarget++;
                    if (z.scenarios[2].saturated) s.zonesSaturatedInCloudburst++;
                }
            }
            r.summary = s;
        }

        static List<string> Assumptions()
        {
            return new List<string>
            {
                "Screening model, not a hydrological design: one vertical column per zone (rain, interception, 2 mm surface store, 10 mm substrate cells, drainage layer, outlet).",
                "Rain scenarios are generic (steady 10 mm/h for 3 h; heavy shower 40 mm/h for 30 min; cloudburst 108 mm/h = 300 l/(s.ha) for 10 min), not the site's design rainfall (KOSTRA).",
                "Substrate: saturated water content 0.50 (extensive) or 0.45 (intensive), field capacity 70% and residual 20% of that, saturated conductivity 0.5 or 0.2 mm/s; gravity drains only what is above field capacity, at a rate growing with the square-and-a-half of the excess. Where the provider prints the system's water storage the pore space is calibrated to it.",
                "Start: the substrate is at field capacity (wet, as after earlier rain): the cautious case for stormwater, since a dry start would retain more. The film on the plants (1 mm) and the surface store (2 mm) are held; evaporation during the storm is ignored.",
                "Drainage layer: keeps 0.2 mm of water per mm of thickness in its cups, holds 6 mm of free water while it drains, and releases the rest through a 90 s linear reservoir; the bare-roof reference has a 1 mm film and a 30 s lag.",
                "The part of the roof outside the drawn green zones (courts, paths) is treated as bare roof. The comparison is always the same rain on the same roof with no green layers.",
                "Retention of at least " + F0(TargetRetentionPercent) + "% of the heavy shower is the aim behind the thicker-substrate advice: a planning aid, not a code value.",
            };
        }

        static void Recommend(PercolationReport r)
        {
            foreach (var z in r.zones)
            {
                if (z.substrateMm <= 0)
                {
                    r.recommendations.Add(new PercolationRecommendation
                    {
                        kind = "surface",
                        target = z.label,
                        text = z.label + " (" + z.system + ") has no substrate: rain runs straight off it. It retains nothing; that is what a paved surface does.",
                    });
                    continue;
                }

                var heavy = z.scenarios[1];
                if (heavy.retainedPercent < TargetRetentionPercent)
                {
                    var text = z.label + " (" + z.system + ") retains " + F0(heavy.retainedPercent) + "% of a 40 mm/h shower (aim " + F0(TargetRetentionPercent) + "%). ";
                    var options = new List<string>();
                    if (z.extraDrainageForTargetMm > 0)
                        options.Add("a water-storing drainage layer " + F0(z.extraDrainageForTargetMm) + " mm thicker (about +" + F0(z.extraDrainageForTargetMm * DrainageCupsPerMm) + " mm of stored water, little added weight)");
                    if (z.extraSubstrateForTargetMm > 0)
                        options.Add(F0(z.extraSubstrateForTargetMm) + " mm more substrate (about +" + F0(z.extraSubstrateForTargetMm * (string.Equals(z.category, "intensive", StringComparison.OrdinalIgnoreCase) ? 1.2 : 1.0)) + " kg/m2 dry; check the deck)");
                    text += options.Count > 0
                        ? "That needs " + string.Join(", or ", options.ToArray()) + "."
                        : "Neither 200 mm more substrate nor 200 mm more drainage layer gets there: this build-up can't hold that much water.";
                    r.recommendations.Add(new PercolationRecommendation { kind = "more-storage", target = z.label, text = text });
                }

                var burst = z.scenarios[2];
                if (burst.saturated || burst.surfaceRunoffMm > 0.5f)
                {
                    r.recommendations.Add(new PercolationRecommendation
                    {
                        kind = burst.surfaceRunoffMm > 0.5f ? "surface" : "check-outlets",
                        target = z.label,
                        text = z.label + (burst.surfaceRunoffMm > 0.5f
                            ? ": in a cloudburst " + F1(burst.surfaceRunoffMm) + " mm can't get into the substrate and runs off the surface (crusting, compaction or a substrate that is too tight)."
                            : ": fills up in a cloudburst (some of its substrate reaches saturation), so its runoff then follows the rain: the roof drains still have to take the peak."),
                    });
                }
            }

            if (r.roof.Count >= 3)
            {
                var b = r.roof[2];
                r.recommendations.Add(new PercolationRecommendation
                {
                    kind = "check-outlets",
                    target = "Roof drains",
                    text = "In the cloudburst the roof sheds " + F0(b.peakFlowLps) + " l/s at its peak (" + F0(b.referencePeakLps) + " l/s with no green layers, " + F0(b.peakReductionPercent) +
                           "% less), " + F0(b.retainedPercent) + "% of the rain stays on the roof. Compare the peak with the drains' capacity.",
                });
            }
            if (r.recommendations.Count == 0)
                r.recommendations.Add(new PercolationRecommendation { kind = "fine", target = "All zones", text = "Every zone meets the retention aim and none fills up in a cloudburst." });
        }
    }
}
