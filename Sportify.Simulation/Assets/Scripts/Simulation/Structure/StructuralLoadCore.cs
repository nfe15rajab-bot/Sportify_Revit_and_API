#nullable disable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Sportify.Simulation.Water;
using Sportify.Simulation.Wind;

namespace Sportify.Simulation.Structure
{
    // ---------------------------------------------------------------------------------------
    //  Static loads on the roof structure: where the weight and the people are, which bays of the
    //  structural grid carry most, and whether the load sits to one side.
    //
    //  Like the wind and rain models this uses nothing from UnityEngine, so the numbers come out in
    //  the Revit add-in with no Unity installed; Unity only films them.
    //
    //  It is a SCREENING model, not a structural verification. It works on the loads that sit ON the
    //  structure (the structure's own weight is outside it):
    //
    //    permanent  G   green-roof build-ups at their water-SATURATED weight (a green roof's permanent
    //                   load is its saturated weight), trees, sports and play surfaces, roof finishes
    //    imposed    Q   the characteristic value of the use category of each area (sports areas are
    //                   category C4, 5.0 kN/m2 in DIN EN 1991-1-1/NA Table 6.1DE), the maximum where
    //                   areas overlap
    //    people         the EXPECTED number of people (players, spectators, visitors, arrivals at the
    //                   entries): where activity concentrates. It is not what Q is made of; Q is the
    //                   code's design value, people are what will actually be there.
    //
    //  The roof (its bounding rectangle) is cut into 0.5 m cells. Every piece of the layout puts its
    //  load on the cells it overlaps in proportion to the overlap, so totals are conserved exactly.
    //  The cells are summed into the bays of the structural grid (a cell across a grid line is split
    //  by area), each bay's characteristic G + Q is compared with the deck capacity, and the centre of
    //  the total load is compared with the centre of the structure (the columns' centroid, else the
    //  plan centre). Where it is off to one side, the single move or lightening that brings it back
    //  is found by re-running the model, so the advice is verified, not estimated.
    //
    //  Every constant that is a judgement call is named below and repeated in the report's
    //  assumptions. The deck's capacity is NOT known from the layout: it is an input, with a default
    //  that is only a placeholder for the structural engineer's figure.
    //
    //  Units: metres, kN, kN/m2. Plan coordinates as everywhere: x right, y DOWN from the top edge.
    // ---------------------------------------------------------------------------------------

    public enum LoadKind { Court, Activity, Zone, Tree }

    public class GridLineInput
    {
        public string Name;
        public double Position;   // x for a vertical line, y for a horizontal one
    }

    public class LoadItem
    {
        public string Id, Label;
        public string Name = "";             // what it is, for the videos: "Badminton (standard)", "Yoga", "Green roof: ZinCo Roof Garden"
        public LoadKind Kind;
        public double X, Y, Width, Height;   // footprint (a tree: its crown's box)
        public double DeadKnM2;              // permanent load on the footprint
        public double LiveKnM2;              // characteristic imposed load of its use
        public double PointKn;               // a concentrated permanent load (a tree's weight)
        public double Persons;               // expected people on it
        public double Players, Seats;        // a court's players and seated spectators (Persons is their sum)
        public string Basis = "";            // where the numbers come from, for the report

        public LoadItem Copy() { return (LoadItem)MemberwiseClone(); }
    }

    public class PathInput
    {
        public List<double[]> Points = new List<double[]>();
        public double WidthM;
    }

    public class StructureInputs
    {
        public double RoofLength, RoofWidth;
        public List<GridLineInput> VerticalLines = new List<GridLineInput>();     // lines of constant x
        public List<GridLineInput> HorizontalLines = new List<GridLineInput>();   // lines of constant y
        public List<double[]> Columns = new List<double[]>();                     // {x, y}
        public string GridSource = "";                                            // "revit" | "manual" | ""
        public double? CapacityKnM2;
        public List<string> AcceptedAssumptions = new List<string>();             // keys (AnalysisAssumptions) whose built-in value the designer accepted
        public List<LoadItem> Items = new List<LoadItem>();
        public List<PathInput> Paths = new List<PathInput>();
        public List<double[]> Entries = new List<double[]>();
        public List<string> Notes = new List<string>();                           // what the reader of the layout had to skip or assume, for the report

        /// <summary>The same inputs with every item copied, so pieces can be moved or lightened without touching the original.</summary>
        public StructureInputs CopyWithItems()
        {
            var copy = (StructureInputs)MemberwiseClone();
            copy.Items = new List<LoadItem>();
            foreach (var i in Items) copy.Items.Add(i.Copy());
            return copy;
        }

        /// <summary>The same inputs with one item replaced (the lists are shared, apart from Items).</summary>
        public StructureInputs WithItem(int index, LoadItem replacement)
        {
            var copy = (StructureInputs)MemberwiseClone();
            copy.Items = new List<LoadItem>(Items);
            copy.Items[index] = replacement;
            return copy;
        }
    }

    // ------------------------------------------------------------------------ results (serialised by Unity's JsonUtility)

    [Serializable]
    public class BayResult
    {
        public string label;
        public string gridNames;          // "B-C / 2-3" when the grid lines are named
        public float x0, x1, y0, y1;
        public float areaM2;
        public float deadKn, liveKn, totalKn;
        public float totalKnM2;
        public float utilisation;         // characteristic G + Q against the deck capacity
        public string status;             // ok | marginal | over
        public float persons;
        public float personsPerM2;
        public float crowdKn;             // expected people x their weight
        public float shareOfLoadPercent;
        public string topContributor;
    }

    [Serializable]
    public class ColumnResult
    {
        public string label;
        public float x, y;
        public float tributaryM2;
        public float loadKn;
        public float ratioToMean;
        public string status;             // ok | high
    }

    [Serializable]
    public class ItemLoadResult
    {
        public string id, label, kind, basis;
        public float areaM2;
        public float deadKnM2, liveKnM2;
        public float deadKn, liveKn, persons;
    }

    [Serializable]
    public class BalanceResult
    {
        public float centreX, centreY;            // the structure's centre (columns' centroid, else the plan centre)
        public string centreBasis;
        public float deadCentroidX, deadCentroidY;
        public float deadEccentricityX, deadEccentricityY;     // permanent load only, as a fraction of the roof's length / width; + = toward +x / +y (right / bottom)
        public float totalCentroidX, totalCentroidY;
        public float totalEccentricityX, totalEccentricityY;   // permanent + imposed: what the status is judged on
        public float leftSharePercent, rightSharePercent, topSharePercent, bottomSharePercent;   // of the total (G + Q) load
        public string status;                     // balanced | marginal | unbalanced
        public string heavySide;                  // "right", "left", "top", "bottom" or ""
    }

    [Serializable]
    public class StructureRecommendation
    {
        public string kind;                       // move | lighten | over-capacity | vulnerable | concentration | grid | fine
        public string target;
        public string text;
        public string itemId;                     // move / lighten: the piece
        public string axis;                       // move: "x" or "y"
        public float moveM;                       // move: signed distance along the axis (+ = toward +x / +y)
        public float newDeadKnM2;                 // lighten: the permanent load the piece should come down to
        public float eccentricityAfter;           // move / lighten: what the balance becomes (fraction of the dimension)
        public float peakUtilisationAfter;        // move / lighten: the busiest bay afterwards
    }

