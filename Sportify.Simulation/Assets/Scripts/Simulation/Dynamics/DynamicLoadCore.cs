#nullable disable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Sportify.Simulation.Structure;
using Sportify.Simulation.Water;
using Sportify.Simulation.Wind;

namespace Sportify.Simulation.Dynamics
{
    // ---------------------------------------------------------------------------------------
    //  The dynamic half of the structural analysis, in three scenarios that follow one another:
    //
    //    1  CROWDS      people walk in from the entries, settle on the courts, gardens and play areas, and leave, hour
    //                   by hour through a day (an agent simulation with a fixed random seed: the same layout always
    //                   gives the same day). What comes out: who is where and when, the weight of the crowd on each
    //                   bay, and how far the crowd pulls the load's centre off the structure's centre.
    //    2  WEATHER     what rain, snow and wind do to the load on the structure: a cloudburst soaking the build-ups
    //                   (their weight rises as they fill), snow on the whole roof, wind suction lifting the edges.
    //                   Each is a load case with the crowd and the permanent load; the governing case of every bay.
    //    3  RESONANCE   the natural frequency of each bay, and what rhythmic crowd movement (walking, court play, a
    //                   jumping event) does to it: the acceleration at resonance against a comfort limit, and the
    //                   spectrum that shows where the deck and the crowd's harmonics meet.
    //
    //  Like the other analyses it uses nothing from UnityEngine: the numbers appear in the Revit add-in with no Unity,
    //  and Unity films them. It is a SCREENING model, not a structural verification or a vibration design.
    //
    //  Every constant that is a judgement call is named below and repeated in the report's assumptions. The ones that
    //  matter most are not in the layout: the deck's first natural frequency (estimated from the spans unless the
    //  engineer's figure is entered), the site's snow zone and altitude, and the day's schedule.
    //
    //  Units: metres, seconds, kN, kN/m2, Hz, g. Plan coordinates as everywhere: x right, y DOWN from the top edge.
    // ---------------------------------------------------------------------------------------

    public class DynamicInputs
    {
        public StructureInputs Structure;        // the pieces, the grid, the columns, the entries, the capacity
        public WindInputs Wind;                  // roof size, wind zone, height: for the wind case
        public string SnowZone;                  // "1", "1a", "2", "2a", "3"; null = not given
        public string SnowZoneSource = "";
        public double? AltitudeM;                // of the site above sea level; null = not given
        public string Schedule;                  // "sports_day" | "event_day" | "community_day"; null = sports_day
        public double? NaturalFrequencyHz;       // the engineer's first natural frequency of the deck; null = estimate from the spans
        public double? WalkingLimitG;            // comfort limits the designer set, in g; null = the built-in ones
        public double? RhythmicLimitG;
    }

    // ------------------------------------------------------------------------ the day

    public sealed class DaySchedule
    {
        public const int FirstHour = 6;
        public const int Hours = 17;             // 06:00 to 23:00, in hourly steps

        public string Key, Name;
        public double[] Play, Spectate, Visit;   // share of the full number of players / seated spectators / garden visitors, per hour
        public string[] Phases;                  // what the hour is called; "" = same as before

        public static DaySchedule Get(string key)
        {
            switch ((key ?? "").ToLowerInvariant())
            {
                case "event_day":
                    return new DaySchedule
                    {
                        Key = "event_day", Name = "Evening event with full stands",
                        Play = new[] { 0, 0, 0, 0, 0, 0, 0, 0, 0.2, 0.4, 0.5, 0.6, 0.8, 1, 1, 0.2, 0 },
                        Spectate = new[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0.1, 0.3, 0.5, 0.9, 1, 1, 0.3, 0 },
                        Visit = new[] { 0, 0, 0, 0, 0, 0, 0.2, 0.3, 0.3, 0.3, 0.4, 0.6, 0.8, 0.9, 0.6, 0.2, 0 },
                        Phases = new[] { "", "", "", "", "", "", "Quiet morning", "", "Warm-up on the courts", "", "Teams arrive", "", "Doors open, stands fill", "Match", "", "Final whistle, leaving", "Closing" },
                    };
                case "community_day":
                    return new DaySchedule
                    {
                        Key = "community_day", Name = "Community garden day",
                        Play = new[] { 0, 0, 0.1, 0.2, 0.3, 0.3, 0.2, 0.3, 0.4, 0.4, 0.5, 0.4, 0.3, 0.2, 0.1, 0, 0 },
                        Spectate = new[] { 0, 0, 0, 0, 0, 0, 0, 0, 0.1, 0.1, 0.1, 0.1, 0, 0, 0, 0, 0 },
                        Visit = new[] { 0, 0.1, 0.4, 0.6, 0.8, 0.9, 1, 0.9, 0.8, 0.7, 0.6, 0.5, 0.3, 0.1, 0, 0, 0 },
                        Phases = new[] { "", "Gardeners arrive", "Morning in the gardens", "", "", "", "Lunch in the gardens", "", "Afternoon", "", "Family time", "", "Evening walkers", "", "Leaving", "", "Closing" },
                    };
                default:
                    return new DaySchedule
                    {
                        Key = "sports_day", Name = "School and club sports day",
                        Play = new[] { 0, 0.1, 0.6, 0.7, 0.7, 0.7, 0.3, 0.5, 0.6, 0.8, 0.9, 0.9, 1, 1, 0.9, 0.2, 0 },
                        Spectate = new[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0.1, 0.2, 0.3, 0.6, 1, 0.8, 0, 0 },
                        Visit = new[] { 0, 0.1, 0.2, 0.3, 0.3, 0.4, 0.9, 0.6, 0.4, 0.4, 0.5, 0.6, 0.5, 0.3, 0.1, 0, 0 },
                        Phases = new[] { "Doors open", "Early training", "School PE", "", "", "", "Lunch break in the gardens", "School PE", "", "Club training", "", "", "Evening league, spectators arrive", "League match", "", "Last games, leaving", "Closing" },
                    };
            }
        }

        public string PhaseAt(int hourIndex)
        {
            for (var i = Math.Min(Hours - 1, Math.Max(0, hourIndex)); i >= 0; i--)
                if (!string.IsNullOrEmpty(Phases[i])) return Phases[i];
            return "";
        }
    }

    // ------------------------------------------------------------------------ results (serialised by Unity's JsonUtility)

    [Serializable]
    public class DaySample
    {
        public float hour, persons, crowdKn;
        public float offsetXPercent, offsetYPercent;    // where the load's centre is with the crowd on the roof, off the structure's centre (of the length / width)
        public float shiftXPercent, shiftYPercent;      // how far the crowd alone moved it from where the permanent load has it
    }

    [Serializable]
    public class CrowdBay
    {
        public string label;
        public float peakPersons, peakAtHour, meanPersons;
        public float peakDensity;                       // persons per m2
        public float peakCrowdKnM2;                     // what that crowd weighs
        public float designImposedKnM2;                 // what the static analysis designs the same bay for (the code's imposed load)
        public float peakShareOfDesignPercent;
    }

    [Serializable]
    public class CrowdReport
    {
        public string schedule, scheduleName;
        public float startHour, endHour;
        public List<DaySample> samples = new List<DaySample>();
        public List<CrowdBay> bays = new List<CrowdBay>();
        public float peakPersons, peakAtHour, meanPersons, peakCrowdKn;
        public int arrivals;
        public float staticExpectedPersons;             // the static analysis's "everybody at once"
        public string busiestBay;
        public float busiestBayPeakDensity;
        public float baselineOffsetXPercent, baselineOffsetYPercent;   // the permanent load's own offset, with nobody on the roof
        public float maxShiftPercent, maxShiftAtHour;                 // the most the crowd moves the load's centre
        public string maxShiftSide;
    }

    [Serializable]
    public class SnowInfo
    {
        public string zone, source;
        public bool zoneAssumed, altitudeAssumed;
        public float altitudeM, skKnM2, shapeCoefficient, roofKnM2;
    }

    [Serializable]
    public class RainInfo
    {
        public float intensityMmH, minutes;
        public float dryKn, fieldCapacityKn, saturatedKn;   // the build-ups' weight: dry, at field capacity (where the storm starts), saturated (what the static analysis carries)
        public float peakAddedKn, peakAtMin, peakKn;
        public float seriesStepMin;
        public float[] addedKnSeries = new float[0];
    }

    [Serializable]
    public class WindInfo
    {
        public string zone, worstDirection;
        public float peakPressurePa, roofHeightM, worstTowardAngleDeg;
        public float maxSuctionKnM2, meanSuctionKnM2;
        public int baysNetUplift;
    }

    [Serializable]
    public class LoadCase
    {
        public string key, name, description;
        public float totalKn, peakUtilisation;
        public string worstBay;
        public int baysOver;
        public float offsetXPercent, offsetYPercent;
        public float[] bayKnM2 = new float[0];
        public float[] bayUtilisation = new float[0];
    }

    [Serializable]
    public class GoverningBay
    {
        public string label, governingCase;
        public float utilisation, designUtilisation, deadKnM2;
    }

    [Serializable]
    public class WeatherReport
    {
        public SnowInfo snow = new SnowInfo();
        public RainInfo rain = new RainInfo();
        public WindInfo wind = new WindInfo();
        public List<LoadCase> cases = new List<LoadCase>();
        public List<GoverningBay> governing = new List<GoverningBay>();
        public string worstCase, worstBay;
        public float peakUtilisation;
    }

