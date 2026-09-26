#nullable disable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Sportify.Simulation.Structure;
using Sportify.Simulation.Wind;

namespace Sportify.Simulation.Sun
{
    // ---------------------------------------------------------------------------------------
    //  Sun and shade on the roof: how many hours of direct sun each part of the roof gets, where the roof
    //  is too sunny for the people who stay on it or too shaded for the plants, and which shading
    //  equipment would fix it without taking the sun from the gardens.
    //
    //  Like the other analyses it uses nothing from UnityEngine, so the numbers appear in the Revit add-in
    //  with no Unity installed; Unity only films them (the shadows sweeping the roof, the sun-hours
    //  building up, the equipment going up). It is a SCREENING model, not a daylight or thermal design.
    //
    //  THE SUN. Latitude and the day of the year give the sun's declination (Spencer's Fourier series); the
    //  hour angle is 15 degrees per hour of SOLAR time from solar noon (clock time differs by the longitude,
    //  the time zone, summer time and the equation of time, so the analysis works in solar time and says so).
    //    sin(alt) = sin(lat) sin(decl) + cos(lat) cos(decl) cos(hourAngle)
    //    the bearing is atan2(east, north) of the sun's direction, clockwise from north.
    //  The sun counts as up from MinElevationDeg (2 degrees).
    //
    //  THE PLAN. x right, y DOWN, metres, as everywhere. northDeg is the compass bearing of the top of the
    //  plan (as in the wind analysis), so a compass bearing b lies at angle (b - northDeg) clockwise from
    //  the top, and the horizontal direction TOWARD the sun is (sin t, -cos t) with t = sunBearing - northDeg.
    //
    //  THE SHADOWS. A point at height H above the roof throws its shadow H / tan(alt) away from the sun.
    //    a wall (a segment with a thickness and a height): its footprint rectangle swept along that
    //      displacement, the convex hull of the footprint and the shifted footprint (a wall of a closed
    //      stair house or plant room is enough: the swept boundary and the footprint make the whole shadow);
    //    an equipment plate at height H (a rectangle, or a parasol's disc): the plate shifted by the same
    //      displacement (a plate parallel to the roof keeps its shape);
    //    a tree canopy: a sphere of the crown's radius r whose centre is at max(r, height - r); its shadow is
    //      an ellipse, semi-axis r across the sun and r / sin(alt) along it, centred on the shadow of the
    //      sphere's centre.
    //  Every shadow lets a share of the sun through (tau: 0 for a solid canopy, 0.25 for a tree in summer).
    //  The sunlit fraction of a point is the product of tau over every shadow that covers it.
    //
    //  THE ROOF. Cut into 0.5 m cells; a cell counts when its centre is inside the roof's outline (the
    //  bounding rectangle when there is none). Every 0.5 h of solar time (midpoints) the sunlit fraction of
    //  every cell is worked out; sun hours = the sum of fraction x 0.5 h. Three design days: 21 June, 21
    //  March, 21 December.
    //
    //  THE ZONES. Every play area / activity is a "people" zone, every court that has seats has a
    //  "spectators" band of SpectatorMarginM around it, every green-roof zone is a "garden". The judgement:
    //    people, spectators  the mean SHADE over the heat window (11:00 to 16:00 solar time) on 21 June must be at least
    //                        the shade target (default 50%), else "too sunny";
    //    garden              the sun hours on 21 June must be at least the garden's need (default 4 h), else "too shaded".
    //
    //  THE EQUIPMENT. For each people zone below its target, worst first, every catalogue piece of the allowed
    //  kinds is tried at every 1 m position around the zone (both orientations of a rectangle): the piece that
    //  raises the zone's mean heat-window shade most is taken, provided it fits (inside the roof, off the courts,
    //  the drains, the openings, the entries and taller walls, its posts clear of the walkways, off other pieces)
    //  and does not take a garden below its need or lower one already below it by more than 0.25 h. Up to
    //  MaxPieces pieces, MaxPerZone per zone; a piece must add MinGain (3 points) to be worth it. The sun is then
    //  re-run with the pieces in place. Each piece's weight and wind uplift are added, and the static structural
    //  analysis is re-run with them so the deck's answer is verified, not estimated.
    //
    //  Every constant that is a judgement call is named below and repeated in the report's assumptions.
    //
    //  Units: metres, hours, degrees, kN, kN/m2, Pa. Plan coordinates: x right, y DOWN from the top edge.
    // ---------------------------------------------------------------------------------------

    public class ObstacleInput
    {
        public string Name = "";
        public double X0, Y0, X1, Y1;        // the wall's centre line, plan metres
        public double HeightM;               // above the roof's top face
        public double ThicknessM;
    }

    public class SunInputs
    {
        public StructureInputs Structure;    // roof size, pieces (courts, activities, green-roof zones), entries, walkways, deck capacity
        public WindInputs Wind;              // plants (the trees) and the wind at the roof, for the wind on the equipment
        public double? LatitudeDeg;          // null = not given
        public double? NorthDeg;             // compass bearing of the top of the plan; null = not given
        public double? ShadeTargetPercent;   // null = the built-in one
        public double? GardenMinSunHours;
        public string Equipment;             // "all" | "light" | "fixed"; null = all
        public List<double[]> Outline = new List<double[]>();          // the roof's outline {x, y} in plan metres; empty = the bounding rectangle
        public List<ObstacleInput> Obstacles = new List<ObstacleInput>();
        public List<double[]> Drains = new List<double[]>();           // {x, y}
        public List<double[][]> Openings = new List<double[][]>();     // polygons of {x, y}
    }

    public struct SunPosition
    {
        public double ElevationDeg, BearingDeg;
        public bool Up;
    }

    // ------------------------------------------------------------------------ results (serialised by Unity's JsonUtility)

    [Serializable]
    public class SunDayInfo
    {
        public string key, name;
        public int dayOfYear;
        public float declinationDeg, sunriseH, sunsetH, noonElevationDeg, noonBearingDeg;
        public float roofMeanSunHours, roofMeanSunHoursAfter;
    }

    [Serializable]
    public class ZoneSun
    {
        public string id, label, kind;          // kind: people | spectators | court | garden
        public float areaM2;
        public float sunHoursJune, sunHoursMarch, sunHoursDecember;
        public float peakShadePercent;          // mean shade in the heat window on 21 June
        public string status;                   // ok | too-sunny | too-shaded | n/a
        public float afterSunHoursJune, afterPeakShadePercent;
        public string afterStatus;
    }

    [Serializable]
    public class EquipmentPiece
    {
        public string key, name, shape;         // shape: rect | disc | tree
        public float x, y, widthM, depthM;      // footprint's top-left corner and size (a disc: its bounding square)
        public float heightM, transmission, weightKnM2, windCp;
        public string zoneId, zoneLabel;
        public float shadeBeforePercent, shadeAfterPercent;
        public float addedLoadKn, windUpliftKn;
        public string note;
    }

    [Serializable]
    public class SunRecommendation
    {
        public string kind, target, text;       // too-sunny | too-shaded | equipment | no-option | structure | input | fine
    }

    [Serializable]
    public class SunStructureEffect
    {
        public bool ran;
        public float addedKn, peakUtilisationBefore, peakUtilisationAfter;
        public int baysOverBefore, baysOverAfter;
        public string worstBayAfter;
    }

    [Serializable]
    public class SunSummary
    {
        public float latitudeDeg, northDeg;
        public bool latitudeAssumed, northAssumed;
        public float shadeTargetPercent, gardenMinSunHours;
        public int peopleZones, peopleZonesTooSunny, gardenZones, gardenZonesTooShaded;
        public int peopleZonesTooSunnyAfter, gardenZonesTooShadedAfter;
        public int pieces;
        public float addedLoadKn, peakWindPressurePa;
        public bool preliminary;
        public string preliminaryNote = "";
        public string acceptedNote = "";
    }