    [Serializable]
    public class StructureSummary
    {
        public float roofAreaM2;
        public float deadKn, liveKn, totalKn;
        public float meanKnM2, peakBayKnM2;
        public float capacityKnM2;
        public bool capacityAssumed;
        public bool capacityAccepted;             // the designer accepted the built-in capacity
        public bool preliminary;                  // some input is still a built-in value nobody confirmed
        public string preliminaryNote = "";       // which ones, for the top of a video and a dialog
        public string acceptedNote = "";          // which built-in values the designer accepted
        public string gridSource;
        public bool gridAssumed;
        public int baysChecked, baysOver, baysMarginal;
        public int columnsChecked, columnsHigh;
        public float expectedPersons;
        public float busiestBaysSharePercent;     // of the people, in the busiest fifth of the bays
        public string worstBay;
        public float peakUtilisation;
    }

    [Serializable]
    public class StructureReport
    {
        public bool ran;
        public List<string> assumptions = new List<string>();
        public List<AssumptionUse> assumptionUses = new List<AssumptionUse>();   // the inputs behind the numbers and whether the designer confirmed them
        public StructureSummary summary = new StructureSummary();
        public BalanceResult balance = new BalanceResult();
        public List<BayResult> bays = new List<BayResult>();
        public List<ColumnResult> columns = new List<ColumnResult>();
        public List<ItemLoadResult> items = new List<ItemLoadResult>();
        public List<StructureRecommendation> recommendations = new List<StructureRecommendation>();
        public float[] verticalLinesM = new float[0];       // the bay boundaries actually used, x
        public float[] horizontalLinesM = new float[0];     // and y
    }

    /// <summary>Per-cell loads, in kN and people per cell, top row first: what the report is summed from and what the video colours.</summary>
    public sealed class LoadField
    {
        public int Nx, Ny;
        public double CellW, CellH;
        public double[] Dead, Live, Persons;

        public int Index(int ix, int iy) { return iy * Nx + ix; }
    }

    // ------------------------------------------------------------------------ the model

    public static class StructureModel
    {
        // --- constants that are judgement calls (all repeated in the report) ---
        public const double Gravity = 9.81;
        public const double CellM = 0.5;
        public const double PersonMassKg = 90.0;                 // the Sportify reference value (Live Loads)
        public const double RoofFinishesKnM2 = 0.5;              // waterproofing, insulation, finishes on top of the deck
        public const double SportsSurfaceKnM2 = 0.5;             // floor build-up of a court or play area
        public const double CourtLiveKnM2 = 5.0;                 // DIN EN 1991-1-1/NA Table 6.1DE, category C4
        public const double ActivityLiveKnM2 = 4.0;              // play and assembly areas (C3 range 3-5), assumed
        public const double AccessibleLiveKnM2 = 3.0;            // accessible roof garden, walkways, circulation, assumed
        public const double RoofLiveKnM2 = 0.75;                 // roof not accessible but for maintenance (category H), assumed: check the National Annex
        public const double DefaultCapacityKnM2 = 8.0;           // placeholder for the engineer's figure: 5.0 sports use + 3.0 permanent
        public const double TreeMassAtSixMetresKg = 100.0;       // above-ground mass, scaled with (height / 6 m)^2, as in the wind model
        public const double AssumedBayM = 8.4;                   // a regular grid when the model has none
        public const double EntryRadiusM = 3.0;
        public const double EntryPersons = 10.0;                 // arrivals waiting at each entry
        public const double AccessiblePersonsPerM2 = 0.1;
        public const double ActivityPersonsPerM2 = 0.3;
        public const double MarginalFrom = 0.8;
        public const double ColumnHighFactor = 1.5;              // a column carrying this much more than the mean
        public const double EccentricityMarginal = 0.05;         // centre of the load this far off the structure's centre, as a share of the dimension
        public const double EccentricityHigh = 0.10;
        public const double TargetEccentricity = 0.03;           // what a recommended move or lightening brings it to
        public const double MinItemShare = 0.05;                 // an item must carry this share of the load to be worth moving
        public const double MinStepGain = 0.01;                  // a step that doesn't reach the target must still take this much (of the dimension) off the eccentricity
        public const double LightenFloor = 0.4;                  // a build-up is not asked to be lighter than this share of what it is

        static readonly string[] SportPlayers = { "badminton:4", "tennis:4", "basketball:10", "handball:14", "volleyball:12", "football:10", "futsal:10" };

        public static double PlayersOn(string sport)
        {
            var s = (sport ?? "").Trim().ToLowerInvariant();
            foreach (var entry in SportPlayers)
            {
                var parts = entry.Split(':');
                if (s.Contains(parts[0])) return double.Parse(parts[1], CultureInfo.InvariantCulture);
            }
            return 8;
        }

        // ---------------------------------------------------------------- items from layout pieces

        /// <summary>A court: its floor, the code's sports load, its players and any seated spectators.</summary>
        public static LoadItem CourtItem(string id, string label, string sport, double x, double y, double w, double h, int seats)
        {
            var players = PlayersOn(sport);
            return new LoadItem
            {
                Id = id, Label = label, Name = label, Kind = LoadKind.Court, X = x, Y = y, Width = w, Height = h,
                DeadKnM2 = SportsSurfaceKnM2, LiveKnM2 = CourtLiveKnM2,
                Persons = players + seats, Players = players, Seats = seats,
                Basis = "sports area, category C4 (" + F1(CourtLiveKnM2) + " kN/m2); " + F0(players) + " players" + (seats > 0 ? " and " + seats + " seats" : ""),
            };
        }

        public static LoadItem ActivityItem(string id, string label, double x, double y, double w, double h)
        {
            return new LoadItem
            {
                Id = id, Label = label, Name = label, Kind = LoadKind.Activity, X = x, Y = y, Width = w, Height = h,
                DeadKnM2 = SportsSurfaceKnM2, LiveKnM2 = ActivityLiveKnM2,
                Persons = w * h * ActivityPersonsPerM2,
                Basis = "play / assembly area (" + F1(ActivityLiveKnM2) + " kN/m2, assumed); " + F1(ActivityPersonsPerM2) + " people per m2",
            };
        }

        /// <summary>A green-roof zone at its water-saturated weight: published where the provider prints it, else the dry layers plus the water at saturation.</summary>
        public static LoadItem ZoneItem(ZoneInput zone)
        {
            var a = zone.Assembly;
            string source;
            double kgM2;
            if (a != null && a.SaturatedKgM2.HasValue && a.SaturatedKgM2.Value > 0)
            {
                kgM2 = a.SaturatedKgM2.Value;
                source = "published saturated weight";
            }
            else
            {
                var spec = PercolationModel.SpecFor(a);
                string ignored;
                kgM2 = WindModel.DryWeightKgM2(a, out ignored) + PercolationModel.StorageAt(spec, spec.ThetaS);
                source = "dry layers plus water at saturation";
            }

            var paved = WindModel.Cover(a) == CoverKind.Paved;
            var intensive = a != null && string.Equals(a.Category, "intensive", StringComparison.OrdinalIgnoreCase);
            var accessible = paved || intensive;

            return new LoadItem
            {
                Id = zone.Id, Label = zone.Label, Kind = LoadKind.Zone,
                Name = "Green roof: " + (a != null && !string.IsNullOrEmpty(a.SystemName) ? a.SystemName : (a != null && !string.IsNullOrEmpty(a.System) ? a.System : "no build-up")),
                X = zone.X, Y = zone.Y, Width = zone.Width, Height = zone.Height,
                DeadKnM2 = kgM2 * Gravity / 1000.0,
                LiveKnM2 = accessible ? AccessibleLiveKnM2 : RoofLiveKnM2,
                Persons = accessible ? zone.Width * zone.Height * AccessiblePersonsPerM2 : 0,
                Basis = (a != null && !string.IsNullOrEmpty(a.System) ? a.System : "no build-up") + ": " + F0(kgM2) + " kg/m2 saturated (" + source + "); " +
                        (accessible ? "accessible, " + F1(AccessibleLiveKnM2) : "not accessible, " + F2(RoofLiveKnM2)) + " kN/m2 imposed",
            };
        }