    [Serializable]
    public class ActivityDef
    {
        public string key, name, description;
        public float fpLowHz, fpHighHz, limitG, sync, contactRatio;
        public float[] alpha = new float[0];            // dynamic load factors of harmonics 1..n
    }

    [Serializable]
    public class ActivityResponse
    {
        public string activity, status;
        public float participants, forcePa;
        public float accelerationG, accelerationBandG, limitG, ratio, worstFpHz;
        public int worstHarmonic;
    }

    [Serializable]
    public class BayResonance
    {
        public string label;
        public float spanM, areaM2, depthM, massKgM2;
        public float frequencyHz, frequencyLowHz, frequencyHighHz;
        public List<ActivityResponse> activities = new List<ActivityResponse>();
        public float worstRatio;
        public string worstActivity, status;
    }

    [Serializable]
    public class SpectrumCurve
    {
        public string activity;
        public float[] accelerationG = new float[0];
    }

    [Serializable]
    public class ResonanceReport
    {
        public bool estimated;
        public float dampingRatio, youngGPa, bandLow, bandHigh;
        public List<ActivityDef> activities = new List<ActivityDef>();
        public List<BayResonance> bays = new List<BayResonance>();
        public string worstBay, worstActivity;
        public float worstAccelerationG, worstLimitG, worstRatio, worstFrequencyHz, worstFpHz;
        public int worstHarmonic, baysExceeding, baysMarginal;
        public float lowestFrequencyHz, highestFrequencyHz;
        public float spectrumFromHz, spectrumStepHz;
        public List<SpectrumCurve> spectrum = new List<SpectrumCurve>();     // acceleration against the deck's natural frequency, for the worst bay's loading
        public float sweepFromHz, sweepStepHz;
        public string sweepActivity;
        public float[] sweepG = new float[0];                                // the worst bay's acceleration as the crowd's rhythm sweeps its range
    }

    [Serializable]
    public class DynamicRecommendation
    {
        public string scenario;                         // crowds | weather | resonance | general
        public string kind;
        public string target, text;
    }

    [Serializable]
    public class DynamicSummary
    {
        public float peakPersons, peakAtHour;
        public float maxShiftPercent, crowdSharePercent, capacityKnM2;
        public string worstCase, worstCaseBay;
        public float worstCaseUtilisation;
        public float skKnM2;
        public float lowestFrequencyHz;
        public string worstResonanceBay, worstResonanceActivity;
        public float worstAccelerationG, worstResonanceRatio;
        public int baysResonanceExceeding, baysOverCapacity;
        public bool frequencyEstimated, snowAssumed, capacityAssumed;
        public bool preliminary;                        // some input is still a built-in value nobody confirmed
        public string preliminaryNote = "";             // which ones, for the top of a video and a dialog
        public string acceptedNote = "";                // which built-in values the designer accepted
    }

    [Serializable]
    public class DynamicReport
    {
        public bool ran;
        public List<string> assumptions = new List<string>();
        public List<AssumptionUse> assumptionUses = new List<AssumptionUse>();   // the inputs behind the numbers and whether the designer confirmed them
        public DynamicSummary summary = new DynamicSummary();
        public CrowdReport crowd = new CrowdReport();
        public WeatherReport weather = new WeatherReport();
        public ResonanceReport resonance = new ResonanceReport();
        public List<DynamicRecommendation> recommendations = new List<DynamicRecommendation>();
    }

    // ------------------------------------------------------------------------ the crowd

    public struct Agent
    {
        public double X, Y, Tx, Ty;
        public int Dest;          // index into CrowdSim.Destinations
        public int State;         // 0 walking in, 1 settled (wanders about its piece), 2 leaving
        public double NextPickS;
    }

    public sealed class Destination
    {
        public int Item;
        public LoadKind Kind;
        public string Label;
        public double X, Y, Width, Height;
        public double Players, Seats, Persons;
    }

    /// <summary>
    /// The people of one day, stepped a second at a time. The analysis runs it to the end; the video steps another instance in
    /// lockstep and draws its agents, so what is filmed is the computation. Only integer arithmetic drives the random choices
    /// and the walking is straight lines, so the same layout gives the same day everywhere.
    /// </summary>
    public sealed class CrowdSim
    {
        public const double WalkMs = 1.3;
        public const double WanderMs = 0.8;
        public const double ArrivalSpreadS = 60.0;      // a deficit of n people is filled at about n/60 per second
        public const double RepickS = 90.0;
        public const double StartS = DaySchedule.FirstHour * 3600.0;
        public const double EndS = (DaySchedule.FirstHour + DaySchedule.Hours) * 3600.0;
        public const double PersonKn = 90.0 * 9.81 / 1000.0;

        public readonly DaySchedule Schedule;
        public readonly List<Destination> Destinations = new List<Destination>();
        public readonly List<Agent> Agents = new List<Agent>();
        public readonly double RoofLength, RoofWidth;
        public double TimeS;
        public int Arrivals, Departures;
        public double[] Dwell;                          // person-seconds per cell of the load field, for the "where people spend the day" picture
        public readonly int Nx, Ny;

        readonly double[][] _entries;
        readonly int[] _assigned;
        uint _rng = 2463534242u;
        int _spawn;

        public CrowdSim(StructureInputs inputs, DaySchedule schedule)
        {
            Schedule = schedule;
            RoofLength = inputs.RoofLength;
            RoofWidth = inputs.RoofWidth;
            Nx = Math.Max(1, (int)Math.Floor(RoofLength / StructureModel.CellM + 0.5));
            Ny = Math.Max(1, (int)Math.Floor(RoofWidth / StructureModel.CellM + 0.5));
            Dwell = new double[Nx * Ny];
            TimeS = StartS;

            for (var i = 0; i < inputs.Items.Count; i++)
            {
                var it = inputs.Items[i];
                if (it.Kind == LoadKind.Tree || it.Persons <= 0 || it.Width <= 0 || it.Height <= 0) continue;
                Destinations.Add(new Destination { Item = i, Kind = it.Kind, Label = it.Label, X = it.X, Y = it.Y, Width = it.Width, Height = it.Height, Players = it.Players, Seats = it.Seats, Persons = it.Persons });
            }
            _assigned = new int[Destinations.Count];

            _entries = inputs.Entries.Count > 0 ? inputs.Entries.ToArray()
                : new[] { new[] { RoofLength / 2, 0.0 }, new[] { RoofLength / 2, RoofWidth }, new[] { 0.0, RoofWidth / 2 }, new[] { RoofLength, RoofWidth / 2 } };
        }

        public double Hour { get { return TimeS / 3600.0; } }
        public int PersonsOnRoof { get { return Agents.Count; } }

        double Rand()
        {
            _rng ^= _rng << 13; _rng ^= _rng >> 17; _rng ^= _rng << 5;
            return (_rng >> 8) / 16777216.0;
        }

        static int Round(double v) { return (int)Math.Floor(v + 0.5); }

        public int Target(int dest, int hourIndex)
        {
            var d = Destinations[dest];
            if (hourIndex < 0 || hourIndex >= DaySchedule.Hours) return 0;
            switch (d.Kind)
            {
                case LoadKind.Court: return Round(d.Players * Schedule.Play[hourIndex] + d.Seats * Schedule.Spectate[hourIndex]);
                case LoadKind.Activity: return Round(d.Persons * Schedule.Play[hourIndex]);
                default: return Round(d.Persons * Schedule.Visit[hourIndex]);
            }
        }

        public int HourIndex { get { return (int)Math.Floor(TimeS / 3600.0) - DaySchedule.FirstHour; } }

        public int Assigned(int dest) { return _assigned[dest]; }

        double[] NearestEntry(double x, double y)
        {
            double[] best = _entries[0];
            var bd = double.MaxValue;
            foreach (var e in _entries)
            {
                var d = (e[0] - x) * (e[0] - x) + (e[1] - y) * (e[1] - y);
                if (d < bd) { bd = d; best = e; }
            }
            return best;
        }

        void PointIn(Destination d, out double x, out double y)
        {
            x = d.X + (0.1 + 0.8 * Rand()) * d.Width;
            y = d.Y + (0.1 + 0.8 * Rand()) * d.Height;
        }