    [Serializable]
    public class SunReport
    {
        public bool ran;
        public List<string> assumptions = new List<string>();
        public List<AssumptionUse> assumptionUses = new List<AssumptionUse>();
        public SunSummary summary = new SunSummary();
        public List<SunDayInfo> days = new List<SunDayInfo>();
        public List<ZoneSun> zones = new List<ZoneSun>();
        public List<EquipmentPiece> equipment = new List<EquipmentPiece>();
        public SunStructureEffect structure = new SunStructureEffect();
        public List<SunRecommendation> recommendations = new List<SunRecommendation>();
    }

    // ------------------------------------------------------------------------ the catalogue

    public sealed class EquipmentType
    {
        public string Key, Name, Shape, Group;     // Shape: rect | disc | tree;  Group: fixed | light | tree
        public double[][] Sizes;                   // {width, depth} (a disc: {diameter, diameter})
        public double HeightM, Transmission, WeightKnM2, WindCp;
        public string Note;
    }

    /// <summary>One shadow on the roof: a convex polygon or an ellipse, with the share of the sun it lets through.</summary>
    public sealed class Shape
    {
        public double MinX, MaxX, MinY, MaxY, Tau;
        public bool Ellipse;
        public double[] Px, Py;                    // polygon corners, counter-clockwise
        public double Cx, Cy, Sx, Sy, A, B;        // ellipse: centre, unit vector of the major axis, semi-axes

        public bool Contains(double x, double y)
        {
            if (x < MinX || x > MaxX || y < MinY || y > MaxY) return false;
            if (Ellipse)
            {
                double dx = x - Cx, dy = y - Cy;
                var along = dx * Sx + dy * Sy;
                var across = -dx * Sy + dy * Sx;
                return (along * along) / (A * A) + (across * across) / (B * B) <= 1.0;
            }
            var n = Px.Length;
            for (var i = 0; i < n; i++)
            {
                var j = (i + 1) % n;
                if ((Px[j] - Px[i]) * (y - Py[i]) - (Py[j] - Py[i]) * (x - Px[i]) < 0) return false;
            }
            return true;
        }
    }

    /// <summary>The roof cut into cells, and which cells count.</summary>
    public sealed class SunGrid
    {
        public int Nx, Ny;
        public double Cw, Ch, Length, Width;
        public bool[] Active;
        public int Count { get { return Nx * Ny; } }
        public double CentreX(int i) { return (i + 0.5) * Cw; }
        public double CentreY(int j) { return (j + 0.5) * Ch; }
        public int Index(int i, int j) { return j * Nx + i; }
    }

    /// <summary>Everything that shades the roof: the walls, the trees, and the equipment placed.</summary>
    public sealed class SunScene
    {
        public double NorthDeg;
        public List<ObstacleInput> Obstacles = new List<ObstacleInput>();
        public List<PlantInput> Trees = new List<PlantInput>();
        public List<EquipmentPiece> Equipment = new List<EquipmentPiece>();

        /// <summary>The shadows the scene throws when the sun is at <paramref name="sun"/>, on a day whose trees let <paramref name="treeTau"/> of the sun through.</summary>
        public void Shapes(SunPosition sun, double treeTau, List<Shape> into)
        {
            into.Clear();
            if (!sun.Up) return;
            double tx, ty;                                   // toward the sun, plan
            SunModel.TowardSun(sun, NorthDeg, out tx, out ty);
            var k = 1.0 / Math.Tan(sun.ElevationDeg * SunModel.Deg);

            foreach (var o in Obstacles) SunModel.WallShadow(o, tx, ty, k, into);
            foreach (var t in Trees)
            {
                if (t.HeightM < SunModel.MinShadingPlantM) continue;
                var r = Math.Max(0.25, t.CrownM * 0.5);
                var zc = Math.Max(r, t.HeightM - r);
                into.Add(SunModel.EllipseShadow(t.X, t.Y, zc, r, tx, ty, k, sun.ElevationDeg, treeTau));
            }
            foreach (var e in Equipment) SunModel.EquipmentShadow(e, tx, ty, k, sun.ElevationDeg, treeTau, into);
        }

        /// <summary>The share of the sun that reaches the point: the product of tau over every shadow that covers it.</summary>
        public static double Fraction(List<Shape> shapes, double x, double y)
        {
            var f = 1.0;
            for (var i = 0; i < shapes.Count; i++)
                if (shapes[i].Contains(x, y)) f *= shapes[i].Tau;
            return f;
        }
    }

    // ------------------------------------------------------------------------ the model

    public static class SunModel
    {
        // --- constants that are judgement calls (all repeated in the report) ---
        public const double Deg = Math.PI / 180.0;
        public const double CellM = 0.5;
        public const double StepH = 0.5;                       // solar-time step of the day
        public const double MinElevationDeg = 2.0;
        public const double DefaultLatitudeDeg = 51.0;
        public const double DefaultNorthDeg = 0.0;
        public const double DefaultShadeTargetPercent = 50.0;
        public const double DefaultGardenMinSunHours = 4.0;
        public const double WindowFromH = 11.0, WindowToH = 16.0;
        public const double SpectatorMarginM = 2.0;
        public const double MinShadingPlantM = 2.0;
        public const double TreeTauSummer = 0.25, TreeTauEquinox = 0.5, TreeTauWinter = 0.7;
        public const int MaxPieces = 6, MaxPerZone = 3;
        public const double MinGain = 0.03;                    // a piece must add this share of the zone's shade
        public const double GardenTolerance = 0.25;            // hours a garden already short of its need may lose
        public const double PostClearanceM = 0.3;
        public const double EntryClearM = 2.0;
        public const double EdgeMarginM = 0.3;
        public const double PositionStepM = 1.0;
        public const double SearchReachM = 4.0;

        public static readonly string[] DayKeys = { "jun", "mar", "dec" };
        public static readonly int[] DayOfYear = { 172, 80, 355 };
        public static readonly string[] DayNames = { "21 June", "21 March", "21 December" };

        public static readonly EquipmentType[] Catalogue =
        {
            new EquipmentType { Key = "pergola", Name = "Louvre pergola", Shape = "rect", Group = "fixed", Sizes = new[] { new[] { 4.0, 3.0 }, new[] { 6.0, 4.0 } }, HeightM = 2.6, Transmission = 0.15, WeightKnM2 = 0.35, WindCp = 1.2, Note = "louvres closed at midday" },
            new EquipmentType { Key = "canopy", Name = "Solid canopy", Shape = "rect", Group = "fixed", Sizes = new[] { new[] { 4.0, 3.0 }, new[] { 6.0, 3.0 } }, HeightM = 3.0, Transmission = 0.0, WeightKnM2 = 0.5, WindCp = 1.2, Note = "opaque roof on posts" },
            new EquipmentType { Key = "sail", Name = "Shade sail", Shape = "rect", Group = "light", Sizes = new[] { new[] { 5.0, 4.0 }, new[] { 6.0, 5.0 } }, HeightM = 3.5, Transmission = 0.10, WeightKnM2 = 0.03, WindCp = 1.5, Note = "tensioned fabric, taken down in storms" },
            new EquipmentType { Key = "parasol", Name = "Parasol", Shape = "disc", Group = "light", Sizes = new[] { new[] { 3.5, 3.5 }, new[] { 4.5, 4.5 } }, HeightM = 2.8, Transmission = 0.10, WeightKnM2 = 0.05, WindCp = 1.3, Note = "one pole, folded when windy" },
            new EquipmentType { Key = "tree", Name = "Tree in a deep bed", Shape = "tree", Group = "tree", Sizes = new[] { new[] { 3.0, 3.0 } }, HeightM = 6.0, Transmission = TreeTauSummer, WeightKnM2 = 13.5, WindCp = 0.0, Note = "crown 5 m, bed 3 x 3 m; takes years to grow" },
        };
        public const double TreeCrownM = 5.0;

        // ---------------------------------------------------------------- the sun