        /// <summary>A tree's own weight as a concentrated permanent load. (Its root ball is the bed's substrate, already in the zone's weight.)</summary>
        public static LoadItem TreeItem(PlantInput plant)
        {
            var kn = TreeMassAtSixMetresKg * Math.Pow(plant.HeightM / 6.0, 2) * Gravity / 1000.0;
            return new LoadItem
            {
                Id = plant.Id, Label = plant.Species, Name = plant.Species, Kind = LoadKind.Tree,
                X = plant.X - plant.CrownM / 2, Y = plant.Y - plant.CrownM / 2, Width = plant.CrownM, Height = plant.CrownM,
                PointKn = kn,
                LiveKnM2 = RoofLiveKnM2,
                Basis = plant.Species + ", " + F1(plant.HeightM) + " m: " + F1(kn) + " kN above ground",
            };
        }

        // ---------------------------------------------------------------- the load field

        sealed class Claim
        {
            public double Q;                                       // imposed load intensity, kN/m2
            public List<KeyValuePair<int, double>> Cells = new List<KeyValuePair<int, double>>();   // cell index, share of the cell covered
        }

        static int CellIndexOf(double v, double size, int n)
        {
            return Math.Min(n - 1, Math.Max(0, (int)Math.Floor(v / size)));
        }

        public static LoadField BuildField(StructureInputs inputs)
        {
            var f = new LoadField
            {
                Nx = Math.Max(1, (int)Math.Floor(inputs.RoofLength / CellM + 0.5)),
                Ny = Math.Max(1, (int)Math.Floor(inputs.RoofWidth / CellM + 0.5)),
            };
            f.CellW = inputs.RoofLength / f.Nx;
            f.CellH = inputs.RoofWidth / f.Ny;
            var cw = f.CellW;
            var ch = f.CellH;
            var cellArea = cw * ch;
            var n = f.Nx * f.Ny;
            f.Dead = new double[n];
            f.Live = new double[n];
            f.Persons = new double[n];
            for (var i = 0; i < n; i++) f.Dead[i] = RoofFinishesKnM2 * cellArea;

            var claims = new List<Claim>();

            foreach (var item in inputs.Items)
            {
                var area = item.Width * item.Height;
                var claim = new Claim { Q = item.LiveKnM2 };
                if (area > 1e-12)
                {
                    var ix0 = CellIndexOf(item.X, cw, f.Nx);
                    var ix1 = CellIndexOf(item.X + item.Width, cw, f.Nx);
                    var iy0 = CellIndexOf(item.Y, ch, f.Ny);
                    var iy1 = CellIndexOf(item.Y + item.Height, ch, f.Ny);
                    for (var iy = iy0; iy <= iy1; iy++)
                    {
                        var oy = Math.Min(item.Y + item.Height, (iy + 1) * ch) - Math.Max(item.Y, iy * ch);
                        if (oy <= 0) continue;
                        for (var ix = ix0; ix <= ix1; ix++)
                        {
                            var ox = Math.Min(item.X + item.Width, (ix + 1) * cw) - Math.Max(item.X, ix * cw);
                            if (ox <= 0) continue;
                            var a = ox * oy;
                            var k = f.Index(ix, iy);
                            f.Dead[k] += item.DeadKnM2 * a;
                            f.Persons[k] += item.Persons * a / area;
                            claim.Cells.Add(new KeyValuePair<int, double>(k, a / cellArea));
                        }
                    }
                }
                claims.Add(claim);

                if (item.PointKn > 0) SpreadPoint(f, item.X + item.Width / 2, item.Y + item.Height / 2, item.PointKn);
            }

            // Circulation is accessible ground: at least the accessible imposed load on the cells its centre line crosses.
            foreach (var path in inputs.Paths)
            {
                var claim = new Claim { Q = AccessibleLiveKnM2 };
                for (var iy = 0; iy < f.Ny; iy++)
                    for (var ix = 0; ix < f.Nx; ix++)
                        if (DistanceToPath(path, (ix + 0.5) * cw, (iy + 0.5) * ch) <= path.WidthM / 2.0)
                            claim.Cells.Add(new KeyValuePair<int, double>(f.Index(ix, iy), 1.0));
                claims.Add(claim);
            }

            // The imposed load of each cell is the highest intensity of what covers it, weighted by how much of the cell each covers
            // (highest first; what is left over carries the roof's own). Exact for pieces that don't overlap or that nest.
            var claimed = new double[n];
            foreach (var claim in claims.OrderByDescending(c => c.Q))
            {
                foreach (var cell in claim.Cells)
                {
                    var take = Math.Min(cell.Value, 1.0 - claimed[cell.Key]);
                    if (take <= 0) continue;
                    f.Live[cell.Key] += claim.Q * take * cellArea;
                    claimed[cell.Key] += take;
                }
            }
            for (var i = 0; i < n; i++) f.Live[i] += RoofLiveKnM2 * (1.0 - claimed[i]) * cellArea;

            // People waiting at an entry.
            foreach (var e in inputs.Entries)
            {
                var near = new List<int>();
                var nearest = 0;
                var nearestD = double.MaxValue;
                for (var iy = 0; iy < f.Ny; iy++)
                {
                    for (var ix = 0; ix < f.Nx; ix++)
                    {
                        var dx = (ix + 0.5) * cw - e[0];
                        var dy = (iy + 0.5) * ch - e[1];
                        var d = Math.Sqrt(dx * dx + dy * dy);
                        if (d <= EntryRadiusM) near.Add(f.Index(ix, iy));
                        if (d < nearestD) { nearestD = d; nearest = f.Index(ix, iy); }
                    }
                }
                if (near.Count == 0) near.Add(nearest);
                foreach (var k in near) f.Persons[k] += EntryPersons / near.Count;
            }

            return f;
        }

        /// <summary>A concentrated load shared between the four cell centres around it, so it neither jumps nor tips at a cell edge.</summary>
        static void SpreadPoint(LoadField f, double x, double y, double kn)
        {
            var gx = x / f.CellW - 0.5;
            var gy = y / f.CellH - 0.5;
            var ix = (int)Math.Floor(gx);
            var iy = (int)Math.Floor(gy);
            var tx = gx - ix;
            var ty = gy - iy;
            for (var dy = 0; dy <= 1; dy++)
            {
                for (var dx = 0; dx <= 1; dx++)
                {
                    var w = (dx == 0 ? 1 - tx : tx) * (dy == 0 ? 1 - ty : ty);
                    if (w <= 0) continue;
                    var cx = Math.Min(f.Nx - 1, Math.Max(0, ix + dx));
                    var cy = Math.Min(f.Ny - 1, Math.Max(0, iy + dy));
                    f.Dead[f.Index(cx, cy)] += kn * w;
                }
            }
        }