        public void Step(double dt)
        {
            var hi = HourIndex;

            // people arrive where there are too few, and leave where there are too many
            for (var d = 0; d < Destinations.Count; d++)
            {
                var deficit = Target(d, hi) - _assigned[d];
                if (deficit > 0)
                {
                    if (Rand() < Math.Min(1.0, deficit / ArrivalSpreadS))
                    {
                        var e = _entries[_spawn++ % _entries.Length];
                        var a = new Agent { X = e[0], Y = e[1], Dest = d, State = 0 };
                        PointIn(Destinations[d], out a.Tx, out a.Ty);
                        Agents.Add(a);
                        _assigned[d]++;
                        Arrivals++;
                    }
                }
                else if (deficit < 0 && Rand() < Math.Min(1.0, -deficit / ArrivalSpreadS))
                {
                    for (var i = Agents.Count - 1; i >= 0; i--)
                    {
                        var a = Agents[i];
                        if (a.Dest != d || a.State == 2) continue;
                        var e = NearestEntry(a.X, a.Y);
                        a.State = 2; a.Tx = e[0]; a.Ty = e[1];
                        Agents[i] = a;
                        _assigned[d]--;
                        break;
                    }
                }
            }

            // everybody moves; the settled wander about their piece
            var write = 0;
            for (var i = 0; i < Agents.Count; i++)
            {
                var a = Agents[i];
                if (a.State == 1 && TimeS >= a.NextPickS)
                {
                    PointIn(Destinations[a.Dest], out a.Tx, out a.Ty);
                    a.NextPickS = TimeS + RepickS * (0.5 + Rand());
                }

                var dx = a.Tx - a.X;
                var dy = a.Ty - a.Y;
                var dist = Math.Sqrt(dx * dx + dy * dy);
                var step = (a.State == 1 ? WanderMs : WalkMs) * dt;
                if (dist <= step)
                {
                    a.X = a.Tx; a.Y = a.Ty;
                    if (a.State == 0) { a.State = 1; a.NextPickS = TimeS + RepickS * (0.5 + Rand()); }
                    else if (a.State == 2) { Departures++; continue; }
                }
                else
                {
                    a.X += dx / dist * step;
                    a.Y += dy / dist * step;
                }

                var ix = Math.Min(Nx - 1, Math.Max(0, (int)Math.Floor(a.X / RoofLength * Nx)));
                var iy = Math.Min(Ny - 1, Math.Max(0, (int)Math.Floor(a.Y / RoofWidth * Ny)));
                Dwell[iy * Nx + ix] += dt;
                Agents[write++] = a;
            }
            if (write < Agents.Count) Agents.RemoveRange(write, Agents.Count - write);
            TimeS += dt;
        }
    }

    public sealed class AgentSnapshot
    {
        public double X, Y;
        public LoadKind Kind;
    }

    /// <summary>What the crowd run leaves for the other scenarios: the roof at its busiest moment.</summary>
    public sealed class DynamicRun
    {
        public StructureReport Static;
        public LoadField Field;
        public double[] Xs, Ys;
        public double CentreX, CentreY;
        public double PeakTimeS;
        public List<AgentSnapshot> PeakAgents = new List<AgentSnapshot>();
        public double[] PeakBayPersons, PeakBayPlayPersons;
        public Dictionary<string, double> ZoneDryKnM2 = new Dictionary<string, double>();
        public Dictionary<string, double> ZoneFieldCapacityKnM2 = new Dictionary<string, double>();
        public Dictionary<string, double> ZoneRainPeakKnM2 = new Dictionary<string, double>();
        public double[] DeadFieldKnM2Bay;               // typical (field capacity) permanent load per bay, kN/m2
    }

    public static class DynamicModel
    {
        // --- constants that are judgement calls (all repeated in the report) ---
        public const double Gravity = 9.81;
        public const double CrowdSampleS = 60.0;
        public const double SeriesSampleS = 600.0;
        public const double RainIntensityMmH = 108.0;             // the cloudburst of the rain analysis
        public const double RainMinutes = 10.0;
        public const double RainTotalMinutes = 180.0;
        public const double SnowShapeCoefficient = 0.8;           // EN 1991-1-3: flat roof, mu1
        public const double AssumedSnowAltitudeM = 100.0;
        public const string AssumedSnowZone = "2";
        public const double PsiCrowd = 0.7;                       // EN 1990 Annex A1, category C (assembly): combination value
        public const double PsiSnow = 0.5;                        // EN 1990 Annex A1, snow (site below 1000 m)
        public const double OverCapacityFrom = 1.0;
        public const double YoungGPa = 30.0;                      // reinforced concrete deck
        public const double ConcreteKgM3 = 2500.0;
        public const double SpanToDepth = 25.0;                   // deck depth = the bay's long span / 25 (clamped)
        public const double MinDepthM = 0.20, MaxDepthM = 0.60;
        public const double Damping = 0.03;                       // finished floor
        public const double BandLow = 0.80, BandHigh = 1.25;      // an estimated frequency is good to about this much
        public const double RhythmicDensity = 0.25;               // persons per m2 in a rhythmic crowd (Bachmann & Ammann)
        public const double PersonN = 90.0 * 9.81;
        public const double FpStepHz = 0.05;

        /// <summary>The activities with the built-in comfort limits.</summary>
        public static readonly ActivityDef[] Activities = MakeActivities(AnalysisAssumptions.DefaultComfortWalkingG, AnalysisAssumptions.DefaultComfortRhythmicG);

        /// <summary>The activities with the designer's comfort limits where they set them, else the built-in ones.</summary>
        public static ActivityDef[] ActivitiesFor(DynamicInputs inputs)
        {
            var w = inputs.WalkingLimitG.HasValue && inputs.WalkingLimitG.Value > 0 ? inputs.WalkingLimitG.Value : AnalysisAssumptions.DefaultComfortWalkingG;
            var r = inputs.RhythmicLimitG.HasValue && inputs.RhythmicLimitG.Value > 0 ? inputs.RhythmicLimitG.Value : AnalysisAssumptions.DefaultComfortRhythmicG;
            return MakeActivities(w, r);
        }

        static ActivityDef[] MakeActivities(double walkingLimitG, double rhythmicLimitG)
        {
            return new[]
            {
                new ActivityDef { key = "walking", name = "Walking", description = "People walking about the roof, not in step", fpLowHz = 1.6f, fpHighHz = 2.4f, limitG = (float)walkingLimitG, sync = 0f, contactRatio = 0f, alpha = new[] { 0.4f, 0.1f, 0.1f, 0f } },
                new ActivityDef { key = "play", name = "Court play", description = "Players and spectators moving on the courts and play areas", fpLowHz = 1.5f, fpHighHz = 3.0f, limitG = (float)rhythmicLimitG, sync = 0.2f, contactRatio = 0.5f, alpha = FourierAlphas(0.5, 4) },
                new ActivityDef { key = "event", name = "Jumping event", description = "A crowd jumping to a beat on the courts and play areas (a full-house celebration), 0.25 people per m2", fpLowHz = 1.5f, fpHighHz = 2.8f, limitG = (float)rhythmicLimitG, sync = 0.6f, contactRatio = 1f / 3f, alpha = FourierAlphas(1.0 / 3.0, 4) },
            };
        }

        /// <summary>
        /// Amplitudes, as multiples of a person's weight, of the harmonics of jumping: a train of half-sine pulses (Bachmann and
        /// Ammann) with contact ratio a (the share of each period the feet are on the floor) whose mean is the weight:
        /// alpha_k = 2 |cos(pi k a)| / |1 - 4 k^2 a^2|, and pi/2 where that is 0/0 (a = 1/(2k)). a = 1/3 gives 1.8, 1.29, 0.67.
        /// </summary>
        public static float[] FourierAlphas(double a, int n)
        {
            var r = new float[n];
            for (var k = 1; k <= n; k++)
            {
                var den = 1.0 - 4.0 * k * k * a * a;
                r[k - 1] = (float)(Math.Abs(den) < 1e-9 ? Math.PI / 2.0 : 2.0 * Math.Abs(Math.Cos(Math.PI * k * a)) / Math.Abs(den));
            }
            return r;
        }

        // ---------------------------------------------------------------- snow (DIN EN 1991-1-3/NA)

        /// <summary>The characteristic ground snow load sk (kN/m2) for a German snow zone and a site altitude, and null for a zone that does not exist.</summary>
        public static double? SnowGroundLoad(string zone, double altitudeM)
        {
            var q = Math.Pow((altitudeM + 140.0) / 760.0, 2);
            switch ((zone ?? "").Trim().ToLowerInvariant())
            {
                case "1": return Math.Max(0.65, 0.19 + 0.91 * q);
                case "1a": return 1.25 * Math.Max(0.65, 0.19 + 0.91 * q);
                case "2": return Math.Max(0.85, 0.25 + 1.91 * q);
                case "2a": return 1.25 * Math.Max(0.85, 0.25 + 1.91 * q);
                case "3": return Math.Max(1.10, 0.31 + 2.91 * q);
                default: return null;
            }
        }

        // ---------------------------------------------------------------- resonance (single mode per bay)

        /// <summary>Dynamic magnification of a damped oscillator driven at frequency ratio r.</summary>
        public static double Magnification(double r, double zeta)
        {
            var a = 1.0 - r * r;
            return 1.0 / Math.Sqrt(a * a + 4.0 * zeta * zeta * r * r);
        }

        /// <summary>
        /// Peak acceleration (g) at the middle of a bay strip whose first mode is sin(pi s / L), when a rhythmic crowd drives it at
        /// frequency fp: the harmonics k fp with amplitudes alpha_k, a uniform force of forcePa (N/m2, the participants' weight over
        /// the bay) times alpha_k, a mass of massKgM2. Each harmonic's acceleration is (4/pi) (q/m) alpha r^2 D(r); they add as the
        /// square root of the sum of squares (independent phases).
        /// </summary>
        public static double AccelerationG(float[] alpha, double forcePa, double massKgM2, double fnHz, double fpHz, double zeta)
        {
            double sum = 0;
            for (var k = 1; k <= alpha.Length; k++)
            {
                var r = k * fpHz / fnHz;
                var a = 4.0 / Math.PI * (forcePa / massKgM2) * alpha[k - 1] * r * r * Magnification(r, zeta);
                sum += a * a;
            }
            return Math.Sqrt(sum) / Gravity;
        }