        public static SunPosition SunAt(double latitudeDeg, int dayOfYear, double solarTimeH)
        {
            var b = 2.0 * Math.PI * (dayOfYear - 1) / 365.0;
            var decl = 0.006918 - 0.399912 * Math.Cos(b) + 0.070257 * Math.Sin(b) - 0.006758 * Math.Cos(2 * b) + 0.000907 * Math.Sin(2 * b)
                       - 0.002697 * Math.Cos(3 * b) + 0.00148 * Math.Sin(3 * b);
            var phi = latitudeDeg * Deg;
            var w = 15.0 * (solarTimeH - 12.0) * Deg;
            var sinAlt = Math.Sin(phi) * Math.Sin(decl) + Math.Cos(phi) * Math.Cos(decl) * Math.Cos(w);
            sinAlt = Math.Max(-1.0, Math.Min(1.0, sinAlt));
            var alt = Math.Asin(sinAlt);
            var east = -Math.Cos(decl) * Math.Sin(w);
            var north = Math.Sin(decl) * Math.Cos(phi) - Math.Cos(decl) * Math.Sin(phi) * Math.Cos(w);
            var bearing = Math.Atan2(east, north) / Deg;
            if (bearing < 0) bearing += 360.0;
            return new SunPosition { ElevationDeg = alt / Deg, BearingDeg = bearing, Up = alt / Deg >= MinElevationDeg };
        }

        public static double DeclinationDeg(int dayOfYear)
        {
            var b = 2.0 * Math.PI * (dayOfYear - 1) / 365.0;
            return (0.006918 - 0.399912 * Math.Cos(b) + 0.070257 * Math.Sin(b) - 0.006758 * Math.Cos(2 * b) + 0.000907 * Math.Sin(2 * b)
                    - 0.002697 * Math.Cos(3 * b) + 0.00148 * Math.Sin(3 * b)) / Deg;
        }

        /// <summary>The horizontal direction toward the sun in the plan (x right, y down), for a plan whose top faces the bearing northDeg.</summary>
        public static void TowardSun(SunPosition sun, double northDeg, out double x, out double y)
        {
            var t = (sun.BearingDeg - northDeg) * Deg;
            x = Math.Sin(t);
            y = -Math.Cos(t);
        }

        public static double TreeTau(string dayKey)
        {
            return dayKey == "jun" ? TreeTauSummer : dayKey == "mar" ? TreeTauEquinox : TreeTauWinter;
        }

        /// <summary>The midpoints of the day's half-hours at which the sun is up.</summary>
        public static List<double> DayTimes(double latitudeDeg, int dayOfYear)
        {
            var times = new List<double>();
            for (var k = 0; k < 48; k++)
            {
                var t = 0.25 + StepH * k;
                if (SunAt(latitudeDeg, dayOfYear, t).Up) times.Add(t);
            }
            return times;
        }

        public static bool InWindow(double t) { return t >= WindowFromH && t <= WindowToH; }

        // ---------------------------------------------------------------- shadow shapes

        static Shape Hull(List<double[]> pts, double tau)
        {
            // Andrew's monotone chain, counter-clockwise
            pts.Sort((a, b) => a[0] != b[0] ? a[0].CompareTo(b[0]) : a[1].CompareTo(b[1]));
            var hull = new List<double[]>();
            for (var pass = 0; pass < 2; pass++)
            {
                var start = hull.Count;
                for (var i = 0; i < pts.Count; i++)
                {
                    var p = pts[pass == 0 ? i : pts.Count - 1 - i];
                    while (hull.Count >= start + 2 && Cross(hull[hull.Count - 2], hull[hull.Count - 1], p) <= 1e-12) hull.RemoveAt(hull.Count - 1);
                    hull.Add(p);
                }
                hull.RemoveAt(hull.Count - 1);
            }
            var s = new Shape { Tau = tau, Px = hull.Select(h => h[0]).ToArray(), Py = hull.Select(h => h[1]).ToArray() };
            s.MinX = s.Px.Min(); s.MaxX = s.Px.Max(); s.MinY = s.Py.Min(); s.MaxY = s.Py.Max();
            return s;
        }

        static double Cross(double[] o, double[] a, double[] b)
        {
            return (a[0] - o[0]) * (b[1] - o[1]) - (a[1] - o[1]) * (b[0] - o[0]);
        }

        public static void WallShadow(ObstacleInput o, double tx, double ty, double k, List<Shape> into)
        {
            double ex = o.X1 - o.X0, ey = o.Y1 - o.Y0;
            var len = Math.Sqrt(ex * ex + ey * ey);
            if (len < 1e-6 || o.HeightM <= 0) return;
            ex /= len; ey /= len;
            var half = Math.Max(o.ThicknessM, 0.05) * 0.5;
            double nx = -ey * half, ny = ex * half;
            var dx = -k * o.HeightM * tx;
            var dy = -k * o.HeightM * ty;
            var pts = new List<double[]>();
            foreach (var c in new[] { new[] { o.X0 + nx, o.Y0 + ny }, new[] { o.X0 - nx, o.Y0 - ny }, new[] { o.X1 - nx, o.Y1 - ny }, new[] { o.X1 + nx, o.Y1 + ny } })
            {
                pts.Add(new[] { c[0], c[1] });
                pts.Add(new[] { c[0] + dx, c[1] + dy });
            }
            var hull = Hull(pts, 0.0);
            if (hull.Px.Length >= 3) into.Add(hull);
        }

        public static Shape EllipseShadow(double x, double y, double centreHeight, double radius, double tx, double ty, double k, double elevationDeg, double tau)
        {
            var sinAlt = Math.Sin(elevationDeg * Deg);
            double sx = -tx, sy = -ty;                     // the shadow runs away from the sun
            var a = radius / sinAlt;
            var cx = x + sx * k * centreHeight;
            var cy = y + sy * k * centreHeight;
            var halfW = Math.Sqrt(a * a * sx * sx + radius * radius * sy * sy);
            var halfH = Math.Sqrt(a * a * sy * sy + radius * radius * sx * sx);
            return new Shape { Ellipse = true, Cx = cx, Cy = cy, Sx = sx, Sy = sy, A = a, B = radius, Tau = tau, MinX = cx - halfW, MaxX = cx + halfW, MinY = cy - halfH, MaxY = cy + halfH };
        }

        public static void EquipmentShadow(EquipmentPiece e, double tx, double ty, double k, double elevationDeg, double treeTau, List<Shape> into)
        {
            double dx = -k * e.heightM * tx, dy = -k * e.heightM * ty;
            if (e.shape == "tree")
            {
                var r = TreeCrownM * 0.5;
                var zc = Math.Max(r, e.heightM - r);
                into.Add(EllipseShadow(e.x + e.widthM * 0.5, e.y + e.depthM * 0.5, zc, r, tx, ty, k, elevationDeg, treeTau));
                return;
            }
            if (e.shape == "disc")
            {
                var r = e.widthM * 0.5;
                var cx = e.x + r + dx; var cy = e.y + e.depthM * 0.5 + dy;
                into.Add(new Shape { Ellipse = true, Cx = cx, Cy = cy, Sx = 1, Sy = 0, A = r, B = r, Tau = e.transmission, MinX = cx - r, MaxX = cx + r, MinY = cy - r, MaxY = cy + r });
                return;
            }
            double x0 = e.x + dx, y0 = e.y + dy, x1 = e.x + e.widthM + dx, y1 = e.y + e.depthM + dy;
            into.Add(new Shape { Tau = e.transmission, Px = new[] { x0, x1, x1, x0 }, Py = new[] { y0, y0, y1, y1 }, MinX = x0, MaxX = x1, MinY = y0, MaxY = y1 });
        }

        // ---------------------------------------------------------------- the roof

        public static bool PointInPolygon(List<double[]> poly, double x, double y)
        {
            var inside = false;
            for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
            {
                double xi = poly[i][0], yi = poly[i][1], xj = poly[j][0], yj = poly[j][1];
                if ((yi > y) != (yj > y) && x < (xj - xi) * (y - yi) / (yj - yi) + xi) inside = !inside;
            }
            return inside;
        }