        static double DistanceToPath(PathInput p, double x, double y)
        {
            var best = double.MaxValue;
            for (var i = 0; i + 1 < p.Points.Count; i++)
            {
                var a = p.Points[i];
                var b = p.Points[i + 1];
                double dx = b[0] - a[0], dy = b[1] - a[1];
                var len2 = dx * dx + dy * dy;
                var t = len2 < 1e-12 ? 0 : Math.Max(0, Math.Min(1, ((x - a[0]) * dx + (y - a[1]) * dy) / len2));
                var px = a[0] + t * dx - x;
                var py = a[1] + t * dy - y;
                best = Math.Min(best, Math.Sqrt(px * px + py * py));
            }
            return best;
        }

        // ---------------------------------------------------------------- bays

        /// <summary>The boundaries of the bays along one axis: the roof's edges and every grid line more than a cell inside it.</summary>
        public static double[] Boundaries(List<GridLineInput> lines, double length)
        {
            var list = new List<double> { 0, length };
            foreach (var l in lines)
                if (l.Position > CellM && l.Position < length - CellM) list.Add(l.Position);
            list.Sort();
            var merged = new List<double>();
            foreach (var v in list)
                if (merged.Count == 0 || v - merged[merged.Count - 1] > CellM) merged.Add(v);
            merged[merged.Count - 1] = length;
            return merged.ToArray();
        }

        static string NameBetween(List<GridLineInput> lines, double a, double b)
        {
            string Nearest(double pos)
            {
                var hit = lines.OrderBy(l => Math.Abs(l.Position - pos)).FirstOrDefault();
                return hit != null && Math.Abs(hit.Position - pos) < 0.5 && !string.IsNullOrEmpty(hit.Name) ? hit.Name : null;
            }
            var na = Nearest(a);
            var nb = Nearest(b);
            return na != null && nb != null ? na + "-" + nb : "";
        }

        /// <summary>For each cell along an axis, the bays it overlaps and the share of the cell in each.</summary>
        static List<KeyValuePair<int, double>>[] Overlaps(double[] bounds, int cells, double cell)
        {
            var result = new List<KeyValuePair<int, double>>[cells];
            for (var i = 0; i < cells; i++)
            {
                result[i] = new List<KeyValuePair<int, double>>();
                double lo = i * cell, hi = (i + 1) * cell;
                for (var b = 0; b + 1 < bounds.Length; b++)
                {
                    var o = Math.Min(hi, bounds[b + 1]) - Math.Max(lo, bounds[b]);
                    if (o > 1e-12) result[i].Add(new KeyValuePair<int, double>(b, o / cell));
                }
            }
            return result;
        }

        // ---------------------------------------------------------------- the analysis

        /// <summary>One line naming what was analysed, shared by the add-in's dialogs and the video's title card.</summary>
        public static string CaseStudy(StructureInputs inputs, StructureReport report)
        {
            var n = inputs.Items.Count;
            var bays = report.bays.Count;
            return n + " load piece" + (n == 1 ? "" : "s") + " on a " + F1(inputs.RoofLength) + " x " + F1(inputs.RoofWidth) + " m roof, " + bays + " bay" + (bays == 1 ? "" : "s") +
                   " (" + (report.summary.gridAssumed ? "assumed grid" : "grid from " + (string.IsNullOrEmpty(inputs.GridSource) ? "the layout" : inputs.GridSource)) + ")";
        }

        public static StructureReport Analyse(StructureInputs inputs)
        {
            var report = Compute(inputs);
            Recommend(report, inputs);
            return report;
        }

        /// <summary>The bay boundaries the analysis uses (x, y), the model's own grid or the assumed regular one.</summary>
        public static void BayBounds(StructureInputs inputs, out double[] xs, out double[] ys)
        {
            var assumed = inputs.VerticalLines.Count == 0 && inputs.HorizontalLines.Count == 0;
            xs = Boundaries(assumed ? RegularLines(inputs.RoofLength) : inputs.VerticalLines, inputs.RoofLength);
            ys = Boundaries(assumed ? RegularLines(inputs.RoofWidth) : inputs.HorizontalLines, inputs.RoofWidth);
        }

        /// <summary>Per-cell values (kN) summed into the bays, a cell across a grid line split by area; bay order as in the report (row by row).</summary>
        public static double[] BaySums(double[] cellValues, LoadField f, double[] xs, double[] ys)
        {
            var nbx = xs.Length - 1;
            var sums = new double[nbx * (ys.Length - 1)];
            var ovX = Overlaps(xs, f.Nx, f.CellW);
            var ovY = Overlaps(ys, f.Ny, f.CellH);
            for (var iy = 0; iy < f.Ny; iy++)
                for (var ix = 0; ix < f.Nx; ix++)
                {
                    var v = cellValues[f.Index(ix, iy)];
                    foreach (var py in ovY[iy])
                        foreach (var px in ovX[ix])
                            sums[py.Key * nbx + px.Key] += v * px.Value * py.Value;
                }
            return sums;
        }