        /// <summary>The strip's first natural frequency: (pi/2) sqrt(E I / (m L^4)) for a simply supported strip of the given depth.</summary>
        public static double FrequencyHz(double spanM, double depthM, double massKgM2)
        {
            var ei = YoungGPa * 1e9 * depthM * depthM * depthM / 12.0;
            return Math.PI / 2.0 * Math.Sqrt(ei / (massKgM2 * Math.Pow(spanM, 4)));
        }

        // ---------------------------------------------------------------- the analysis

        public static DynamicReport Analyse(DynamicInputs inputs)
        {
            DynamicRun run;
            return Analyse(inputs, out run);
        }

        public static DynamicReport Analyse(DynamicInputs inputs, out DynamicRun run)
        {
            var report = new DynamicReport { ran = true };
            var st = inputs.Structure;
            run = new DynamicRun { Static = StructureModel.Compute(st), Field = StructureModel.BuildField(st) };
            StructureModel.BayBounds(st, out run.Xs, out run.Ys);
            run.CentreX = run.Static.balance.centreX;
            run.CentreY = run.Static.balance.centreY;

            report.crowd = AnalyseCrowd(inputs, run);
            report.weather = AnalyseWeather(inputs, run, report.crowd);
            report.resonance = AnalyseResonance(inputs, run);
            report.assumptionUses = DescribeInputs(inputs, report, run);
            Summarise(inputs, report);
            report.assumptions = Assumptions(inputs, report);
            report.assumptions.AddRange(AnalysisAssumptions.Lines(report.assumptionUses));
            Recommend(inputs, report, run);
            return report;
        }

        // ---------------------------------------------------------------- scenario 1: crowds

        static int BayOf(DynamicRun run, double x, double y)
        {
            var nbx = run.Xs.Length - 1;
            var c = 0;
            while (c < nbx - 1 && x >= run.Xs[c + 1]) c++;
            var r = 0;
            while (r < run.Ys.Length - 2 && y >= run.Ys[r + 1]) r++;
            return r * nbx + c;
        }

        static CrowdReport AnalyseCrowd(DynamicInputs inputs, DynamicRun run)
        {
            var st = inputs.Structure;
            var sched = DaySchedule.Get(inputs.Schedule);
            var sim = new CrowdSim(st, sched);
            var nb = run.Static.bays.Count;
            var report = new CrowdReport
            {
                schedule = sched.Key, scheduleName = sched.Name,
                startHour = DaySchedule.FirstHour, endHour = DaySchedule.FirstHour + DaySchedule.Hours,
                staticExpectedPersons = run.Static.summary.expectedPersons,
            };

            double deadSum = 0, deadX = 0, deadY = 0;
            var f = run.Field;
            for (var iy = 0; iy < f.Ny; iy++)
                for (var ix = 0; ix < f.Nx; ix++)
                {
                    var d = f.Dead[f.Index(ix, iy)];
                    deadSum += d; deadX += d * (ix + 0.5) * f.CellW; deadY += d * (iy + 0.5) * f.CellH;
                }

            var peakBay = new double[nb];
            var peakAt = new double[nb];
            var sumBay = new double[nb];
            var counts = new double[nb];
            var samples = 0;
            var peakKn = -1.0;
            double personSum = 0;
            var maxShift = 0.0;
            var maxShiftAt = 0.0;
            var maxSide = "";
            var baseX = (deadX / deadSum - run.CentreX) / st.RoofLength;
            var baseY = (deadY / deadSum - run.CentreY) / st.RoofWidth;

            var steps = (int)((CrowdSim.EndS - CrowdSim.StartS) / 1.0);
            var nextBay = 0.0;
            var nextSeries = 0.0;
            for (var s = 0; s < steps; s++)
            {
                var t = s;   // seconds since the start
                if (t >= nextBay)
                {
                    nextBay += CrowdSampleS;
                    samples++;
                    for (var b = 0; b < nb; b++) counts[b] = 0;
                    double mx = 0, my = 0;
                    foreach (var a in sim.Agents)
                    {
                        counts[BayOf(run, a.X, a.Y)]++;
                        mx += a.X; my += a.Y;
                    }
                    for (var b = 0; b < nb; b++)
                    {
                        sumBay[b] += counts[b];
                        if (counts[b] > peakBay[b]) { peakBay[b] = counts[b]; peakAt[b] = sim.Hour; }
                    }

                    var n = sim.Agents.Count;
                    personSum += n;
                    var kn = n * CrowdSim.PersonKn;
                    var total = deadSum + kn;
                    var offX = ((deadX + mx * CrowdSim.PersonKn) / total - run.CentreX) / st.RoofLength;
                    var offY = ((deadY + my * CrowdSim.PersonKn) / total - run.CentreY) / st.RoofWidth;
                    var shiftX = offX - baseX;
                    var shiftY = offY - baseY;
                    if (Math.Max(Math.Abs(shiftX), Math.Abs(shiftY)) > maxShift)
                    {
                        maxShift = Math.Max(Math.Abs(shiftX), Math.Abs(shiftY)); maxShiftAt = sim.Hour;
                        maxSide = Math.Abs(shiftX) >= Math.Abs(shiftY) ? (shiftX > 0 ? "right" : "left") : (shiftY > 0 ? "bottom" : "top");
                    }

                    if (kn > peakKn)
                    {
                        peakKn = kn;
                        run.PeakTimeS = sim.TimeS;
                        run.PeakAgents.Clear();
                        run.PeakBayPersons = (double[])counts.Clone();
                        run.PeakBayPlayPersons = new double[nb];
                        foreach (var a in sim.Agents)
                        {
                            var kind = sim.Destinations[a.Dest].Kind;
                            run.PeakAgents.Add(new AgentSnapshot { X = a.X, Y = a.Y, Kind = kind });
                            if (kind != LoadKind.Zone) run.PeakBayPlayPersons[BayOf(run, a.X, a.Y)]++;
                        }
                    }

                    if (t >= nextSeries)
                    {
                        nextSeries += SeriesSampleS;
                        report.samples.Add(new DaySample { hour = (float)sim.Hour, persons = n, crowdKn = (float)kn, offsetXPercent = (float)(offX * 100), offsetYPercent = (float)(offY * 100), shiftXPercent = (float)(shiftX * 100), shiftYPercent = (float)(shiftY * 100) });
                    }
                }
                sim.Step(1.0);
            }
            if (run.PeakBayPersons == null) { run.PeakBayPersons = new double[nb]; run.PeakBayPlayPersons = new double[nb]; }

            report.arrivals = sim.Arrivals;
            report.peakCrowdKn = (float)Math.Max(0, peakKn);
            report.peakPersons = (float)Math.Round(Math.Max(0, peakKn) / CrowdSim.PersonKn);
            report.peakAtHour = (float)(run.PeakTimeS / 3600.0);
            report.meanPersons = (float)(personSum / Math.Max(1, samples));
            report.baselineOffsetXPercent = (float)(baseX * 100);
            report.baselineOffsetYPercent = (float)(baseY * 100);
            report.maxShiftPercent = (float)(maxShift * 100);
            report.maxShiftAtHour = (float)maxShiftAt;
            report.maxShiftSide = maxSide;

            var busiestDensity = -1.0;
            for (var b = 0; b < nb; b++)
            {
                var bay = run.Static.bays[b];
                var area = bay.areaM2;
                var cb = new CrowdBay
                {
                    label = bay.label, peakPersons = (float)peakBay[b], peakAtHour = (float)peakAt[b], meanPersons = (float)(sumBay[b] / Math.Max(1, samples)),
                    peakDensity = (float)(peakBay[b] / area), peakCrowdKnM2 = (float)(peakBay[b] * CrowdSim.PersonKn / area),
                    designImposedKnM2 = (float)(bay.liveKn / area),
                };
                cb.peakShareOfDesignPercent = cb.designImposedKnM2 > 0 ? (float)(100.0 * cb.peakCrowdKnM2 / cb.designImposedKnM2) : 0f;
                report.bays.Add(cb);
                if (cb.peakDensity > busiestDensity * (1 + 1e-4) + 1e-12) { busiestDensity = cb.peakDensity; report.busiestBay = bay.label; report.busiestBayPeakDensity = cb.peakDensity; }
            }
            return report;
        }

        // ---------------------------------------------------------------- scenario 2: weather

        /// <summary>A copy of the pieces with each green-roof zone at the weight the function gives (kN/m2), everything else as it was.</summary>
        static StructureInputs WithZoneWeights(StructureInputs inputs, Func<string, double> weightKnM2)
        {
            var copy = inputs.CopyWithItems();
            foreach (var it in copy.Items)
                if (it.Kind == LoadKind.Zone) it.DeadKnM2 = weightKnM2(it.Id);
            return copy;
        }

        /// <summary>The wind's vertical load on every cell (kN, negative = suction lifting the roof) for a wind blowing toward dirDeg.</summary>
        public static double[] WindCells(DynamicInputs inputs, WindSite site, double dirDeg, LoadField f)
        {
            var cells = new double[f.Nx * f.Ny];
            var cellArea = f.CellW * f.CellH;
            for (var iy = 0; iy < f.Ny; iy++)
                for (var ix = 0; ix < f.Nx; ix++)
                {
                    var zone = WindModel.Classify((ix + 0.5) * f.CellW, (iy + 0.5) * f.CellH, dirDeg, f.Nx * f.CellW, f.Ny * f.CellH, site.RoofElevation);
                    cells[f.Index(ix, iy)] = WindModel.Cpe(zone) * site.QRoof / 1000.0 * cellArea;   // cpe is negative: suction
                }
            return cells;
        }