        public static SunGrid BuildGrid(SunInputs inputs)
        {
            var st = inputs.Structure;
            var g = new SunGrid { Length = st.RoofLength, Width = st.RoofWidth };
            g.Nx = Math.Max(1, (int)Math.Ceiling(g.Length / CellM - 1e-9));
            g.Ny = Math.Max(1, (int)Math.Ceiling(g.Width / CellM - 1e-9));
            g.Cw = g.Length / g.Nx;
            g.Ch = g.Width / g.Ny;
            g.Active = new bool[g.Count];
            var poly = inputs.Outline != null && inputs.Outline.Count >= 3 ? inputs.Outline : null;
            for (var j = 0; j < g.Ny; j++)
                for (var i = 0; i < g.Nx; i++)
                    g.Active[g.Index(i, j)] = poly == null || PointInPolygon(poly, g.CentreX(i), g.CentreY(j));
            return g;
        }

        public static SunScene BuildScene(SunInputs inputs, IList<EquipmentPiece> equipment)
        {
            var scene = new SunScene { NorthDeg = inputs.NorthDeg ?? DefaultNorthDeg };
            scene.Obstacles.AddRange(inputs.Obstacles);
            if (inputs.Wind != null)
                foreach (var p in inputs.Wind.Plants) scene.Trees.Add(p);
            if (equipment != null) scene.Equipment.AddRange(equipment);
            return scene;
        }

        /// <summary>One cell's zone membership.</summary>
        sealed class Zone
        {
            public string Id, Label, Kind;
            public List<int> Cells = new List<int>();
            public double X, Y, W, H;                    // the piece's rectangle
            public int PiecesAdded;
        }

        sealed class Sample
        {
            public double T;
            public SunPosition Sun;
            public double[] F;                           // sunlit fraction of every cell
            public bool Window;
        }

        sealed class DayData
        {
            public string Key;
            public int Doy;
            public double TreeTau;
            public List<Sample> Samples = new List<Sample>();
        }

        static List<DayData> BuildDays(SunInputs inputs, SunGrid grid, SunScene scene, double lat)
        {
            var days = new List<DayData>();
            var shapes = new List<Shape>();
            for (var d = 0; d < DayKeys.Length; d++)
            {
                var day = new DayData { Key = DayKeys[d], Doy = DayOfYear[d], TreeTau = TreeTau(DayKeys[d]) };
                foreach (var t in DayTimes(lat, day.Doy))
                {
                    var sun = SunAt(lat, day.Doy, t);
                    var s = new Sample { T = t, Sun = sun, Window = day.Key == "jun" && InWindow(t), F = new double[grid.Count] };
                    scene.Shapes(sun, day.TreeTau, shapes);
                    for (var j = 0; j < grid.Ny; j++)
                        for (var i = 0; i < grid.Nx; i++)
                        {
                            var c = grid.Index(i, j);
                            if (grid.Active[c]) s.F[c] = SunScene.Fraction(shapes, grid.CentreX(i), grid.CentreY(j));
                        }
                    day.Samples.Add(s);
                }
                days.Add(day);
            }
            return days;
        }

        // ---------------------------------------------------------------- the zones

        static bool InRect(double x, double y, double rx, double ry, double rw, double rh)
        {
            return x >= rx && x <= rx + rw && y >= ry && y <= ry + rh;
        }

        static List<Zone> BuildZones(SunInputs inputs, SunGrid grid)
        {
            var zones = new List<Zone>();
            var courts = inputs.Structure.Items.Where(i => i.Kind == LoadKind.Court).ToList();

            foreach (var it in inputs.Structure.Items)
            {
                if (it.IsObject) continue;
                var kind = it.Kind == LoadKind.Court ? "court" : it.Kind == LoadKind.Activity ? "people" : "garden";
                var z = new Zone { Id = it.Id, Label = string.IsNullOrEmpty(it.Name) ? it.Label : it.Name, Kind = kind, X = it.X, Y = it.Y, W = it.Width, H = it.Height };
                for (var j = 0; j < grid.Ny; j++)
                    for (var i = 0; i < grid.Nx; i++)
                        if (grid.Active[grid.Index(i, j)] && (it.Polygon != null && it.Polygon.Count >= 3 ? PointInPolygon(it.Polygon, grid.CentreX(i), grid.CentreY(j)) : InRect(grid.CentreX(i), grid.CentreY(j), it.X, it.Y, it.Width, it.Height))) z.Cells.Add(grid.Index(i, j));
                zones.Add(z);

                if (it.Kind == LoadKind.Court && it.Seats > 0)
                {
                    var band = new Zone { Id = it.Id + "_seats", Label = z.Label + " spectators", Kind = "spectators", X = it.X - SpectatorMarginM, Y = it.Y - SpectatorMarginM, W = it.Width + 2 * SpectatorMarginM, H = it.Height + 2 * SpectatorMarginM };
                    for (var j = 0; j < grid.Ny; j++)
                        for (var i = 0; i < grid.Nx; i++)
                        {
                            if (!grid.Active[grid.Index(i, j)]) continue;
                            double cx = grid.CentreX(i), cy = grid.CentreY(j);
                            if (!InRect(cx, cy, band.X, band.Y, band.W, band.H)) continue;
                            if (courts.Any(c => InRect(cx, cy, c.X, c.Y, c.Width, c.Height))) continue;
                            band.Cells.Add(grid.Index(i, j));
                        }
                    zones.Add(band);
                }
            }
            return zones;
        }

        // ---------------------------------------------------------------- measures

        static double Mean(List<int> cells, double[] values)
        {
            if (cells.Count == 0) return 0;
            double s = 0;
            for (var i = 0; i < cells.Count; i++) s += values[cells[i]];
            return s / cells.Count;
        }

        /// <summary>Sun hours of every cell on a day, and the mean shade of every cell over the heat window of the summer day.</summary>
        static void Measures(List<DayData> days, int cellCount, out double[][] sunHours, out double[] peakShade)
        {
            sunHours = new double[days.Count][];
            peakShade = new double[cellCount];
            for (var d = 0; d < days.Count; d++)
            {
                sunHours[d] = new double[cellCount];
                var window = 0;
                foreach (var s in days[d].Samples)
                {
                    for (var c = 0; c < cellCount; c++) sunHours[d][c] += s.F[c] * StepH;
                    if (s.Window)
                    {
                        window++;
                        for (var c = 0; c < cellCount; c++) peakShade[c] += 1.0 - s.F[c];
                    }
                }
                if (d == 0 && window > 0) for (var c = 0; c < cellCount; c++) peakShade[c] /= window;
            }
        }

        static string PeopleStatus(Zone z, double shadePercent, double target)
        {
            if (z.Cells.Count == 0) return "n/a";
            return shadePercent + 1e-9 >= target ? "ok" : "too-sunny";
        }

        static string GardenStatus(Zone z, double hours, double min)
        {
            if (z.Cells.Count == 0) return "n/a";
            return hours + 1e-9 >= min ? "ok" : "too-shaded";
        }

        // ---------------------------------------------------------------- the analysis