        /// <summary>Everything except the advice.</summary>
        public static StructureReport Compute(StructureInputs inputs)
        {
            var report = new StructureReport { ran = true };

            // A grid the model doesn't give is assumed regular, and says so.
            var gridAssumed = inputs.VerticalLines.Count == 0 && inputs.HorizontalLines.Count == 0;
            var vLines = inputs.VerticalLines;
            var hLines = inputs.HorizontalLines;
            if (gridAssumed)
            {
                vLines = RegularLines(inputs.RoofLength);
                hLines = RegularLines(inputs.RoofWidth);
            }
            var xs = Boundaries(vLines, inputs.RoofLength);
            var ys = Boundaries(hLines, inputs.RoofWidth);
            report.verticalLinesM = xs.Select(v => (float)v).ToArray();
            report.horizontalLinesM = ys.Select(v => (float)v).ToArray();

            var field = BuildField(inputs);
            var cw = field.CellW;
            var ch = field.CellH;
            var capacityAssumed = !inputs.CapacityKnM2.HasValue;
            var capacity = inputs.CapacityKnM2 ?? DefaultCapacityKnM2;
            report.assumptions.AddRange(Assumptions(capacity, capacityAssumed, gridAssumed, inputs.GridSource));
            report.assumptionUses.Add(AnalysisAssumptions.Use(AnalysisAssumptions.DeckCapacity, !capacityAssumed, F1(capacity) + " kN/m2", inputs.AcceptedAssumptions));
            report.assumptions.AddRange(AnalysisAssumptions.Lines(report.assumptionUses));
            report.assumptions.AddRange(inputs.Notes);

            // ---- bays: each cell splits over the bays it overlaps, by area
            var nbx = xs.Length - 1;
            var nby = ys.Length - 1;
            var nb = nbx * nby;
            var dead = new double[nb];
            var live = new double[nb];
            var persons = new double[nb];
            var ovX = Overlaps(xs, field.Nx, cw);
            var ovY = Overlaps(ys, field.Ny, ch);
            for (var iy = 0; iy < field.Ny; iy++)
            {
                for (var ix = 0; ix < field.Nx; ix++)
                {
                    var k = field.Index(ix, iy);
                    foreach (var py in ovY[iy])
                    {
                        foreach (var px in ovX[ix])
                        {
                            var b = py.Key * nbx + px.Key;
                            var w = px.Value * py.Value;
                            dead[b] += field.Dead[k] * w;
                            live[b] += field.Live[k] * w;
                            persons[b] += field.Persons[k] * w;
                        }
                    }
                }
            }

            double totalDead = 0, totalLive = 0, totalPersons = 0;
            for (var k = 0; k < field.Dead.Length; k++)
            {
                totalDead += field.Dead[k];
                totalLive += field.Live[k];
                totalPersons += field.Persons[k];
            }
            var roofArea = inputs.RoofLength * inputs.RoofWidth;

            for (var r = 0; r < nby; r++)
            {
                for (var c = 0; c < nbx; c++)
                {
                    var b = r * nbx + c;
                    var bay = new BayResult
                    {
                        label = "Bay " + (c + 1) + "." + (r + 1),
                        gridNames = JoinNames(NameBetween(vLines, xs[c], xs[c + 1]), NameBetween(hLines, ys[r], ys[r + 1])),
                        x0 = (float)xs[c], x1 = (float)xs[c + 1], y0 = (float)ys[r], y1 = (float)ys[r + 1],
                    };
                    var area = (xs[c + 1] - xs[c]) * (ys[r + 1] - ys[r]);
                    var total = dead[b] + live[b];
                    bay.areaM2 = (float)area;
                    bay.deadKn = (float)dead[b];
                    bay.liveKn = (float)live[b];
                    bay.totalKn = (float)total;
                    bay.totalKnM2 = (float)(total / area);
                    bay.utilisation = (float)(total / area / capacity);
                    bay.status = bay.utilisation > 1.0f ? "over" : (bay.utilisation > MarginalFrom ? "marginal" : "ok");
                    bay.persons = (float)persons[b];
                    bay.personsPerM2 = (float)(persons[b] / area);
                    bay.crowdKn = (float)(persons[b] * PersonMassKg * Gravity / 1000.0);
                    bay.shareOfLoadPercent = (float)(100.0 * total / Math.Max(1e-9, totalDead + totalLive));
                    bay.topContributor = TopContributor(inputs, bay);
                    report.bays.Add(bay);
                }
            }

            // ---- columns and their tributary loads
            var columns = inputs.Columns.Count > 0 ? inputs.Columns : Intersections(vLines, hLines, inputs.RoofLength, inputs.RoofWidth);
            if (columns.Count > 0)
            {
                var load = new double[columns.Count];
                var area = new double[columns.Count];
                for (var iy = 0; iy < field.Ny; iy++)
                {
                    for (var ix = 0; ix < field.Nx; ix++)
                    {
                        var x = (ix + 0.5) * cw;
                        var y = (iy + 0.5) * ch;
                        var best = 0;
                        var bestD = double.MaxValue;
                        for (var c = 0; c < columns.Count; c++)
                        {
                            var d = (columns[c][0] - x) * (columns[c][0] - x) + (columns[c][1] - y) * (columns[c][1] - y);
                            if (d < bestD) { bestD = d; best = c; }
                        }
                        var k = field.Index(ix, iy);
                        load[best] += field.Dead[k] + field.Live[k];
                        area[best] += cw * ch;
                    }
                }
                var mean = load.Average();
                for (var c = 0; c < columns.Count; c++)
                {
                    report.columns.Add(new ColumnResult
                    {
                        label = "Column " + (c + 1),
                        x = (float)columns[c][0], y = (float)columns[c][1],
                        tributaryM2 = (float)area[c], loadKn = (float)load[c],
                        ratioToMean = (float)(mean > 0 ? load[c] / mean : 1),
                        status = mean > 0 && load[c] > ColumnHighFactor * mean ? "high" : "ok",
                    });
                }
            }

            // ---- items
            foreach (var item in inputs.Items)
            {
                var area = item.Width * item.Height;
                report.items.Add(new ItemLoadResult
                {
                    id = item.Id, label = item.Label, kind = item.Kind.ToString(), basis = item.Basis,
                    areaM2 = (float)area, deadKnM2 = (float)item.DeadKnM2, liveKnM2 = (float)item.LiveKnM2,
                    deadKn = (float)(item.DeadKnM2 * area + item.PointKn),
                    liveKn = (float)(item.LiveKnM2 * area),
                    persons = (float)item.Persons,
                });
            }

            report.balance = Balance(inputs, field, columns);

            var s = new StructureSummary
            {
                roofAreaM2 = (float)roofArea,
                deadKn = (float)totalDead, liveKn = (float)totalLive, totalKn = (float)(totalDead + totalLive),
                meanKnM2 = (float)((totalDead + totalLive) / roofArea),
                peakBayKnM2 = report.bays.Max(b => b.totalKnM2),
                capacityKnM2 = (float)capacity, capacityAssumed = capacityAssumed,
                capacityAccepted = report.assumptionUses[0].state == AnalysisAssumptions.Accepted,
                preliminary = AnalysisAssumptions.IsPreliminary(report.assumptionUses),
                preliminaryNote = AnalysisAssumptions.PreliminaryNote(report.assumptionUses),
                acceptedNote = AnalysisAssumptions.AcceptedNote(report.assumptionUses),
                gridSource = gridAssumed ? "assumed regular " + F1(AssumedBayM) + " m grid" : (string.IsNullOrEmpty(inputs.GridSource) ? "given with the layout" : inputs.GridSource),
                gridAssumed = gridAssumed,
                baysChecked = report.bays.Count,
                baysOver = report.bays.Count(b => b.status == "over"),
                baysMarginal = report.bays.Count(b => b.status == "marginal"),
                columnsChecked = report.columns.Count,
                columnsHigh = report.columns.Count(c => c.status == "high"),
                expectedPersons = (float)totalPersons,
                peakUtilisation = report.bays.Max(b => b.utilisation),
                worstBay = report.bays.OrderByDescending(b => b.utilisation).First().label,
            };
            var busiest = report.bays.OrderByDescending(b => b.persons).Take(Math.Max(1, (int)Math.Ceiling(report.bays.Count * 0.2))).Sum(b => b.persons);
            s.busiestBaysSharePercent = totalPersons > 0 ? (float)(100.0 * busiest / totalPersons) : 0f;
            report.summary = s;
            return report;
        }

        static string JoinNames(string across, string down)
        {
            return string.IsNullOrEmpty(across) || string.IsNullOrEmpty(down) ? "" : across + " / " + down;
        }

        static List<GridLineInput> RegularLines(double length)
        {
            var n = Math.Max(1, (int)Math.Floor(length / AssumedBayM + 0.5));   // half up, not to even, so every reader of the layout agrees
            var lines = new List<GridLineInput>();
            for (var i = 1; i < n; i++) lines.Add(new GridLineInput { Name = "", Position = length * i / n });
            return lines;
        }

        static List<double[]> Intersections(List<GridLineInput> v, List<GridLineInput> h, double length, double width)
        {
            var list = new List<double[]>();
            foreach (var x in v.Where(l => l.Position >= -0.3 && l.Position <= length + 0.3))
                foreach (var y in h.Where(l => l.Position >= -0.3 && l.Position <= width + 0.3))
                    list.Add(new[] { x.Position, y.Position });
            return list;
        }