        /// <summary>The people of the busiest moment as point loads on the cells (kN).</summary>
        public static double[] CrowdCells(DynamicRun run)
        {
            var f = run.Field;
            var cells = new double[f.Nx * f.Ny];
            foreach (var a in run.PeakAgents)
            {
                var ix = Math.Min(f.Nx - 1, Math.Max(0, (int)Math.Floor(a.X / f.CellW)));
                var iy = Math.Min(f.Ny - 1, Math.Max(0, (int)Math.Floor(a.Y / f.CellH)));
                cells[f.Index(ix, iy)] += CrowdSim.PersonKn;
            }
            return cells;
        }

        static WindInputs WindOf(DynamicInputs inputs)
        {
            return inputs.Wind ?? new WindInputs { RoofLength = inputs.Structure.RoofLength, RoofWidth = inputs.Structure.RoofWidth };
        }

        static double[] Scaled(double[] v, double s)
        {
            var r = new double[v.Length];
            for (var i = 0; i < v.Length; i++) r[i] = v[i] * s;
            return r;
        }

        static double[] Sum(params double[][] parts)
        {
            var r = new double[parts[0].Length];
            foreach (var p in parts)
                for (var i = 0; i < r.Length; i++) r[i] += p[i];
            return r;
        }

        /// <summary>
        /// The permanent load of the roof in one state of its build-ups, per cell (kN): the layout's own pieces with every green-roof
        /// zone at the weight of that state ("dry", "field" = at field capacity, "saturated" = what the static analysis carries,
        /// "rain" = at the wettest moment of the cloudburst).
        /// </summary>
        public static double[] DeadCells(DynamicInputs inputs, DynamicRun run, string state)
        {
            var zoneKnM2 = new Func<string, double>(id =>
            {
                double v;
                var it = inputs.Structure.Items.First(i => i.Id == id);
                switch (state)
                {
                    case "dry": return run.ZoneDryKnM2.TryGetValue(id, out v) ? v : it.DeadKnM2;
                    case "field": return run.ZoneFieldCapacityKnM2.TryGetValue(id, out v) ? v : it.DeadKnM2;
                    case "rain": return run.ZoneRainPeakKnM2.TryGetValue(id, out v) ? v : it.DeadKnM2;
                    default: return it.DeadKnM2;
                }
            });
            return StructureModel.BuildField(WithZoneWeights(inputs.Structure, zoneKnM2)).Dead;
        }

        public static double[] SnowCells(DynamicInputs inputs, WeatherReport w, LoadField f)
        {
            var cells = new double[f.Nx * f.Ny];
            var v = w.snow.roofKnM2 * f.CellW * f.CellH;
            for (var i = 0; i < cells.Length; i++) cells[i] = v;
            return cells;
        }

        /// <summary>The load case's per-cell load (kN).</summary>
        public static double[] CaseCells(DynamicInputs inputs, DynamicRun run, WeatherReport w, string key)
        {
            var f = run.Field;
            var crowd = CrowdCells(run);
            var snow = SnowCells(inputs, w, f);
            switch (key)
            {
                case "busy": return Sum(DeadCells(inputs, run, "dry"), crowd);
                case "rain": return DeadCells(inputs, run, "rain");
                case "snow": return Sum(DeadCells(inputs, run, "field"), snow);
                case "event":
                {
                    var crowdLeading = Sum(crowd, Scaled(snow, PsiSnow));
                    var snowLeading = Sum(snow, Scaled(crowd, PsiCrowd));
                    var a = crowdLeading.Sum();
                    var b = snowLeading.Sum();
                    return Sum(DeadCells(inputs, run, "field"), a >= b ? crowdLeading : snowLeading);
                }
                default:
                {
                    var site = WindModel.ResolveSite(WindOf(inputs));
                    return Sum(DeadCells(inputs, run, "dry"), WindCells(inputs, site, w.wind.worstTowardAngleDeg, f));
                }
            }
        }

        static void Centroid(double[] cells, LoadField f, StructureInputs st, DynamicRun run, out double offX, out double offY)
        {
            double sum = 0, mx = 0, my = 0;
            for (var iy = 0; iy < f.Ny; iy++)
                for (var ix = 0; ix < f.Nx; ix++)
                {
                    var v = Math.Max(0, cells[f.Index(ix, iy)]);
                    sum += v; mx += v * (ix + 0.5) * f.CellW; my += v * (iy + 0.5) * f.CellH;
                }
            offX = sum > 0 ? (mx / sum - run.CentreX) / st.RoofLength : 0;
            offY = sum > 0 ? (my / sum - run.CentreY) / st.RoofWidth : 0;
        }