        public static SunReport Analyse(SunInputs inputs)
        {
            var report = new SunReport { ran = true };
            var st = inputs.Structure;
            var lat = inputs.LatitudeDeg ?? DefaultLatitudeDeg;
            var target = inputs.ShadeTargetPercent ?? DefaultShadeTargetPercent;
            var minSun = inputs.GardenMinSunHours ?? DefaultGardenMinSunHours;
            var allowed = inputs.Equipment == "light" || inputs.Equipment == "fixed" ? inputs.Equipment : "all";

            var grid = BuildGrid(inputs);
            var scene = BuildScene(inputs, null);
            var zones = BuildZones(inputs, grid);
            if (zones.Count > 60) zones = zones.Take(60).ToList();
            var days = BuildDays(inputs, grid, scene, lat);

            double[][] sun0; double[] shade0;
            Measures(days, grid.Count, out sun0, out shade0);

            // ---- the pieces, worst zone first
            var placed = new List<EquipmentPiece>();
            var noOption = new List<Zone>();
            var wind = WindPressure(inputs);
            var structure = new SunStructureEffect();
            if (lat > -66 && lat < 66) Recommend(inputs, grid, scene, zones, days, target, minSun, allowed, placed, noOption, wind, report);

            double[][] sun1; double[] shade1;
            Measures(days, grid.Count, out sun1, out shade1);      // the days now carry the pieces' shade

            // ---- the numbers
            for (var d = 0; d < days.Count; d++)
            {
                var sunUp = days[d].Samples;
                var decl = DeclinationDeg(days[d].Doy);
                var noon = SunAt(lat, days[d].Doy, 12.0);
                var active = Enumerable.Range(0, grid.Count).Where(c => grid.Active[c]).ToList();
                report.days.Add(new SunDayInfo
                {
                    key = DayKeys[d], name = DayNames[d], dayOfYear = days[d].Doy, declinationDeg = (float)decl,
                    sunriseH = sunUp.Count > 0 ? (float)(sunUp[0].T - StepH * 0.5) : 0f, sunsetH = sunUp.Count > 0 ? (float)(sunUp[sunUp.Count - 1].T + StepH * 0.5) : 0f,
                    noonElevationDeg = (float)noon.ElevationDeg, noonBearingDeg = (float)noon.BearingDeg,
                    roofMeanSunHours = (float)Mean(active, sun0[d]), roofMeanSunHoursAfter = (float)Mean(active, sun1[d]),
                });
            }

            foreach (var z in zones)
            {
                var zs = new ZoneSun
                {
                    id = z.Id, label = z.Label, kind = z.Kind, areaM2 = (float)(z.Cells.Count * grid.Cw * grid.Ch),
                    sunHoursJune = (float)Mean(z.Cells, sun0[0]), sunHoursMarch = (float)Mean(z.Cells, sun0[1]), sunHoursDecember = (float)Mean(z.Cells, sun0[2]),
                    peakShadePercent = (float)(100.0 * Mean(z.Cells, shade0)),
                    afterSunHoursJune = (float)Mean(z.Cells, sun1[0]), afterPeakShadePercent = (float)(100.0 * Mean(z.Cells, shade1)),
                };
                if (z.Kind == "people" || z.Kind == "spectators")
                {
                    zs.status = PeopleStatus(z, zs.peakShadePercent, target);
                    zs.afterStatus = PeopleStatus(z, zs.afterPeakShadePercent, target);
                }
                else if (z.Kind == "garden")
                {
                    zs.status = GardenStatus(z, zs.sunHoursJune, minSun);
                    zs.afterStatus = GardenStatus(z, zs.afterSunHoursJune, minSun);
                }
                else { zs.status = "n/a"; zs.afterStatus = "n/a"; }
                report.zones.Add(zs);
            }

            // pieces: shade before and after in their zones
            foreach (var p in placed)
            {
                var zs = report.zones.FirstOrDefault(z => z.id == p.zoneId);
                if (zs != null) { p.shadeBeforePercent = zs.peakShadePercent; p.shadeAfterPercent = zs.afterPeakShadePercent; }
            }
            report.equipment.AddRange(placed);

            // ---- the deck with the pieces on it
            StructureEffect(inputs, placed, structure);
            report.structure = structure;

            report.assumptionUses = DescribeInputs(inputs, target, minSun, allowed);
            report.assumptions = Assumptions(inputs, lat, target, minSun, allowed, wind);
            report.assumptions.AddRange(AnalysisAssumptions.Lines(report.assumptionUses));
            report.assumptions.AddRange(st.Notes);

            var people = report.zones.Where(z => z.kind == "people" || z.kind == "spectators").ToList();
            var gardens = report.zones.Where(z => z.kind == "garden").ToList();
            report.summary = new SunSummary
            {
                latitudeDeg = (float)lat, northDeg = (float)(inputs.NorthDeg ?? DefaultNorthDeg),
                latitudeAssumed = !inputs.LatitudeDeg.HasValue, northAssumed = !inputs.NorthDeg.HasValue,
                shadeTargetPercent = (float)target, gardenMinSunHours = (float)minSun,
                peopleZones = people.Count, peopleZonesTooSunny = people.Count(z => z.status == "too-sunny"), peopleZonesTooSunnyAfter = people.Count(z => z.afterStatus == "too-sunny"),
                gardenZones = gardens.Count, gardenZonesTooShaded = gardens.Count(z => z.status == "too-shaded"), gardenZonesTooShadedAfter = gardens.Count(z => z.afterStatus == "too-shaded"),
                pieces = placed.Count, addedLoadKn = (float)placed.Sum(p => p.addedLoadKn), peakWindPressurePa = (float)wind,
                preliminary = AnalysisAssumptions.IsPreliminary(report.assumptionUses),
                preliminaryNote = AnalysisAssumptions.PreliminaryNote(report.assumptionUses),
                acceptedNote = AnalysisAssumptions.AcceptedNote(report.assumptionUses),
            };
            Advise(report, noOption, target, minSun);
            return report;
        }

        // ---------------------------------------------------------------- the search

        sealed class Candidate
        {
            public EquipmentType Type;
            public double W, D, X, Y;
            public double Gain;
            public int Order;
        }

        static double WindPressure(SunInputs inputs)
        {
            try
            {
                if (inputs.Wind == null) return 0;
                return WindModel.Analyse(inputs.Wind).site.peakPressureAtRoofPa;
            }
            catch (Exception) { return 0; }
        }

        static bool Allowed(EquipmentType t, string allowed)
        {
            return allowed == "all" || t.Group == allowed;
        }

        static EquipmentPiece MakePiece(EquipmentType t, double w, double d, double x, double y, Zone z, double windPa)
        {
            var area = t.Shape == "disc" ? Math.PI * w * w / 4.0 : w * d;
            var loadArea = t.Shape == "disc" ? w * w : w * d;
            return new EquipmentPiece
            {
                key = t.Key, name = t.Name, shape = t.Shape, x = (float)x, y = (float)y, widthM = (float)w, depthM = (float)d,
                heightM = (float)t.HeightM, transmission = (float)t.Transmission, weightKnM2 = (float)t.WeightKnM2, windCp = (float)t.WindCp,
                zoneId = z != null ? z.Id : "", zoneLabel = z != null ? z.Label : "",
                addedLoadKn = (float)(t.Shape == "tree" ? t.WeightKnM2 * loadArea : t.WeightKnM2 * area),
                windUpliftKn = (float)(t.WindCp * windPa * 1e-3 * area),
                note = t.Note,
            };
        }