        static string TopContributor(StructureInputs inputs, BayResult bay)
        {
            double overlap;
            var item = TopItem(inputs, bay, out overlap);
            return item == null ? "" : item.Label;
        }

        /// <summary>The piece that puts most load into the bay, and how much of the bay it covers (m2).</summary>
        static LoadItem TopItem(StructureInputs inputs, BayResult bay, out double overlapM2)
        {
            LoadItem best = null;
            overlapM2 = 0;
            var bestKn = 0.0;
            foreach (var item in inputs.Items)
            {
                var w = Math.Max(0, Math.Min(item.X + item.Width, bay.x1) - Math.Max(item.X, bay.x0));
                var h = Math.Max(0, Math.Min(item.Y + item.Height, bay.y1) - Math.Max(item.Y, bay.y0));
                var kn = (item.DeadKnM2 + item.LiveKnM2) * w * h + (item.PointKn > 0 && w > 0 && h > 0 ? item.PointKn * w * h / Math.Max(1e-9, item.Width * item.Height) : 0);
                if (kn > bestKn) { bestKn = kn; best = item; overlapM2 = w * h; }
            }
            return best;
        }

        // ---------------------------------------------------------------- balance

        static BalanceResult Balance(StructureInputs inputs, LoadField f, List<double[]> columns)
        {
            var b = new BalanceResult();
            if (columns.Count > 0)
            {
                b.centreX = (float)columns.Average(c => c[0]);
                b.centreY = (float)columns.Average(c => c[1]);
                b.centreBasis = "the columns' centroid";
            }
            else
            {
                b.centreX = (float)(inputs.RoofLength / 2);
                b.centreY = (float)(inputs.RoofWidth / 2);
                b.centreBasis = "the plan centre";
            }

            var cw = f.CellW;
            var ch = f.CellH;
            double dSum = 0, dx = 0, dy = 0, tSum = 0, tx = 0, ty = 0;
            double left = 0, right = 0, top = 0, bottom = 0;
            for (var iy = 0; iy < f.Ny; iy++)
            {
                for (var ix = 0; ix < f.Nx; ix++)
                {
                    var k = f.Index(ix, iy);
                    var x = (ix + 0.5) * cw;
                    var y = (iy + 0.5) * ch;
                    var d = f.Dead[k];
                    var t = f.Dead[k] + f.Live[k];
                    dSum += d; dx += d * x; dy += d * y;
                    tSum += t; tx += t * x; ty += t * y;
                    var fl = Math.Max(0, Math.Min(1, (b.centreX - ix * cw) / cw));   // share of the cell left of the centre
                    var ft = Math.Max(0, Math.Min(1, (b.centreY - iy * ch) / ch));   // share above it
                    left += t * fl; right += t * (1 - fl);
                    top += t * ft; bottom += t * (1 - ft);
                }
            }

            b.deadCentroidX = (float)(dx / dSum);
            b.deadCentroidY = (float)(dy / dSum);
            b.totalCentroidX = (float)(tx / tSum);
            b.totalCentroidY = (float)(ty / tSum);
            b.deadEccentricityX = (float)((dx / dSum - b.centreX) / inputs.RoofLength);
            b.deadEccentricityY = (float)((dy / dSum - b.centreY) / inputs.RoofWidth);
            b.totalEccentricityX = (float)((tx / tSum - b.centreX) / inputs.RoofLength);
            b.totalEccentricityY = (float)((ty / tSum - b.centreY) / inputs.RoofWidth);
            b.leftSharePercent = (float)(100.0 * left / tSum);
            b.rightSharePercent = (float)(100.0 * right / tSum);
            b.topSharePercent = (float)(100.0 * top / tSum);
            b.bottomSharePercent = (float)(100.0 * bottom / tSum);

            var ex = Math.Abs(b.totalEccentricityX);
            var ey = Math.Abs(b.totalEccentricityY);
            var worst = Math.Max(ex, ey);
            b.status = worst > EccentricityHigh ? "unbalanced" : (worst > EccentricityMarginal ? "marginal" : "balanced");
            b.heavySide = worst <= EccentricityMarginal ? "" : (ex >= ey ? (b.totalEccentricityX > 0 ? "right" : "left") : (b.totalEccentricityY > 0 ? "bottom" : "top"));
            return b;
        }

        // ---------------------------------------------------------------- advice

        static string F0(double v) { return v.ToString("0", CultureInfo.InvariantCulture); }
        static string F1(double v) { return v.ToString("0.#", CultureInfo.InvariantCulture); }
        static string F2(double v) { return v.ToString("0.##", CultureInfo.InvariantCulture); }
        static string Pct(double fraction) { return F1(Math.Abs(fraction) * 100.0) + "%"; }

        static string SideWord(string axis, bool positive)
        {
            return axis == "x" ? (positive ? "right" : "left") : (positive ? "bottom" : "top");
        }

        static double Ecc(StructureReport r, string axis)
        {
            return axis == "x" ? r.balance.totalEccentricityX : r.balance.totalEccentricityY;
        }

        static LoadItem Moved(LoadItem item, string axis, double by)
        {
            var c = item.Copy();
            if (axis == "x") c.X += by; else c.Y += by;
            return c;
        }