        static WeatherReport AnalyseWeather(DynamicInputs inputs, DynamicRun run, CrowdReport crowd)
        {
            var st = inputs.Structure;
            var f = run.Field;
            var w = new WeatherReport();
            var bays = run.Static.bays;
            var nb = bays.Count;
            var capacity = run.Static.summary.capacityKnM2;

            // ---- snow
            var zoneGiven = !string.IsNullOrEmpty(inputs.SnowZone) && SnowGroundLoad(inputs.SnowZone, 0).HasValue;
            var zone = zoneGiven ? inputs.SnowZone.Trim().ToLowerInvariant() : AssumedSnowZone;
            var altGiven = inputs.AltitudeM.HasValue;
            var alt = altGiven ? inputs.AltitudeM.Value : AssumedSnowAltitudeM;
            var sk = SnowGroundLoad(zone, alt).Value;
            w.snow = new SnowInfo
            {
                zone = zone, zoneAssumed = !zoneGiven, altitudeM = (float)alt, altitudeAssumed = !altGiven, skKnM2 = (float)sk,
                shapeCoefficient = (float)SnowShapeCoefficient, roofKnM2 = (float)(SnowShapeCoefficient * sk),
                source = zoneGiven ? (string.IsNullOrEmpty(inputs.SnowZoneSource) ? "set by the designer" : inputs.SnowZoneSource) : "assumed: no snow zone given",
            };

            // ---- rain: the cloudburst, stepped through every zone's own column, from field capacity
            var zones = inputs.Wind != null ? inputs.Wind.Zones : new List<ZoneInput>();
            var columns = new Dictionary<string, SoilColumn>();
            var specs = new Dictionary<string, ColumnSpec>();
            var areas = new Dictionary<string, double>();
            double dryTot = 0, fcTot = 0, satTot = 0;
            foreach (var it in st.Items.Where(i => i.Kind == LoadKind.Zone))
            {
                var z = zones.FirstOrDefault(q => q.Id == it.Id);
                var spec = PercolationModel.SpecFor(z != null ? z.Assembly : null);
                string src;
                var dryKgM2 = z != null ? WindModel.DryWeightKgM2(z.Assembly, out src) : it.DeadKnM2 * 1000.0 / Gravity;
                var satKnM2 = it.DeadKnM2;
                var dryKn = Math.Min(satKnM2, dryKgM2 * Gravity / 1000.0);
                var fcKn = Math.Min(satKnM2, dryKn + PercolationModel.StorageAt(spec, spec.ThetaFc) * Gravity / 1000.0);
                run.ZoneDryKnM2[it.Id] = dryKn;
                run.ZoneFieldCapacityKnM2[it.Id] = fcKn;
                specs[it.Id] = spec;
                columns[it.Id] = new SoilColumn(spec);
                areas[it.Id] = it.Width * it.Height;
                dryTot += dryKn * it.Width * it.Height; fcTot += fcKn * it.Width * it.Height; satTot += satKnM2 * it.Width * it.Height;
            }

            var steps = (int)(RainTotalMinutes * 60);
            var series = new List<float>();
            var peakAdded = 0.0;
            var peakAtS = 0.0;
            var zonePeak = new Dictionary<string, double>();
            foreach (var id in columns.Keys) zonePeak[id] = 0;
            for (var s = 0; s < steps; s++)
            {
                var rate = s < RainMinutes * 60 ? RainIntensityMmH / 3600.0 : 0.0;
                double addedKn = 0;
                foreach (var kv in columns)
                {
                    kv.Value.Step(1.0, rate);
                    var stored = Math.Max(0, kv.Value.StoredMm);
                    if (stored > zonePeak[kv.Key]) zonePeak[kv.Key] = stored;
                    addedKn += stored * Gravity / 1000.0 * areas[kv.Key];
                }
                if (addedKn > peakAdded) { peakAdded = addedKn; peakAtS = s; }
                if (s % 60 == 0) series.Add((float)addedKn);
            }
            foreach (var it in st.Items.Where(i => i.Kind == LoadKind.Zone))
                run.ZoneRainPeakKnM2[it.Id] = Math.Min(it.DeadKnM2, run.ZoneFieldCapacityKnM2[it.Id] + zonePeak[it.Id] * Gravity / 1000.0);
            w.rain = new RainInfo
            {
                intensityMmH = (float)RainIntensityMmH, minutes = (float)RainMinutes,
                dryKn = (float)dryTot, fieldCapacityKn = (float)fcTot, saturatedKn = (float)satTot,
                peakAddedKn = (float)peakAdded, peakAtMin = (float)(peakAtS / 60.0), peakKn = (float)(fcTot + peakAdded),
                seriesStepMin = 1f, addedKnSeries = series.ToArray(),
            };

            // ---- wind: the direction that lifts a bay most
            var site = WindModel.ResolveSite(WindOf(inputs));
            var worstSuction = -1.0;
            double worstDir = 0;
            foreach (var dir in WindModel.DirectionsDeg)
            {
                var sums = StructureModel.BaySums(WindCells(inputs, site, dir, f), f, run.Xs, run.Ys);
                var m = 0.0;
                for (var b = 0; b < nb; b++) m = Math.Max(m, -sums[b] / bays[b].areaM2);
                if (m > worstSuction * (1 + 1e-6) + 1e-12) { worstSuction = m; worstDir = dir; }   // mirror-image directions tie: the first listed wins, whatever the rounding
            }
            w.wind = new WindInfo
            {
                zone = site.WindZone, peakPressurePa = (float)site.QRoof, roofHeightM = (float)site.RoofElevation,
                worstTowardAngleDeg = (float)worstDir, worstDirection = WindModel.DirectionLabel(worstDir),
                maxSuctionKnM2 = (float)(-WindModel.Cpe(RoofZone.F) * site.QRoof / 1000.0),
            };

            // ---- the load cases
            var defs = new[]
            {
                new[] { "busy", "Busiest hour, dry", "Permanent load with the build-ups dry, and the crowd of the busiest moment of the day" },
                new[] { "rain", "Cloudburst", "The build-ups at their wettest during a " + F0(RainIntensityMmH) + " mm/h, " + F0(RainMinutes) + " min storm on a roof at field capacity; nobody on the roof" },
                new[] { "snow", "Snow", "Permanent load (build-ups at field capacity) with " + F2(w.snow.roofKnM2) + " kN/m2 of snow (zone " + zone + ", mu = " + F1(SnowShapeCoefficient) + "), the roof closed" },
                new[] { "event", "Event in winter", "Snow and the crowd of the busiest moment together: the worse of crowd + " + F1(PsiSnow) + " snow and snow + " + F1(PsiCrowd) + " crowd (EN 1990 combination values)" },
                new[] { "gale", "Gale", "Build-ups dry, the design wind (" + WindModel.DirectionLabel(worstDir) + ") lifting the roof: net vertical load" },
            };
            var totals = new List<double[]>();
            foreach (var d in defs)
            {
                var cells = CaseCells(inputs, run, w, d[0]);
                var sums = StructureModel.BaySums(cells, f, run.Xs, run.Ys);
                var lc = new LoadCase { key = d[0], name = d[1], description = d[2], bayKnM2 = new float[nb], bayUtilisation = new float[nb], totalKn = (float)cells.Sum() };
                for (var b = 0; b < nb; b++)
                {
                    lc.bayKnM2[b] = (float)(sums[b] / bays[b].areaM2);
                    lc.bayUtilisation[b] = (float)(Math.Max(0, sums[b]) / bays[b].areaM2 / capacity);
                    if (lc.bayUtilisation[b] > lc.peakUtilisation * (1 + 1e-5) + 1e-9) { lc.peakUtilisation = lc.bayUtilisation[b]; lc.worstBay = bays[b].label; }
                    if (lc.bayUtilisation[b] > OverCapacityFrom) lc.baysOver++;
                }
                double ox, oy;
                Centroid(cells, f, st, run, out ox, out oy);
                lc.offsetXPercent = (float)(ox * 100); lc.offsetYPercent = (float)(oy * 100);
                w.cases.Add(lc);
                totals.Add(cells);
            }
            var galeCase = w.cases.First(c => c.key == "gale");
            w.wind.baysNetUplift = galeCase.bayKnM2.Count(v => v < 0);
            var galeSums = StructureModel.BaySums(WindCells(inputs, site, worstDir, f), f, run.Xs, run.Ys);
            w.wind.meanSuctionKnM2 = (float)(-galeSums.Sum() / (st.RoofLength * st.RoofWidth));

            // typical permanent load per bay, kept for the frequency estimate
            var fcSums = StructureModel.BaySums(DeadCells(inputs, run, "field"), f, run.Xs, run.Ys);
            run.DeadFieldKnM2Bay = new double[nb];
            for (var b = 0; b < nb; b++) run.DeadFieldKnM2Bay[b] = fcSums[b] / bays[b].areaM2;

            // ---- the governing case of every bay (the gale only lifts, so it is not a candidate)
            for (var b = 0; b < nb; b++)
            {
                LoadCase best = null;
                foreach (var c in w.cases)
                    if (c.key != "gale" && (best == null || c.bayUtilisation[b] > best.bayUtilisation[b])) best = c;
                w.governing.Add(new GoverningBay
                {
                    label = bays[b].label, governingCase = best.name, utilisation = best.bayUtilisation[b],
                    designUtilisation = bays[b].utilisation, deadKnM2 = (float)run.DeadFieldKnM2Bay[b],
                });
            }
            var worst = w.cases.Where(c => c.key != "gale").OrderByDescending(c => c.peakUtilisation).First();
            w.worstCase = worst.name; w.worstBay = worst.worstBay; w.peakUtilisation = worst.peakUtilisation;
            return w;
        }

        // ---------------------------------------------------------------- scenario 3: resonance

        static double OverlapArea(LoadItem i, BayResult bay)
        {
            var w = Math.Max(0, Math.Min(i.X + i.Width, bay.x1) - Math.Max(i.X, bay.x0));
            var h = Math.Max(0, Math.Min(i.Y + i.Height, bay.y1) - Math.Max(i.Y, bay.y0));
            return w * h;
        }

        /// <summary>The largest acceleration (g) over the crowd's rhythm range, and the rhythm and harmonic that produce it.</summary>
        public static double WorstAcceleration(ActivityDef a, double forcePa, double massKgM2, double fnHz, out double fpAt, out int harmonic)
        {
            var best = 0.0;
            fpAt = a.fpLowHz;
            harmonic = 1;
            for (var fp = (double)a.fpLowHz; fp <= a.fpHighHz + 1e-6; fp += FpStepHz)
            {
                var g = AccelerationG(a.alpha, forcePa, massKgM2, fnHz, fp, Damping);
                if (g > best * (1 + 1e-7) + 1e-15) { best = g; fpAt = fp; }
            }
            // the harmonic that dominates at the worst rhythm
            var top = 0.0;
            for (var k = 1; k <= a.alpha.Length; k++)
            {
                var r = k * fpAt / fnHz;
                var v = a.alpha[k - 1] * r * r * Magnification(r, Damping);
                if (v > top) { top = v; harmonic = k; }
            }
            return best;
        }