        static void Recommend(SunInputs inputs, SunGrid grid, SunScene scene, List<Zone> zones, List<DayData> days, double target, double minSun, string allowed,
                              List<EquipmentPiece> placed, List<Zone> noOption, double windPa, SunReport report)
        {
            var people = zones.Where(z => (z.Kind == "people" || z.Kind == "spectators") && z.Cells.Count > 0).ToList();
            var gardens = zones.Where(z => z.Kind == "garden" && z.Cells.Count > 0).ToList();
            var junWindow = days[0].Samples.Where(s => s.Window).ToList();
            if (people.Count == 0 || junWindow.Count == 0) return;

            var types = Catalogue.Where(t => Allowed(t, allowed)).ToList();
            var junHoursNow = new double[gardens.Count];
            var attempts = 0;

            while (placed.Count < MaxPieces && attempts++ < 40)
            {
                // the zone furthest below its target
                double[][] sh; double[] peak;
                Measures(days, grid.Count, out sh, out peak);
                var candidates = people.Where(z => !noOption.Contains(z) && z.PiecesAdded < MaxPerZone)
                    .Select(z => new { Zone = z, Shade = 100.0 * Mean(z.Cells, peak) })
                    .Where(x => x.Shade + 1e-9 < target)
                    .OrderByDescending(x => Math.Round(target - x.Shade, 6)).ToList();
                if (candidates.Count == 0) break;
                var zone = candidates[0].Zone;
                var member = new bool[grid.Count];
                foreach (var c in zone.Cells) member[c] = true;

                for (var gi = 0; gi < gardens.Count; gi++) junHoursNow[gi] = Mean(gardens[gi].Cells, sh[0]);

                // every piece that fits, scored by what it adds to this zone's shade in the heat window
                var list = new List<Candidate>();
                var order = 0;
                foreach (var t in types)
                {
                    foreach (var size in t.Sizes)
                    {
                        var orientations = t.Shape == "rect" && Math.Abs(size[0] - size[1]) > 1e-9 ? 2 : 1;
                        for (var o = 0; o < orientations; o++)
                        {
                            double w = o == 0 ? size[0] : size[1], d = o == 0 ? size[1] : size[0];
                            var x0 = Math.Floor(zone.X - SearchReachM);
                            var x1 = Math.Ceiling(zone.X + zone.W + SearchReachM - w);
                            var y0 = Math.Floor(zone.Y - SearchReachM);
                            var y1 = Math.Ceiling(zone.Y + zone.H + SearchReachM - d);
                            for (var x = x0; x <= x1 + 1e-9; x += PositionStepM)
                                for (var y = y0; y <= y1 + 1e-9; y += PositionStepM)
                                {
                                    order++;
                                    if (!Fits(inputs, grid, t, w, d, x, y, placed)) continue;
                                    var piece = MakePiece(t, w, d, x, y, zone, windPa);
                                    var gain = Gain(grid, scene, junWindow, member, zone.Cells.Count, piece, days[0].TreeTau);
                                    if (gain >= MinGain) list.Add(new Candidate { Type = t, W = w, D = d, X = x, Y = y, Gain = Math.Round(gain, 9), Order = order });
                                }
                        }
                    }
                }

                // the best gain first; among the pieces nearly as good (within 10% of it) the lightest on the deck comes first
                var ranked = list.OrderByDescending(c => c.Gain).ThenBy(c => c.Order).Take(48).ToList();
                if (ranked.Count > 0)
                {
                    var top = ranked[0].Gain;
                    var nearBest = ranked.Where(c => c.Gain >= 0.9 * top - 1e-12)
                        .OrderBy(c => Math.Round(MakePiece(c.Type, c.W, c.D, c.X, c.Y, zone, windPa).addedLoadKn, 3)).ThenByDescending(c => c.Gain).ThenBy(c => c.Order).ToList();
                    ranked = nearBest.Concat(ranked.Where(c => !nearBest.Contains(c))).ToList();
                }
                Candidate chosen = null;
                foreach (var c in ranked)
                {
                    var piece = MakePiece(c.Type, c.W, c.D, c.X, c.Y, zone, windPa);
                    if (KeepsGardens(grid, scene, days[0], gardens, junHoursNow, minSun, piece)) { chosen = c; break; }
                }
                if (chosen == null) { noOption.Add(zone); continue; }

                var final = MakePiece(chosen.Type, chosen.W, chosen.D, chosen.X, chosen.Y, zone, windPa);
                placed.Add(final);
                zone.PiecesAdded++;
                scene.Equipment.Add(final);
                ApplyPiece(grid, scene, days, final);
            }
        }

        /// <summary>Does the piece fit on this roof: inside it, off the courts, drains, openings, entries and taller walls, its posts clear of the walkways, off the other pieces.</summary>
        static bool Fits(SunInputs inputs, SunGrid grid, EquipmentType t, double w, double d, double x, double y, List<EquipmentPiece> placed)
        {
            double x1 = x + w, y1 = y + d;
            var corners = new[] { new[] { x, y }, new[] { x1, y }, new[] { x1, y1 }, new[] { x, y1 } };

            // inside the roof, off its edge
            foreach (var c in corners)
            {
                if (c[0] < EdgeMarginM || c[0] > grid.Length - EdgeMarginM || c[1] < EdgeMarginM || c[1] > grid.Width - EdgeMarginM) return false;
                if (inputs.Outline != null && inputs.Outline.Count >= 3 && !PointInPolygon(inputs.Outline, c[0], c[1])) return false;
            }

            var st = inputs.Structure;
            foreach (var it in st.Items)
                if (it.Kind == LoadKind.Court && Overlap(x, y, x1, y1, it.X, it.Y, it.X + it.Width, it.Y + it.Height)) return false;
            foreach (var p in placed)
                if (Overlap(x, y, x1, y1, p.x, p.y, p.x + p.widthM, p.y + p.depthM)) return false;
            foreach (var dr in inputs.Drains)
                if (dr[0] >= x - 0.3 && dr[0] <= x1 + 0.3 && dr[1] >= y - 0.3 && dr[1] <= y1 + 0.3) return false;
            foreach (var e in st.Entries)
                if (e[0] >= x - EntryClearM && e[0] <= x1 + EntryClearM && e[1] >= y - EntryClearM && e[1] <= y1 + EntryClearM) return false;
            foreach (var op in inputs.Openings)
            {
                double ox0 = op.Min(q => q[0]), ox1 = op.Max(q => q[0]), oy0 = op.Min(q => q[1]), oy1 = op.Max(q => q[1]);
                if (Overlap(x, y, x1, y1, ox0 - 0.3, oy0 - 0.3, ox1 + 0.3, oy1 + 0.3)) return false;
            }
            foreach (var o in inputs.Obstacles)
            {
                if (o.HeightM < t.HeightM - 0.3) continue;              // a low wall is passed over
                if (SegmentHitsRect(o.X0, o.Y0, o.X1, o.Y1, x - 0.2, y - 0.2, x1 + 0.2, y1 + 0.2)) return false;
            }

            // the posts: the corners of a rectangle, the middle of a disc or a tree
            var posts = t.Shape == "rect" ? corners.Select(c => new[] { c[0], c[1] }).ToList() : new List<double[]> { new[] { x + w * 0.5, y + d * 0.5 } };
            foreach (var path in st.Paths)
                foreach (var pt in posts)
                    if (DistanceToPolyline(path.Points, pt[0], pt[1]) < path.WidthM * 0.5 + PostClearanceM) return false;
            return true;
        }

        static bool Overlap(double ax0, double ay0, double ax1, double ay1, double bx0, double by0, double bx1, double by1)
        {
            return ax0 < bx1 && ax1 > bx0 && ay0 < by1 && ay1 > by0;
        }

        static bool SegmentHitsRect(double sx0, double sy0, double sx1, double sy1, double rx0, double ry0, double rx1, double ry1)
        {
            double t0 = 0, t1 = 1, dx = sx1 - sx0, dy = sy1 - sy0;
            double[] p = { -dx, dx, -dy, dy };
            double[] q = { sx0 - rx0, rx1 - sx0, sy0 - ry0, ry1 - sy0 };
            for (var i = 0; i < 4; i++)
            {
                if (Math.Abs(p[i]) < 1e-12) { if (q[i] < 0) return false; continue; }
                var r = q[i] / p[i];
                if (p[i] < 0) { if (r > t1) return false; if (r > t0) t0 = r; }
                else { if (r < t0) return false; if (r < t1) t1 = r; }
            }
            return true;
        }

        static double DistanceToPolyline(List<double[]> pts, double x, double y)
        {
            var best = double.MaxValue;
            for (var i = 0; i + 1 < pts.Count; i++)
            {
                double ax = pts[i][0], ay = pts[i][1], bx = pts[i + 1][0], by = pts[i + 1][1];
                double vx = bx - ax, vy = by - ay;
                var len2 = vx * vx + vy * vy;
                var u = len2 < 1e-12 ? 0 : Math.Max(0, Math.Min(1, ((x - ax) * vx + (y - ay) * vy) / len2));
                double px = ax + u * vx, py = ay + u * vy;
                best = Math.Min(best, Math.Sqrt((x - px) * (x - px) + (y - py) * (y - py)));
            }
            return best;
        }

        /// <summary>What the piece adds to the zone's mean shade over the heat window, as a share (0 to 1).</summary>
        static double Gain(SunGrid grid, SunScene scene, List<Sample> window, bool[] member, int zoneCells, EquipmentPiece piece, double treeTau)
        {
            double sum = 0;
            var shapes = new List<Shape>(1);
            foreach (var s in window)
            {
                double tx, ty;
                TowardSun(s.Sun, scene.NorthDeg, out tx, out ty);
                shapes.Clear();
                EquipmentShadow(piece, tx, ty, 1.0 / Math.Tan(s.Sun.ElevationDeg * Deg), s.Sun.ElevationDeg, treeTau, shapes);
                var sh = shapes[0];
                var tau = sh.Tau;
                ForCells(grid, sh, c => { if (member[c]) sum += s.F[c] * (1.0 - tau); });
            }
            return sum / (zoneCells * (double)window.Count);
        }