        static void Recommend(StructureReport r, StructureInputs inputs)
        {
            var s = r.summary;
            var b = r.balance;
            var capacity = s.capacityKnM2;

            foreach (var bay in r.bays.Where(x => x.status == "over").OrderByDescending(x => x.utilisation).Take(3))
            {
                double overlap;
                var top = TopItem(inputs, bay, out overlap);
                var fit = "";
                if (top != null && top.DeadKnM2 > 0 && overlap > 0)
                {
                    // the piece's permanent load that would bring this bay exactly to its capacity, if only that piece changed
                    var fitKnM2 = top.DeadKnM2 - (bay.totalKnM2 - capacity) / (overlap / bay.areaM2);
                    fit = fitKnM2 >= top.DeadKnM2 * LightenFloor
                        ? top.Label + " would have to come down from " + F2(top.DeadKnM2) + " to " + F2(fitKnM2) + " kN/m2 (" + F0(fitKnM2 * 1000 / Gravity) + " kg/m2) here to fit. Otherwise, "
                        : "Lightening " + top.Label + " alone will not bring it within the capacity: ";
                }
                r.recommendations.Add(new StructureRecommendation
                {
                    kind = "over-capacity",
                    target = bay.label,
                    text = bay.label + (string.IsNullOrEmpty(bay.gridNames) ? "" : " (" + bay.gridNames + ")") + " carries " + F1(bay.totalKnM2) + " kN/m2 against a capacity of " +
                           F1(capacity) + " (" + F0(bay.utilisation * 100) + "%)" + (string.IsNullOrEmpty(bay.topContributor) ? "" : ", most of it from " + bay.topContributor) +
                           ". " + (fit == "" ? "Lighten what stands there, " : fit) + "move it to a lighter bay or have the deck checked" + (s.capacityAssumed ? (s.capacityAccepted ? " (the capacity is the built-in assumption you accepted, not the engineer's figure)" : " (the capacity is a placeholder: enter the engineer's figure)") : "") + ".",
                });
            }

            var vulnerable = r.bays.OrderByDescending(x => x.utilisation).Take(3).ToList();
            r.recommendations.Add(new StructureRecommendation
            {
                kind = "vulnerable",
                target = string.Join(", ", vulnerable.Select(x => x.label).ToArray()),
                text = "Most heavily loaded: " + string.Join("; ", vulnerable.Select(x => x.label + (string.IsNullOrEmpty(x.gridNames) ? "" : " (" + x.gridNames + ")") + " " + F0(x.utilisation * 100) + "%" +
                       (string.IsNullOrEmpty(x.topContributor) ? "" : " under " + x.topContributor)).ToArray()) + ".",
            });

            foreach (var axis in new[] { "x", "y" })
            {
                var e = Ecc(r, axis);
                if (Math.Abs(e) > EccentricityMarginal) AdviseBalance(r, inputs, axis);
            }

            if (s.expectedPersons > 0)
            {
                var busiest = r.bays.OrderByDescending(x => x.persons).Take(Math.Max(1, (int)Math.Ceiling(r.bays.Count * 0.2))).ToList();
                if (s.busiestBaysSharePercent >= 50f && r.bays.Count >= 5)
                {
                    var names = string.Join(", ", busiest.Select(x => x.label).ToArray());
                    r.recommendations.Add(new StructureRecommendation
                    {
                        kind = "concentration",
                        target = names,
                        text = "About " + F0(s.busiestBaysSharePercent) + "% of the " + F0(s.expectedPersons) + " expected people are in " + busiest.Count + " of " + r.bays.Count + " bays (" + names +
                               "). Keep heavy permanent loads out of them and check the deck there first.",
                    });
                }
            }

            foreach (var col in r.columns.Where(x => x.status == "high").OrderByDescending(x => x.ratioToMean).Take(2))
            {
                r.recommendations.Add(new StructureRecommendation
                {
                    kind = "over-capacity",
                    target = col.label,
                    text = col.label + " at (" + F1(col.x) + ", " + F1(col.y) + ") takes " + F0(col.loadKn) + " kN, " + F1(col.ratioToMean) + "x the average column. Move load off its tributary area or have it checked.",
                });
            }

            if (s.gridAssumed)
            {
                r.recommendations.Add(new StructureRecommendation
                {
                    kind = "grid",
                    target = "Structural grid",
                    text = "The layout carries no structural grid, so a regular " + F1(AssumedBayM) + " m grid was assumed. Push the roof from Revit (its grids and columns come with it) for real bays.",
                });
            }

            if (s.baysOver == 0 && b.status == "balanced")
            {
                r.recommendations.Insert(0, new StructureRecommendation
                {
                    kind = "fine",
                    target = "All bays",
                    text = "No bay is over its capacity, and the centre of the load is within " + F0(EccentricityMarginal * 100) + "% of the structure's centre.",
                });
            }
        }

        sealed class Step
        {
            public bool IsMove;
            public int Index;
            public double Amount;          // metres moved, or the new permanent load in kN/m2
            public bool Hits;              // reaches the target
            public StructureInputs State;
            public StructureReport After;
        }

        /// <summary>
        /// The balance on one axis, put right by up to three steps, each one piece moved toward the light side or made lighter, each found by
        /// re-running the model: the smallest move that reaches the target if one does (before any lightening), else the step that gets
        /// closest. A step is refused if it would push the busiest bay further over its capacity than it already is.
        /// </summary>
        static void AdviseBalance(StructureReport r, StructureInputs inputs, string axis)
        {
            var b = r.balance;
            var e0 = Ecc(r, axis);
            var sign = e0 > 0 ? 1.0 : -1.0;
            var heavy = SideWord(axis, e0 > 0);
            var light = SideWord(axis, e0 <= 0);
            var share = axis == "x" ? (e0 > 0 ? b.rightSharePercent : b.leftSharePercent) : (e0 > 0 ? b.bottomSharePercent : b.topSharePercent);
            var dimName = axis == "x" ? "length" : "width";
            var head = F0(share) + "% of the load sits on the " + heavy + " half (its centre is " + Pct(e0) + " of the " + dimName + " off the structure's centre). ";

            var state = inputs;
            var cur = r;
            var steps = 0;
            for (; steps < 3; steps++)
            {
                if (Math.Abs(Ecc(cur, axis)) <= TargetEccentricity + 0.005) break;
                var step = BestStep(cur, state, axis, sign, inputs);
                if (step == null) break;

                var item = state.Items[step.Index];
                var beforePeak = cur.summary.peakUtilisation;
                var afterE = Ecc(step.After, axis);
                var peakText = step.After.summary.peakUtilisation > beforePeak + 0.005
                    ? ", and the busiest bay goes from " + F0(beforePeak * 100) + "% to " + F0(step.After.summary.peakUtilisation * 100) + "%"
                    : (step.After.summary.peakUtilisation < beforePeak - 0.005 ? ", and the busiest bay comes down from " + F0(beforePeak * 100) + "% to " + F0(step.After.summary.peakUtilisation * 100) + "%" : "");
                var rec = new StructureRecommendation
                {
                    kind = step.IsMove ? "move" : "lighten", target = item.Label, itemId = item.Id, axis = axis,
                    eccentricityAfter = (float)afterE, peakUtilisationAfter = step.After.summary.peakUtilisation,
                };
                if (step.IsMove)
                {
                    rec.moveM = (float)(-sign * step.Amount);
                    rec.text = (steps == 0 ? head : "Then ") + "Move " + item.Label + " " + F1(step.Amount) + " m toward the " + light + ": the centre of the load goes to " + Pct(afterE) + " off" + peakText + ".";
                }
                else
                {
                    rec.newDeadKnM2 = (float)step.Amount;
                    rec.text = (steps == 0 ? head : "Then ") + "Bring " + item.Label + " down from " + F2(item.DeadKnM2) + " to about " + F2(step.Amount) + " kN/m2 (" + F0(step.Amount * 1000 / Gravity) +
                               " kg/m2: a lighter build-up or a thinner substrate): the centre of the load goes to " + Pct(afterE) + " off" + peakText + ".";
                }
                r.recommendations.Add(rec);
                state = step.State;
                cur = step.After;
            }

            if (steps == 0)
            {
                r.recommendations.Add(new StructureRecommendation
                {
                    kind = "move", target = "Load on the " + heavy + " half", axis = axis,
                    text = head + "No single move or lightening of one piece puts it right without overloading a bay: shift several pieces toward the " + light + " or lighten what stays on the " + heavy + " half.",
                });
            }
            else if (Math.Abs(Ecc(cur, axis)) > TargetEccentricity + 0.005)
            {
                var last = r.recommendations[r.recommendations.Count - 1];
                last.text += " That is as far as these steps go: " + Pct(Ecc(cur, axis)) + " is still off, so shift more load toward the " + light + ".";
            }
        }