        static ResonanceReport AnalyseResonance(DynamicInputs inputs, DynamicRun run)
        {
            var st = inputs.Structure;
            var bays = run.Static.bays;
            var given = inputs.NaturalFrequencyHz.HasValue && inputs.NaturalFrequencyHz.Value > 0;
            var rr = new ResonanceReport
            {
                estimated = !given, dampingRatio = (float)Damping, youngGPa = (float)YoungGPa,
                bandLow = given ? 1f : (float)BandLow, bandHigh = given ? 1f : (float)BandHigh,
            };
            var activities = ActivitiesFor(inputs);
            rr.activities.AddRange(activities);

            var worstRatio = -1.0;
            BayResonance worstBay = null;
            ActivityResponse worstResp = null;
            double worstForce = 0, worstMass = 0, worstFn = 0;
            ActivityDef worstDef = null;

            for (var b = 0; b < bays.Count; b++)
            {
                var bay = bays[b];
                var bx = bay.x1 - bay.x0;
                var by = bay.y1 - bay.y0;
                var span = Math.Max(bx, by);
                var depth = Math.Min(MaxDepthM, Math.Max(MinDepthM, span / SpanToDepth));
                var mass = ConcreteKgM3 * depth + run.DeadFieldKnM2Bay[b] * 1000.0 / Gravity;
                var fn = given ? inputs.NaturalFrequencyHz.Value : FrequencyHz(span, depth, mass);
                var br = new BayResonance
                {
                    label = bay.label, spanM = (float)span, areaM2 = bay.areaM2, depthM = (float)depth, massKgM2 = (float)mass,
                    frequencyHz = (float)fn, frequencyLowHz = (float)(given ? fn : fn * BandLow), frequencyHighHz = (float)(given ? fn : fn * BandHigh),
                };

                double hostArea = 0;
                // a jumping crowd stands where people play or gather: courts and play areas, not planted beds (they only carry walkers)
                foreach (var it in st.Items)
                    if ((it.Kind == LoadKind.Court || it.Kind == LoadKind.Activity) && it.Persons > 0) hostArea += OverlapArea(it, bay);

                foreach (var a in activities)
                {
                    double n;
                    if (a.key == "walking") n = run.PeakBayPersons[b];
                    else if (a.key == "play") n = run.PeakBayPlayPersons[b];
                    else n = RhythmicDensity * hostArea;

                    var nEff = n < 1 ? n : a.sync * n + (1 - a.sync) * Math.Sqrt(n);
                    var forcePa = nEff * PersonN / bay.areaM2;
                    var crowdMass = n * 90.0 / bay.areaM2;
                    var m = mass + crowdMass;
                    var fLoaded = given ? fn : fn * Math.Sqrt(mass / m);
                    double fpAt = a.fpLowHz;
                    var k = 1;
                    double nominal = 0;
                    if (n > 0) nominal = WorstAcceleration(a, forcePa, m, fLoaded, out fpAt, out k);

                    var bandMax = nominal;
                    if (!given && n > 0)
                    {
                        for (var s = BandLow; s <= BandHigh + 1e-9; s += 0.025)
                        {
                            double fp2; int k2;
                            var g = WorstAcceleration(a, forcePa, m, fLoaded * s, out fp2, out k2);
                            if (g > bandMax * (1 + 1e-7) + 1e-15) { bandMax = g; fpAt = fp2; k = k2; }
                        }
                    }

                    var ratio = bandMax / a.limitG;
                    var resp = new ActivityResponse
                    {
                        activity = a.name, participants = (float)n, forcePa = (float)forcePa,
                        accelerationG = (float)nominal, accelerationBandG = (float)bandMax, limitG = a.limitG,
                        ratio = (float)ratio, worstFpHz = (float)fpAt, worstHarmonic = k,
                        status = n <= 0 ? "none" : (ratio > 1.0 ? "exceeds" : (ratio > 0.5 ? "marginal" : "ok")),
                    };
                    br.activities.Add(resp);
                    if (resp.ratio > br.worstRatio) { br.worstRatio = resp.ratio; br.worstActivity = a.name; }
                    if (ratio > worstRatio * 1.001 + 1e-9)
                    {
                        worstRatio = ratio; worstBay = br; worstResp = resp;
                        worstForce = forcePa; worstMass = m; worstFn = fLoaded; worstDef = a;
                    }
                }
                br.status = br.worstRatio > 1.0 ? "exceeds" : (br.worstRatio > 0.5 ? "marginal" : "ok");
                rr.bays.Add(br);
            }

            rr.lowestFrequencyHz = rr.bays.Min(x => x.frequencyHz);
            rr.highestFrequencyHz = rr.bays.Max(x => x.frequencyHz);
            rr.baysExceeding = rr.bays.Count(x => x.status == "exceeds");
            rr.baysMarginal = rr.bays.Count(x => x.status == "marginal");

            if (worstBay != null && worstResp != null && worstResp.participants > 0)
            {
                rr.worstBay = worstBay.label; rr.worstActivity = worstResp.activity;
                rr.worstAccelerationG = worstResp.accelerationBandG; rr.worstLimitG = worstResp.limitG; rr.worstRatio = worstResp.ratio;
                rr.worstFrequencyHz = (float)worstFn; rr.worstFpHz = worstResp.worstFpHz; rr.worstHarmonic = worstResp.worstHarmonic;

                // the spectrum: the worst bay's loading against every deck frequency, for each activity
                rr.spectrumFromHz = 2f; rr.spectrumStepHz = 0.1f;
                foreach (var a in activities)
                {
                    var resp = worstBay.activities.First(x => x.activity == a.name);
                    var curve = new SpectrumCurve { activity = a.name, accelerationG = new float[101] };
                    for (var i = 0; i <= 100; i++)
                    {
                        double fp; int k;
                        curve.accelerationG[i] = resp.participants <= 0 ? 0f : (float)WorstAcceleration(a, resp.forcePa, worstMass, 2.0 + 0.1 * i, out fp, out k);
                    }
                    rr.spectrum.Add(curve);
                }

                // the sweep: the crowd's rhythm through its range on the estimated deck
                rr.sweepActivity = worstDef.name; rr.sweepFromHz = worstDef.fpLowHz; rr.sweepStepHz = (float)FpStepHz;
                var sweep = new List<float>();
                for (var fp = (double)worstDef.fpLowHz; fp <= worstDef.fpHighHz + 1e-6; fp += FpStepHz)
                    sweep.Add((float)AccelerationG(worstDef.alpha, worstForce, worstMass, worstFn, fp, Damping));
                rr.sweepG = sweep.ToArray();
            }
            else
            {
                rr.worstBay = worstBay != null ? worstBay.label : "";
                rr.worstActivity = "";
            }
            return rr;
        }

        // ---------------------------------------------------------------- summary, advice, assumptions

        static string F0(double v) { return v.ToString("0", CultureInfo.InvariantCulture); }
        static string F1(double v) { return v.ToString("0.#", CultureInfo.InvariantCulture); }
        static string F2(double v) { return v.ToString("0.##", CultureInfo.InvariantCulture); }
        public static string Clock(double hour)
        {
            var h = (int)Math.Floor(hour);
            var m = (int)Math.Floor((hour - h) * 60.0 + 1e-6);
            return h.ToString("00", CultureInfo.InvariantCulture) + ":" + m.ToString("00", CultureInfo.InvariantCulture);
        }

        static void Summarise(DynamicInputs inputs, DynamicReport r)
        {
            r.summary = new DynamicSummary
            {
                peakPersons = r.crowd.peakPersons, peakAtHour = r.crowd.peakAtHour, maxShiftPercent = r.crowd.maxShiftPercent,
                crowdSharePercent = r.weather.cases.Count > 0 && r.weather.cases[0].totalKn > 0 ? 100f * r.crowd.peakCrowdKn / r.weather.cases[0].totalKn : 0f,
                worstCase = r.weather.worstCase, worstCaseBay = r.weather.worstBay, worstCaseUtilisation = r.weather.peakUtilisation,
                skKnM2 = r.weather.snow.skKnM2,
                lowestFrequencyHz = r.resonance.lowestFrequencyHz,
                worstResonanceBay = r.resonance.worstBay, worstResonanceActivity = r.resonance.worstActivity,
                worstAccelerationG = r.resonance.worstAccelerationG, worstResonanceRatio = r.resonance.worstRatio,
                baysResonanceExceeding = r.resonance.baysExceeding,
                baysOverCapacity = r.weather.governing.Count(g => g.utilisation > OverCapacityFrom),
                frequencyEstimated = r.resonance.estimated, snowAssumed = r.weather.snow.zoneAssumed || r.weather.snow.altitudeAssumed,
                capacityAssumed = !inputs.Structure.CapacityKnM2.HasValue,
                capacityKnM2 = (float)(inputs.Structure.CapacityKnM2 ?? StructureModel.DefaultCapacityKnM2),
                preliminary = AnalysisAssumptions.IsPreliminary(r.assumptionUses),
                preliminaryNote = AnalysisAssumptions.PreliminaryNote(r.assumptionUses),
                acceptedNote = AnalysisAssumptions.AcceptedNote(r.assumptionUses),
            };
        }

        /// <summary>The inputs behind the dynamic numbers, each with whether the designer entered it, accepted the built-in value, or did neither.</summary>
        static List<AssumptionUse> DescribeInputs(DynamicInputs inputs, DynamicReport r, DynamicRun run)
        {
            var acc = inputs.Structure.AcceptedAssumptions;
            var uses = new List<AssumptionUse>();
            uses.AddRange(run.Static.assumptionUses);   // the deck capacity
            var f = r.resonance;
            uses.Add(AnalysisAssumptions.Use(AnalysisAssumptions.NaturalFrequency, !f.estimated,
                f.estimated ? "estimated, " + F1(f.lowestFrequencyHz) + " to " + F1(f.highestFrequencyHz) + " Hz" : F1(inputs.NaturalFrequencyHz.Value) + " Hz", acc));
            var snow = r.weather.snow;
            uses.Add(AnalysisAssumptions.Use(AnalysisAssumptions.SnowZone, !snow.zoneAssumed, "zone " + snow.zone, acc));
            uses.Add(AnalysisAssumptions.Use(AnalysisAssumptions.Altitude, !snow.altitudeAssumed, F0(snow.altitudeM) + " m", acc));
            var sched = DaySchedule.Get(inputs.Schedule);
            uses.Add(AnalysisAssumptions.Use(AnalysisAssumptions.DaySchedule, !string.IsNullOrWhiteSpace(inputs.Schedule), sched.Name.ToLowerInvariant(), acc));
            var acts = f.activities;
            var walking = acts.First(a => a.key == "walking").limitG;
            var rhythmic = acts.First(a => a.key == "play").limitG;
            uses.Add(AnalysisAssumptions.Use(AnalysisAssumptions.ComfortWalking, inputs.WalkingLimitG.HasValue && inputs.WalkingLimitG.Value > 0, F2(walking) + " g", acc));
            uses.Add(AnalysisAssumptions.Use(AnalysisAssumptions.ComfortRhythmic, inputs.RhythmicLimitG.HasValue && inputs.RhythmicLimitG.Value > 0, F2(rhythmic) + " g", acc));
            return uses;
        }