        static void ForCells(SunGrid grid, Shape sh, Action<int> act)
        {
            var i0 = Math.Max(0, (int)Math.Floor(sh.MinX / grid.Cw - 0.5));
            var i1 = Math.Min(grid.Nx - 1, (int)Math.Ceiling(sh.MaxX / grid.Cw - 0.5));
            var j0 = Math.Max(0, (int)Math.Floor(sh.MinY / grid.Ch - 0.5));
            var j1 = Math.Min(grid.Ny - 1, (int)Math.Ceiling(sh.MaxY / grid.Ch - 0.5));
            for (var j = j0; j <= j1; j++)
                for (var i = i0; i <= i1; i++)
                {
                    var c = grid.Index(i, j);
                    if (grid.Active[c] && sh.Contains(grid.CentreX(i), grid.CentreY(j))) act(c);
                }
        }

        /// <summary>Would the piece leave every garden with its sun: at or above its need, or, if it was already short, not lower by more than the tolerance?</summary>
        static bool KeepsGardens(SunGrid grid, SunScene scene, DayData jun, List<Zone> gardens, double[] hoursNow, double minSun, EquipmentPiece piece)
        {
            if (gardens.Count == 0) return true;
            var loss = new double[gardens.Count];
            var of = new Dictionary<int, List<int>>();
            for (var gi = 0; gi < gardens.Count; gi++)
                foreach (var c in gardens[gi].Cells) { List<int> l; if (!of.TryGetValue(c, out l)) of[c] = l = new List<int>(); l.Add(gi); }

            var shapes = new List<Shape>(1);
            foreach (var s in jun.Samples)
            {
                double tx, ty;
                TowardSun(s.Sun, scene.NorthDeg, out tx, out ty);
                shapes.Clear();
                EquipmentShadow(piece, tx, ty, 1.0 / Math.Tan(s.Sun.ElevationDeg * Deg), s.Sun.ElevationDeg, jun.TreeTau, shapes);
                var sh = shapes[0];
                ForCells(grid, sh, c =>
                {
                    List<int> gs;
                    if (of.TryGetValue(c, out gs)) foreach (var gi in gs) loss[gi] += s.F[c] * (1.0 - sh.Tau) * StepH;
                });
            }
            for (var gi = 0; gi < gardens.Count; gi++)
            {
                var after = hoursNow[gi] - loss[gi] / gardens[gi].Cells.Count;
                var ok = hoursNow[gi] + 1e-9 >= minSun ? after + 1e-9 >= minSun : after + 1e-9 >= hoursNow[gi] - GardenTolerance;
                if (!ok) return false;
            }
            return true;
        }

        /// <summary>Puts the piece's shade into every sample of every day.</summary>
        static void ApplyPiece(SunGrid grid, SunScene scene, List<DayData> days, EquipmentPiece piece)
        {
            var shapes = new List<Shape>(1);
            foreach (var day in days)
                foreach (var s in day.Samples)
                {
                    double tx, ty;
                    TowardSun(s.Sun, scene.NorthDeg, out tx, out ty);
                    shapes.Clear();
                    EquipmentShadow(piece, tx, ty, 1.0 / Math.Tan(s.Sun.ElevationDeg * Deg), s.Sun.ElevationDeg, day.TreeTau, shapes);
                    var sh = shapes[0];
                    ForCells(grid, sh, c => s.F[c] *= sh.Tau);
                }
        }

        // ---------------------------------------------------------------- the deck with the pieces on it

        static void StructureEffect(SunInputs inputs, List<EquipmentPiece> placed, SunStructureEffect effect)
        {
            try
            {
                var before = StructureModel.Compute(inputs.Structure);
                effect.ran = true;
                effect.peakUtilisationBefore = before.summary.peakUtilisation;
                effect.baysOverBefore = before.summary.baysOver;
                if (placed.Count == 0)
                {
                    effect.peakUtilisationAfter = before.summary.peakUtilisation;
                    effect.baysOverAfter = before.summary.baysOver;
                    effect.worstBayAfter = before.summary.worstBay;
                    return;
                }
                var withPieces = inputs.Structure.CopyWithItems();
                foreach (var p in placed)
                {
                    var loaded = p.shape == "tree" ? p.widthM * p.depthM : p.shape == "disc" ? p.widthM * p.widthM : p.widthM * p.depthM;
                    withPieces.Items.Add(new LoadItem
                    {
                        Id = "sun_" + p.key + "_" + withPieces.Items.Count, Label = p.name, Name = p.name, Kind = LoadKind.Zone,
                        X = p.x, Y = p.y, Width = p.widthM, Height = p.shape == "disc" ? p.widthM : p.depthM,
                        DeadKnM2 = loaded > 0 ? p.addedLoadKn / loaded : 0, LiveKnM2 = 0, Basis = "shading equipment (" + p.name + ")",
                    });
                }
                var after = StructureModel.Compute(withPieces);
                effect.addedKn = (float)placed.Sum(p => p.addedLoadKn);
                effect.peakUtilisationAfter = after.summary.peakUtilisation;
                effect.baysOverAfter = after.summary.baysOver;
                effect.worstBayAfter = after.summary.worstBay;
            }
            catch (Exception) { effect.ran = false; }
        }

        // ---------------------------------------------------------------- words

        static string F0(double v) { return v.ToString("0", CultureInfo.InvariantCulture); }
        static string F1(double v) { return v.ToString("0.#", CultureInfo.InvariantCulture); }
        static string F2(double v) { return v.ToString("0.##", CultureInfo.InvariantCulture); }

        static List<AssumptionUse> DescribeInputs(SunInputs inputs, double target, double minSun, string allowed)
        {
            var acc = inputs.Structure.AcceptedAssumptions;
            var uses = new List<AssumptionUse>();
            uses.Add(AnalysisAssumptions.Use(AnalysisAssumptions.SiteLatitude, inputs.LatitudeDeg.HasValue, F1(inputs.LatitudeDeg ?? DefaultLatitudeDeg) + " deg N", acc));
            uses.Add(AnalysisAssumptions.Use(AnalysisAssumptions.RoofNorth, inputs.NorthDeg.HasValue, F0(inputs.NorthDeg ?? DefaultNorthDeg) + " deg", acc));
            uses.Add(AnalysisAssumptions.Use(AnalysisAssumptions.ShadeTarget, inputs.ShadeTargetPercent.HasValue, F0(target) + "%", acc));
            uses.Add(AnalysisAssumptions.Use(AnalysisAssumptions.GardenMinSun, inputs.GardenMinSunHours.HasValue, F1(minSun) + " h", acc));
            var label = allowed == "light" ? "light only" : allowed == "fixed" ? "fixed only" : "all kinds";
            uses.Add(AnalysisAssumptions.Use(AnalysisAssumptions.ShadeEquipment, inputs.Equipment == "all" || inputs.Equipment == "light" || inputs.Equipment == "fixed", label, acc));
            var cap = inputs.Structure.CapacityKnM2.HasValue;
            uses.Add(AnalysisAssumptions.Use(AnalysisAssumptions.DeckCapacity, cap, F1(inputs.Structure.CapacityKnM2 ?? StructureModel.DefaultCapacityKnM2) + " kN/m2", acc));
            return uses;
        }