        /// <summary>How far a piece can slide along an axis (direction -1 / +1) before it leaves the roof or runs into another piece.</summary>
        static double FreeTravel(StructureInputs state, int index, string axis, double direction)
        {
            var item = state.Items[index];
            var x = axis == "x";
            double lo = x ? item.X : item.Y, size = x ? item.Width : item.Height;
            double crossLo = x ? item.Y : item.X, crossSize = x ? item.Height : item.Width;
            var travel = direction < 0 ? lo : (x ? state.RoofLength : state.RoofWidth) - (lo + size);
            for (var k = 0; k < state.Items.Count; k++)
            {
                var o = state.Items[k];
                if (k == index || o.Kind == LoadKind.Tree) continue;
                double olo = x ? o.X : o.Y, osize = x ? o.Width : o.Height;
                double ocrossLo = x ? o.Y : o.X, ocrossSize = x ? o.Height : o.Width;
                if (Math.Min(crossLo + crossSize, ocrossLo + ocrossSize) - Math.Max(crossLo, ocrossLo) <= 0.05) continue;   // beside it, not in its way
                if (direction < 0 && olo + osize <= lo + 1e-9) travel = Math.Min(travel, lo - (olo + osize));
                if (direction > 0 && olo >= lo + size - 1e-9) travel = Math.Min(travel, olo - (lo + size));
            }
            return travel;
        }

        static Step BestStep(StructureReport cur, StructureInputs state, string axis, double sign, StructureInputs original)
        {
            var centre = axis == "x" ? cur.balance.centreX : cur.balance.centreY;
            var e0 = Math.Abs(Ecc(cur, axis));
            var peakLimit = Math.Max(cur.summary.peakUtilisation, 1.0) + 0.005;

            // Pieces that carry a fair share of the load and sit on the heavy side, heaviest first.
            var candidates = new List<int>();
            for (var i = 0; i < state.Items.Count; i++)
            {
                var item = state.Items[i];
                if (item.Kind == LoadKind.Tree) continue;
                var kn = (item.DeadKnM2 + item.LiveKnM2) * item.Width * item.Height;
                var pos = axis == "x" ? item.X + item.Width / 2 : item.Y + item.Height / 2;
                if (kn < MinItemShare * cur.summary.totalKn || (pos - centre) * sign <= 0) continue;
                candidates.Add(i);
            }
            candidates = candidates.OrderByDescending(i => (state.Items[i].DeadKnM2 + state.Items[i].LiveKnM2) * state.Items[i].Width * state.Items[i].Height).Take(4).ToList();

            Step best = null;
            Action<Step> consider = s =>
            {
                var e = Math.Abs(Ecc(s.After, axis));
                if (s.After.summary.peakUtilisation > peakLimit || e >= e0 - (s.Hits ? 1e-4 : MinStepGain)) return;
                if (best == null) { best = s; return; }
                var better = s.Hits != best.Hits ? s.Hits
                           : s.Hits ? (s.IsMove != best.IsMove ? s.IsMove : (s.IsMove ? s.Amount < best.Amount : s.Amount > best.Amount))
                           : e < Math.Abs(Ecc(best.After, axis));
                if (better) best = s;
            };

            foreach (var i in candidates)
            {
                var index = i;
                var item = state.Items[i];

                // a move toward the light side
                var room = Math.Max(0, FreeTravel(state, i, axis, -sign));
                if (room >= 0.05)
                {
                    Func<double, StructureInputs> at = by => state.WithItem(index, Moved(item, axis, -sign * by));
                    Func<double, double> excess = by => sign * Ecc(Compute(at(by)), axis) - TargetEccentricity;
                    var amount = room;
                    var hits = excess(room) <= 0;
                    if (hits)
                    {
                        double lower = 0, upper = room;
                        for (var it = 0; it < 22; it++)
                        {
                            var mid = (lower + upper) / 2;
                            if (excess(mid) > 0) lower = mid; else upper = mid;
                        }
                        amount = Math.Min(room, Math.Ceiling(upper * 10 - 1e-9) / 10.0);
                    }
                    var st = at(amount);
                    consider(new Step { IsMove = true, Index = index, Amount = amount, Hits = hits, State = st, After = Compute(st) });
                }

                // a lighter build-up (once per piece: a second step would compound the floor)
                if (item.DeadKnM2 > 0 && Math.Abs(item.DeadKnM2 - original.Items[index].DeadKnM2) < 1e-12)
                {
                    Func<double, StructureInputs> at = v => { var c = item.Copy(); c.DeadKnM2 = v; return state.WithItem(index, c); };
                    Func<double, double> excess = v => sign * Ecc(Compute(at(v)), axis) - TargetEccentricity;
                    var floor = item.DeadKnM2 * LightenFloor;
                    var amount = floor;
                    var hits = excess(floor) <= 0;
                    if (hits)
                    {
                        double lower = floor, upper = item.DeadKnM2;   // excess(lower) <= 0 < excess(upper)
                        for (var it = 0; it < 22; it++)
                        {
                            var mid = (lower + upper) / 2;
                            if (excess(mid) > 0) upper = mid; else lower = mid;
                        }
                        amount = Math.Floor(lower * 100 + 1e-9) / 100.0;
                    }
                    else amount = Math.Ceiling(floor * 100 - 1e-9) / 100.0;
                    var st = at(amount);
                    consider(new Step { IsMove = false, Index = index, Amount = amount, Hits = hits, State = st, After = Compute(st) });
                }
            }
            return best;
        }

        static List<string> Assumptions(double capacity, bool capacityAssumed, bool gridAssumed, string gridSource)
        {
            return new List<string>
            {
                "Screening model, not a structural verification: loads that sit ON the structure (its own weight is outside), on the roof's bounding rectangle, in 0.5 m cells split over the bays of the structural grid by area.",
                "Permanent: green-roof build-ups at their water-saturated weight (published where the provider prints it, else dry layers plus water at saturation), a tree's weight as a point load, " +
                    F1(SportsSurfaceKnM2) + " kN/m2 under courts and play areas, " + F1(RoofFinishesKnM2) + " kN/m2 of roof finishes everywhere.",
                "Imposed (characteristic, the highest intensity where areas overlap): courts " + F1(CourtLiveKnM2) + " kN/m2 (category C4, DIN EN 1991-1-1/NA Table 6.1DE); play and assembly areas " + F1(ActivityLiveKnM2) +
                    "; accessible roof gardens, walkways and circulation " + F1(AccessibleLiveKnM2) + "; a roof not accessible but for maintenance " + F2(RoofLiveKnM2) + " (category H: check the National Annex). Only the court value is from the standard; the others are the author's.",
                "People (where activity concentrates, not what the imposed load is made of): players by sport, seated spectators, " + F1(AccessiblePersonsPerM2) + " per m2 on accessible gardens, " + F1(ActivityPersonsPerM2) +
                    " per m2 on play areas, " + F0(EntryPersons) + " waiting at each entry; " + F0(PersonMassKg) + " kg each.",
                "Capacity: " + F1(capacity) + " kN/m2 characteristic G + Q, the same in every bay" + (capacityAssumed ? ". This is a PLACEHOLDER (5.0 sports use + 3.0 permanent): the layout carries no deck capacity, so enter the structural engineer's figure." : "."),
                "Balance: the centre of the total (G + Q) load against the columns' centroid (else the plan centre); off by more than " + F0(EccentricityMarginal * 100) + "% of the dimension is marginal, more than " +
                    F0(EccentricityHigh * 100) + "% unbalanced. Advice moves one piece until it is " + F0(TargetEccentricity * 100) + "%. Column loads are tributary areas (each cell to its nearest column).",
                gridAssumed ? "No structural grid in the layout: a regular " + F1(AssumedBayM) + " m grid is assumed." : "Structural grid from " + (string.IsNullOrEmpty(gridSource) ? "the layout" : gridSource) + ".",
            };
        }
    }
}