        static void Recommend(DynamicInputs inputs, DynamicReport r, DynamicRun run)
        {
            var c = r.crowd;
            var w = r.weather;
            var res = r.resonance;
            var recs = r.recommendations;

            // crowds
            var busy = c.bays.First(b => b.label == c.busiestBay);
            recs.Add(new DynamicRecommendation
            {
                scenario = "crowds", kind = "peak", target = busy.label,
                text = "The roof is busiest at " + Clock(c.peakAtHour) + " with about " + F0(c.peakPersons) + " people (" + F0(c.peakCrowdKn) + " kN); " + busy.label + " is the most crowded bay, " +
                       F2(busy.peakDensity) + " people per m2 at " + Clock(busy.peakAtHour) + ", " + F0(busy.peakShareOfDesignPercent) + "% of what the deck is designed for there.",
            });
            recs.Add(new DynamicRecommendation
            {
                scenario = "crowds", kind = "balance", target = "Load balance",
                text = "The crowd is " + F1(100.0 * c.peakCrowdKn / Math.Max(1e-9, w.cases[0].totalKn)) + "% of the load at its busiest: it moves the load's centre by at most " + F2(c.maxShiftPercent) + "% (toward the " + c.maxShiftSide + ", at " + Clock(c.maxShiftAtHour) +
                       "), on top of the " + F1(Math.Max(Math.Abs(c.baselineOffsetXPercent), Math.Abs(c.baselineOffsetYPercent))) + "% the permanent load already has. The weight of the build-ups decides the balance, not the people.",
            });

            // weather
            var wg = w.governing.OrderByDescending(g => g.utilisation).First();
            var over = w.governing.Where(g => g.utilisation > OverCapacityFrom).ToList();
            if (over.Count > 0)
                recs.Add(new DynamicRecommendation
                {
                    scenario = "weather", kind = "over-capacity", target = wg.label,
                    text = over.Count + " bay" + (over.Count == 1 ? "" : "s") + " exceed" + (over.Count == 1 ? "s" : "") + " the deck capacity in some weather: " + wg.label + " reaches " + F0(wg.utilisation * 100) + "% in \"" + wg.governingCase +
                           "\" (the static design case gives " + F0(wg.designUtilisation * 100) + "%). Lighten what stands there or have the deck checked.",
                });
            else
                recs.Add(new DynamicRecommendation
                {
                    scenario = "weather", kind = "fine", target = "All bays",
                    text = "No bay exceeds the deck capacity in any weather case; the governing one is \"" + w.worstCase + "\" at " + F0(w.peakUtilisation * 100) + "% in " + w.worstBay + ".",
                });
            if (w.rain.peakAddedKn > 0)
                recs.Add(new DynamicRecommendation
                {
                    scenario = "weather", kind = "rain", target = "Green roofs",
                    text = "A " + F0(RainIntensityMmH) + " mm/h cloudburst adds up to " + F0(w.rain.peakAddedKn) + " kN of water to the build-ups by minute " + F0(w.rain.peakAtMin) + " (" + F0(w.rain.peakKn) + " kN in all; saturated: " + F0(w.rain.saturatedKn) + " kN).",
                });
            if (Open(r, AnalysisAssumptions.SnowZone) || Open(r, AnalysisAssumptions.Altitude))
                recs.Add(new DynamicRecommendation
                {
                    scenario = "weather", kind = "snow-input", target = "Snow zone",
                    text = "The snow load uses " + (w.snow.zoneAssumed ? "an ASSUMED snow zone (" + w.snow.zone + ")" : "zone " + w.snow.zone) + (w.snow.altitudeAssumed ? " and an ASSUMED altitude of " + F0(w.snow.altitudeM) + " m" : "") +
                           ": enter the site's snow zone and altitude in the Site tab. sk = " + F2(w.snow.skKnM2) + " kN/m2.",
                });

            // resonance
            if (res.worstRatio > 1.0)
            {
                var a = res.activities.First(x => x.name == res.worstActivity);
                var kMax = 1;
                for (var k = 1; k <= a.alpha.Length; k++) if (a.alpha[k - 1] >= 0.25f) kMax = k;   // the harmonics that carry weight
                var clearHz = a.fpHighHz * kMax;
                recs.Add(new DynamicRecommendation
                {
                    scenario = "resonance", kind = "resonance", target = res.worstBay,
                    text = res.worstBay + " reaches " + F2(res.worstAccelerationG) + " g under " + res.worstActivity.ToLowerInvariant() + " (comfort limit " + F2(res.worstLimitG) + " g): harmonic " + res.worstHarmonic + " of a " + F1(res.worstFpHz) +
                           " Hz rhythm meets the deck at " + F1(res.worstFrequencyHz) + " Hz. Only a deck above " + F1(clearHz) + " Hz (harmonic " + kMax + " of the fastest rhythm; stiffness about x" + F1(Math.Pow(clearHz / Math.Max(0.1, res.worstFrequencyHz), 2)) +
                           ") is clear of the crowd's harmonics; otherwise more damping, or keeping rhythmic events off this bay.",
                });
            }
            else
                recs.Add(new DynamicRecommendation
                {
                    scenario = "resonance", kind = "fine", target = "All bays",
                    text = "No bay exceeds its comfort limit under the crowd activities checked; the closest is " + res.worstBay + " under " + (res.worstActivity ?? "").ToLowerInvariant() + " at " + F0(res.worstRatio * 100) + "% of the limit.",
                });
            if (res.estimated && Open(r, AnalysisAssumptions.NaturalFrequency))
                recs.Add(new DynamicRecommendation
                {
                    scenario = "resonance", kind = "frequency-input", target = "Natural frequency",
                    text = "The deck's frequency is an ESTIMATE from the spans (" + F1(res.lowestFrequencyHz) + " to " + F1(res.highestFrequencyHz) + " Hz, good to about 25%). Enter the structural engineer's first natural frequency in the Site tab before relying on this.",
                });
            if (Open(r, AnalysisAssumptions.DeckCapacity))
                recs.Add(new DynamicRecommendation
                {
                    scenario = "general", kind = "capacity-input", target = "Deck capacity",
                    text = "The deck capacity (" + F1(run.Static.summary.capacityKnM2) + " kN/m2) is a placeholder: enter the structural engineer's figure.",
                });
        }

        /// <summary>True while an input is still a built-in value nobody confirmed (neither entered nor accepted).</summary>
        static bool Open(DynamicReport r, string key)
        {
            return r.assumptionUses.Any(u => u.key == key && u.state == AnalysisAssumptions.Unconfirmed);
        }

        static List<string> Assumptions(DynamicInputs inputs, DynamicReport r)
        {
            var sched = DaySchedule.Get(inputs.Schedule);
            return new List<string>
            {
                "Screening model, not a structural verification or a vibration design.",
                "Crowds: an agent simulation of the day \"" + sched.Name + "\" (06:00 to 23:00, fixed random seed). People arrive at the entries, walk in straight lines at " + F1(CrowdSim.WalkMs) + " m/s (circulation paths are not followed), settle on their court, play area or garden and wander about it, and leave. " +
                    "How full each piece is each hour (a share of its players, seats or garden visitors) is the author's schedule. A person weighs " + F0(90) + " kg.",
                "Weather: each case is a characteristic combination, without partial safety factors. Permanent load: build-ups dry, at field capacity (where the storm starts) or wet, as the case says; the static analysis's saturated weight is the envelope of all of them.",
                "Rain: the cloudburst of the rain analysis (" + F0(RainIntensityMmH) + " mm/h for " + F0(RainMinutes) + " min) through each zone's own column, from field capacity. Snow: DIN EN 1991-1-3/NA ground load for zone " + r.weather.snow.zone + " at " + F0(r.weather.snow.altitudeM) +
                    " m, times " + F1(SnowShapeCoefficient) + " (flat roof); no drifting or sliding. Wind: the wind analysis's roof zones and peak pressure, the direction that lifts a bay most. Event in winter: EN 1990 combination values " + F1(PsiCrowd) + " (crowd) and " + F1(PsiSnow) + " (snow).",
                "Resonance: each bay is one simply supported strip along its long span, depth span/" + F0(SpanToDepth) + " (" + F2(MinDepthM) + " to " + F2(MaxDepthM) + " m), E = " + F0(YoungGPa) + " GPa, damping " + F2(Damping) + "; " +
                    (r.resonance.estimated ? "the natural frequency is ESTIMATED from that (good to about 25%), and the response is the worst over that band" : "the natural frequency is the engineer's figure") + ". The crowd drives the harmonics of its rhythm with the dynamic load factors of a half-sine pulse train (jumping: contact ratio 1/3 gives 1.8, 1.29, 0.67; Bachmann and Ammann) or walking 0.4, 0.1, 0.1; " +
                    "participants add as sync x N + (1 - sync) x sqrt(N) with sync 0 (walking), 0.2 (court play), 0.6 (event). A jumping event is 0.25 people per m2 on the courts and play areas only (planted gardens carry walkers, not a jumping crowd). Steady-state response; acceleration limits " + F2(r.resonance.activities.First(a => a.key == "walking").limitG) + " g (walking) and " + F2(r.resonance.activities.First(a => a.key == "play").limitG) + " g (rhythmic activities): see the inputs below for whose they are.",
                "Capacity: " + F1(r.summary.capacityAssumed ? StructureModel.DefaultCapacityKnM2 : inputs.Structure.CapacityKnM2.Value) + " kN/m2" + (r.summary.capacityAssumed ? " (a PLACEHOLDER: enter the engineer's figure)." : "."),
            };
        }

        /// <summary>One line naming what was analysed, shared by the add-in's dialogs and the video's title card.</summary>
        public static string CaseStudy(DynamicInputs inputs, DynamicReport r)
        {
            return DaySchedule.Get(inputs.Schedule).Name + " on a " + F1(inputs.Structure.RoofLength) + " x " + F1(inputs.Structure.RoofWidth) + " m roof, " + r.weather.cases.Count + " load cases, " + r.resonance.bays.Count + " bays";
        }
    }
}