        static List<string> Assumptions(SunInputs inputs, double lat, double target, double minSun, string allowed, double windPa)
        {
            return new List<string>
            {
                "Screening model, not a daylight or thermal design: direct sun only, on the roof's plan, in 0.5 m cells, every half hour of SOLAR time (the sun is due south at 12:00; clock time differs by the longitude, the time zone, summer time and the equation of time).",
                "Sun: declination by Spencer's series, hour angle 15 degrees per hour, counted above " + F0(MinElevationDeg) + " degrees, at latitude " + F1(lat) + " N on 21 June, 21 March and 21 December. Diffuse light, cloud, reflections and neighbouring buildings are not modelled.",
                "Shadows: walls (their footprint swept along the shadow), equipment plates shifted by their height over the tangent of the sun's height, tree canopies as spheres (plants of " + F0(MinShadingPlantM) + " m or more; they let " + F2(TreeTauSummer) + " of the sun through in summer, " + F2(TreeTauEquinox) + " in March, " + F2(TreeTauWinter) + " bare in December).",
                "Judgement: a people zone (play area, or the " + F0(SpectatorMarginM) + " m band around a court with seats) needs " + F0(target) + "% shade on average between " + F0(WindowFromH) + ":00 and " + F0(WindowToH) + ":00 on 21 June; a garden needs " + F1(minSun) + " h of direct sun on 21 June. The courts' play areas are neither shaded nor judged.",
                "Equipment (" + (allowed == "light" ? "light only" : allowed == "fixed" ? "fixed only" : "all kinds") + "; the lightest piece wins among those within 10% of the best shade): pergola 2.6 m (lets 15% through, 0.35 kN/m2), solid canopy 3.0 m (0%, 0.5), sail 3.5 m (10%, 0.03), parasol 2.8 m (10%, 0.05), tree in a deep bed (crown 5 m, 13.5 kN/m2 on 3 x 3 m). Tried at every 1 m around the zone, off the courts, drains, openings, entries and taller walls, posts " + F1(PostClearanceM) + " m from the walkways; at most " + MaxPieces + " pieces, " + MaxPerZone + " per zone, each worth at least " + F0(MinGain * 100) + " points of shade; no garden may fall below its need.",
                "Wind on the equipment: the wind analysis's peak pressure (" + F0(windPa) + " Pa) x the piece's coefficient (1.2 to 1.5) x its area: the uplift its anchors carry, an estimate for the structural engineer. Weight and wind are not applied to the structural analysis's balance except as the added load in the deck check.",
            };
        }

        static void Advise(SunReport r, List<Zone> noOption, double target, double minSun)
        {
            var recs = r.recommendations;
            var s = r.summary;
            var people = r.zones.Where(z => z.kind == "people" || z.kind == "spectators").ToList();
            var gardens = r.zones.Where(z => z.kind == "garden").ToList();

            foreach (var z in people.Where(x => x.status == "too-sunny").OrderBy(x => x.peakShadePercent))
            {
                var mine = r.equipment.Where(p => p.zoneId == z.id).ToList();
                var text = z.label + ": " + F0(z.peakShadePercent) + "% in shade between " + F0(WindowFromH) + " and " + F0(WindowToH) + " h on 21 June, against a target of " + F0(target) + "% (" + F1(z.sunHoursJune) + " h of sun in all)";
                if (mine.Count > 0)
                    text += ". " + string.Join(" and ", mine.Select(p => "a " + p.name.ToLowerInvariant() + " (" + Size(p) + ", " + F1(p.heightM) + " m high) at x " + F1(p.x + p.widthM * 0.5) + ", y " + F1(p.y + p.depthM * 0.5)).ToArray())
                            + " brings the shade to " + F0(z.afterPeakShadePercent) + "%" + (z.afterStatus == "ok" ? "." : ", still short of the target.");
                else text += ".";
                recs.Add(new SunRecommendation { kind = "too-sunny", target = z.label, text = text });
            }
            foreach (var z in noOption)
            {
                var zs = r.zones.FirstOrDefault(x => x.id == z.Id);
                if (zs != null && zs.afterStatus == "too-sunny" && r.equipment.All(p => p.zoneId != z.Id))
                    recs.Add(new SunRecommendation { kind = "no-option", target = z.Label, text = z.Label + " is too sunny, but no piece of the allowed kinds fits beside it without breaking a rule (a court, a walkway, a drain, an entry, an opening, another piece) or taking the sun from a garden. Move something, allow another kind of equipment, or shade it with a tree or a building element in the design." });
            }
            foreach (var z in gardens.Where(x => x.status == "too-shaded").OrderBy(x => x.sunHoursJune))
                recs.Add(new SunRecommendation { kind = "too-shaded", target = z.label, text = z.label + " gets " + F1(z.sunHoursJune) + " h of direct sun on 21 June, under the " + F1(minSun) + " h a planting needs: a shade-tolerant planting suits it, or the walls and trees that shade it should move." });
            foreach (var p in r.equipment)
                recs.Add(new SunRecommendation
                {
                    kind = "equipment", target = p.name,
                    text = "A " + p.name.ToLowerInvariant() + " (" + Size(p) + ", " + F1(p.heightM) + " m high) at x " + F1(p.x + p.widthM * 0.5) + ", y " + F1(p.y + p.depthM * 0.5) + " for " + p.zoneLabel + ": adds about " + F1(p.addedLoadKn) + " kN of permanent load" +
                           (p.windUpliftKn > 0.05 ? " and up to " + F1(p.windUpliftKn) + " kN of wind uplift on its anchors" : "") + " (" + p.note + ").",
                });
            if (r.structure.ran && r.equipment.Count > 0)
            {
                var worse = r.structure.baysOverAfter > r.structure.baysOverBefore;
                recs.Add(new SunRecommendation
                {
                    kind = "structure", target = "Deck",
                    text = "With the equipment the deck carries " + F1(r.structure.addedKn) + " kN more: the most loaded bay goes from " + F0(r.structure.peakUtilisationBefore * 100) + "% to " + F0(r.structure.peakUtilisationAfter * 100) + "% of the deck capacity, " +
                           (worse ? r.structure.baysOverAfter + " bays over it instead of " + r.structure.baysOverBefore + ": check the deck before any of it is built" : "no bay more over it than before") + (s.preliminary ? " (against a capacity nobody has confirmed)" : "") + ".",
                });
            }
            if (gardens.Any(g => g.afterStatus == "too-shaded" && g.status != "too-shaded"))
                recs.Add(new SunRecommendation { kind = "too-shaded", target = "Gardens", text = "Some garden falls below its sun need with the equipment in place: check the sun hours in the zone list." });
            if (recs.Count == 0)
                recs.Add(new SunRecommendation { kind = "fine", target = "Roof", text = "Every people zone has at least " + F0(target) + "% shade at midday on 21 June and every garden gets at least " + F1(minSun) + " h of sun: no equipment is needed." });
        }

        static string Size(EquipmentPiece p)
        {
            return p.shape == "disc" ? F1(p.widthM) + " m across" : F1(p.widthM) + " x " + F1(p.depthM) + " m";
        }

        /// <summary>One line naming what was analysed, shared by the add-in's dialogs and the video's title card.</summary>
        public static string CaseStudy(SunInputs inputs, SunReport r)
        {
            return "Sun and shade on a " + F1(inputs.Structure.RoofLength) + " x " + F1(inputs.Structure.RoofWidth) + " m roof at " + F1(r.summary.latitudeDeg) + " deg N: " +
                   r.summary.peopleZones + " people zone" + (r.summary.peopleZones == 1 ? "" : "s") + ", " + r.summary.gardenZones + " garden" + (r.summary.gardenZones == 1 ? "" : "s");
        }

        // ---------------------------------------------------------------- for the video

        /// <summary>The sunlit fraction of every cell for a sun position, with the scene as it is (used by the film, which draws what the analysis computed).</summary>
        public static void FillFractions(SunGrid grid, SunScene scene, SunPosition sun, double treeTau, double[] into, List<Shape> scratch)
        {
            scene.Shapes(sun, treeTau, scratch);
            for (var j = 0; j < grid.Ny; j++)
                for (var i = 0; i < grid.Nx; i++)
                {
                    var c = grid.Index(i, j);
                    into[c] = grid.Active[c] ? (sun.Up ? SunScene.Fraction(scratch, grid.CentreX(i), grid.CentreY(j)) : 0.0) : 0.0;
                }
        }
    }
}
