#nullable disable
using System;
using System.Collections.Generic;
using System.Globalization;

namespace Sportify.Simulation.Wind
{
    // ---------------------------------------------------------------------------------------
    //  Wind and erosion screening of a roof garden.
    //
    //  This file uses nothing from UnityEngine on purpose: it is plain arithmetic over a layout,
    //  so the numbers can be produced (and tested) outside Unity - inside the Revit add-in,
    //  in a unit test, or behind a web service - and the renderer that draws them can be swapped
    //  for a lighter one later without touching the analysis.
    //
    //  It is a SCREENING model, not a structural design. It borrows the concepts of
    //  EN 1991-1-4 (peak velocity pressure, the F/G/H/I zoning of a flat roof) and the two
    //  concerns the FLL green-roof guideline raises for wind - uplift of the build-up and of
    //  tall planting, and erosion of exposed growing medium - and reduces each to a formula a
    //  designer can follow. Every constant that is a judgement call is named below and repeated
    //  in the report's assumptions, so it can be argued with.
    //
    //  Units: metres, kilograms, newtons, pascals unless a name says otherwise. Layout
    //  coordinates: x right, y down the plan (like the web app); a wind direction is the way
    //  the wind BLOWS TOWARD, in degrees from +x toward +y (0 = toward the right edge, i.e.
    //  the wind comes from the left).
    // ---------------------------------------------------------------------------------------

    /// <summary>The flat-roof zones of EN 1991-1-4 (Figure 7.6). I is the calm interior.</summary>
    public enum RoofZone { I = 0, H = 1, G = 2, F = 3 }

    public enum CoverKind { MatureVegetation, PlantedOrSeeded, CoarseGravel, BareSubstrate, Paved }

    // ------------------------------------------------------------------------ inputs

    public class LayerInput
    {
        public string Function;      // vegetation | substrate | filter | drainage | protection | root_barrier | waterproofing | wearing | bedding
        public string Name;
        public double ThicknessMm;
    }

    public class AssemblyInput
    {
        public string Key;
        public string System;            // provider and system name, for reports
        public string SystemName;        // the system name alone, for short labels
        public string Category;          // extensive | intensive | walkway
        public double? SaturatedKgM2;    // published, when the provider prints it
        public double? WaterStorageLM2;  // published, when the provider prints it
        public List<LayerInput> Layers = new List<LayerInput>();   // ordered top to bottom
    }

    public class ZoneInput
    {
        public string Id;
        public string Label;
        public double X, Y, Width, Height;   // rectangle in layout metres (top-left corner, x extent, y extent)
        public AssemblyInput Assembly;
    }

    public class PlantInput
    {
        public string Id;
        public string Species;
        public string Form;            // tree | shrub | grass | groundcover
        public double X, Y;            // centre, layout metres
        public double HeightM, CrownM, MinSubstrateMm;
    }

    public class WindInputs
    {
        public double RoofLength, RoofWidth;
        public double RoofElevationM;          // elevation of the roof in the Revit project (world_origin_z_m); 0 = not known
        public double RoofHeightAboveGroundM;  // height above ground when the Revit model or the designer gave it; 0 = not given
        public string RoofHeightSource;        // where that came from, for the report
        public string WindZone;                // "1".."4"; null = not given
        public string WindZoneSource;          // where it came from ("DIBt list ...", "set by hand ..."), for the report
        public double? NorthDeg;               // compass bearing of the top of the plan; null = the designer hasn't set it
        public double BasicWindSpeedMs;        // 0 = use the wind zone
        public string TerrainCategory;
        public List<ZoneInput> Zones = new List<ZoneInput>();
        public List<PlantInput> Plants = new List<PlantInput>();
    }

    // ------------------------------------------------------------------------ results (serialised by Unity's JsonUtility)

    [Serializable]
    public class WindSiteReport
    {
        public string windZone;
        public float basicWindSpeedMs;
        public string terrainCategory;
        public float roughnessLengthM;
        public float roofElevationM;
        public bool roofElevationAssumed;
        public string roofElevationNote;
        public float peakPressureAtRoofPa;
        public float peakSpeedAtRoofMs;
        public float edgeZoneWidthM;       // the G/F strip, e/10, for the wider axis
        public string windZoneSource;      // where the zone came from
        public bool windZoneAssumed;
        public string roofHeightSource;    // where the roof height came from
        public bool hasNorth;
        public float northDeg;             // compass bearing of the top of the plan (when hasNorth)
        public float prevailingFromBearingDeg;
        public int prevailingDirectionIndex;   // the analysed direction nearest the prevailing wind, or -1 (needs the orientation)
    }

    [Serializable]
    public class WindDirectionReport
    {
        public float towardAngleDeg;
        public string label;               // "from the left", ... (relative to the plan)
        public string compassLabel;        // "from the south-west" (only when the orientation is known)
        public float fromBearingDeg;       // compass bearing the wind comes from (only when the orientation is known)
        public bool isPrevailing;
        public int plantsFailing;
        public int plantsMarginal;
        public int zonesUpliftFlagged;
        public float maxSpeedUp;
    }

    [Serializable]
    public class ZoneWindResult
    {
        public string id;
        public string label;
        public string assemblyKey;
        public string system;
        public string category;
        public float areaM2;
        public float dryWeightKgM2;
        public string weightSource;        // "published (saturated - storage)" | "estimated from the layers" | "no build-up data"
        public float substrateMm;
        public string coverKind;
        public float upliftUtilisationMax; // > 1 fails
        public float upliftFlaggedAreaPercent;
        public string upliftStatus;        // ok | marginal | fails | unknown
        public string worstEdge;           // the roof edge nearest the flagged area
        public float flaggedDepthM;        // how far the flagged area reaches in from that edge
        public float requiredBallastMm;    // extra gravel (1.8 t/m3) that would bring the worst point to 1.0
        public float requiredDryWeightKgM2;
        public float erosionOnsetBareMs;         // 10 m wind at which loose substrate starts to move (worst point)
        public float erosionOnsetEstablishedMs;  // same, once the planting has closed
        public float erosionBareAreaPercent;     // share of the zone where BARE substrate moves at Beaufort 6 (while the planting establishes)
        public float erosionAreaPercent;   // share of the zone that still loses material at Beaufort 8 once established
        public float erosionBandDepthM;    // how far in from the worst edge that share reaches
        public string erosionEdge;
        public string erosionRisk;         // low | medium | high (once established) | n/a (paved)
    }

    [Serializable]
    public class PlantWindResult
    {
        public string id;
        public string species;
        public string form;
        public float xM, yM;
        public float heightM, crownM;
        public string zoneId;
        public float substrateMm;
        public float minSubstrateMm;
        public string roofZone;            // worst EN zone over the crown, over all directions
        public float peakSpeedMs;          // local, at the crown
        public float overturningMomentKNm;
        public float resistingMomentKNm;
        public float utilisation;          // 1.5 * overturning / (0.9 * resisting); > 1 fails
        public string status;              // ok | marginal | fails | not-checked
        public float worstTowardAngleDeg;
        public string worstDirectionLabel;
        public float requiredAnchorageKNm; // extra resisting moment an anchor would have to supply
        public float requiredSubstrateMm;  // substrate depth that would carry it by weight alone
        public bool canMoveToFix;          // a better spot inside the same zone brings it to 1.0 or less
        public float betterXM, betterYM;   // where (layout metres); the best spot found inside the zone, when it is an improvement
        public float betterUtilisation;
        public string note;
        public float[] utilisationByDirection;
    }

    [Serializable]
    public class WindRecommendation
    {
        public string kind;                // ballast | heavier-build-up | fix-paving | anchor-or-relocate | erosion-control
        public string target;
        public string edge;
        public string text;
    }

    [Serializable]
    public class WindSummary
    {
        public int plantsChecked;
        public int plantsFailing;
        public int plantsMarginal;
        public int zonesChecked;
        public int zonesUpliftFlagged;
        public int zonesErosionHigh;
        public float plantedAreaM2;
        public float percentPlantedAreaUpliftFlagged;
        public float percentPlantedAreaErosionFlagged;   // still erodes at Beaufort 8 once established
        public float percentPlantedAreaBareErodes;       // erodes at Beaufort 6 while bare
        public float lowestBareOnsetMs;   // the wind at which loose substrate first starts to move anywhere on the roof
    }

    [Serializable]
    public class WindReport
    {
        public bool ran;
        public List<string> assumptions = new List<string>();
        public WindSiteReport site = new WindSiteReport();
        public List<WindDirectionReport> directions = new List<WindDirectionReport>();
        public List<ZoneWindResult> zones = new List<ZoneWindResult>();
        public List<PlantWindResult> plants = new List<PlantWindResult>();
        public WindSummary summary = new WindSummary();
        public List<WindRecommendation> recommendations = new List<WindRecommendation>();
    }

    /// <summary>The wind at this roof, resolved from the inputs and the defaults.</summary>
    public class WindSite
    {
        public string WindZone, TerrainCategory;
        public double BasicWindSpeed, Z0, ZMin, RoofElevation;
        public bool ElevationAssumed;
        public string ElevationNote = "";
        public string ElevationSource = "";
        public string WindZoneSource = "";
        public bool WindZoneAssumed;
        public double QRoof;   // peak velocity pressure at the roof surface, Pa

        public double PeakPressureAt(double z)
        {
            return WindModel.PeakPressure(BasicWindSpeed, Z0, ZMin, z);
        }
    }

    // ------------------------------------------------------------------------ the model

    public static class WindModel
    {
        // --- constants that are judgement calls (all repeated in the report) ---
        public const double AirDensity = 1.25;                 // kg/m3 (EN 1991-1-4)
        public const double VonKarman = 0.4;
        public const double Gravity = 9.81;
        public const double GammaWind = 1.5;                   // partial factor, unfavourable variable action
        public const double GammaWeightFavourable = 0.9;       // partial factor, favourable permanent action
        public const double MarginalFrom = 0.8;                // utilisation from which an item is "marginal"
        public const double GravelKgPerM2PerMm = 1.8;          // gravel ballast: 1.8 t/m3
        public const double SaturatedSubstrateKgM3 = 1650;     // root ball, watered
        public const double TreeDragCoefficient = 0.5;         // leafy crown, per projected area
        public const double CrownHeightFraction = 0.65;        // crown depth as a share of tree height
        public const double CentreOfPressureFraction = 0.675;  // height of the crown's centre / tree height
        public const double RootPlateRadiusCrownFraction = 0.25; // root plate radius as a share of crown DIAMETER
        public const double RootPlateRadiusMaxM = 2.5;
        public const double TreeMassAtSixMetresKg = 100;       // above-ground mass, scaled with (height / 6 m)^2
        public const double CellSizeM = 0.5;
        public const double StrongBreezeMs = 10.8;             // Beaufort 6: what bare substrate must survive while the planting establishes
        public const double GaleMs = 17.2;                     // Beaufort 8: what closed planting must survive
        public const double EstablishedCoverFactor = 2.0;      // closed planting doubles the wind speed needed to move the substrate
        public const double LightweightGrainMm = 3.0;          // roof substrate: lightweight mineral aggregate
        public const double LightweightGrainDensity = 1200;    // kg/m3, particle density including pores
        public const double GravelGrainMm = 8.0;
        public const double GravelGrainDensity = 2600;
        public const double ErosionReferenceHeightM = 2.0;     // above the roof surface
        public const double AssumedRoofElevationM = 12.0;
        public const double PlausibleGivenHeightMaxM = 300.0;  // a height the model or the designer gave outright
        public const double PrevailingFromBearingDeg = 250.0;  // generic for Germany (west-south-west): NOT site data
        public const double PlausibleRoofElevationMaxM = 60.0; // above this the value is a datum height, not a building height
        public const double ErosionHighShare = 0.10;           // a zone is "high" when this share of it still erodes once established

        public static readonly double[] DirectionsDeg = { 0, 45, 90, 135, 180, 225, 270, 315 };

        // EN 1991-1-4 Table 7.2, flat roof, sharp eaves, external pressure coefficients for a 10 m2 area.
        public static double Cpe(RoofZone z)
        {
            switch (z)
            {
                case RoofZone.F: return -1.8;
                case RoofZone.G: return -1.2;
                case RoofZone.H: return -0.7;
                default: return -0.2;
            }
        }

        /// <summary>
        /// Local wind speed relative to the calm interior. A suction coefficient is a pressure
        /// drop, so by Bernoulli the surface speed goes as sqrt(1 - cpe); dividing by the
        /// interior's value makes the calm zone 1.0.
        /// </summary>
        public static double SpeedUp(RoofZone z)
        {
            return Math.Sqrt(1.0 - Cpe(z)) / Math.Sqrt(1.0 - Cpe(RoofZone.I));
        }

        // EN 1991-1-4 Table 4.1
        public static double RoughnessLength(string terrain)
        {
            switch ((terrain ?? "III").Trim().ToUpperInvariant())
            {
                case "0": return 0.003;
                case "I": return 0.01;
                case "II": return 0.05;
                case "IV": return 1.0;
                default: return 0.3;
            }
        }

        public static double MinHeight(string terrain)
        {
            switch ((terrain ?? "III").Trim().ToUpperInvariant())
            {
                case "0": return 1;
                case "I": return 1;
                case "II": return 2;
                case "IV": return 10;
                default: return 5;
            }
        }

        /// <summary>DIN EN 1991-1-4/NA basic wind speed (10 min mean, 10 m) by German wind zone.</summary>
        public static double BasicWindSpeed(string windZone)
        {
            switch ((windZone ?? "2").Trim())
            {
                case "1": return 22.5;
                case "3": return 27.5;
                case "4": return 30.0;
                default: return 25.0;
            }
        }

        /// <summary>Mean wind speed at height z (EN 1991-1-4 4.3): v_m = v_b * k_r * ln(z/z0).</summary>
        public static double MeanSpeed(double vb, double z0, double zmin, double z)
        {
            var kr = 0.19 * Math.Pow(z0 / 0.05, 0.07);
            return vb * kr * Math.Log(Math.Max(z, zmin) / z0);
        }

        /// <summary>Peak velocity pressure q_p(z) = (1 + 7 I_v) * 1/2 rho v_m^2 (EN 1991-1-4 4.5), in pascals.</summary>
        public static double PeakPressure(double vb, double z0, double zmin, double z)
        {
            var zz = Math.Max(z, zmin);
            var vm = MeanSpeed(vb, z0, zmin, zz);
            var iv = 1.0 / Math.Log(zz / z0);
            return (1.0 + 7.0 * iv) * 0.5 * AirDensity * vm * vm;
        }

        // ---------------------------------------------------------------- roof zoning

        /// <summary>
        /// Which EN zone a point of the roof is in for one wind direction. For each windward
        /// edge (the wind blows in across it) the zone follows from the distance in from that
        /// edge and along it: e = min(b, 2h) with b the edge's length; F within e/10 of the edge
        /// and e/4 of a corner, G within e/10, H within e/2, I beyond. Oblique winds have two
        /// windward edges and the worse zone wins - a screening simplification, since the code
        /// only tabulates winds square to an edge.
        /// </summary>
        public static RoofZone Classify(double x, double y, double dirDeg, double roofLength, double roofWidth, double roofElevation)
        {
            var rad = dirDeg * Math.PI / 180.0;
            var ux = Math.Cos(rad);
            var uy = Math.Sin(rad);
            var worst = RoofZone.I;

            if (Math.Abs(ux) > 0.01)
            {
                var d = ux > 0 ? x : roofLength - x;
                var a = Math.Min(y, roofWidth - y);
                worst = Worse(worst, ZoneAt(d, a, Math.Min(roofWidth, 2.0 * roofElevation)));
            }
            if (Math.Abs(uy) > 0.01)
            {
                var d = uy > 0 ? y : roofWidth - y;
                var a = Math.Min(x, roofLength - x);
                worst = Worse(worst, ZoneAt(d, a, Math.Min(roofLength, 2.0 * roofElevation)));
            }
            return worst;
        }

        static RoofZone ZoneAt(double d, double a, double e)
        {
            d = Math.Max(0, d);
            if (d <= e / 10.0) return a <= e / 4.0 ? RoofZone.F : RoofZone.G;
            if (d <= e / 2.0) return RoofZone.H;
            return RoofZone.I;
        }

        static RoofZone Worse(RoofZone a, RoofZone b)
        {
            return (int)a >= (int)b ? a : b;
        }

        public static string DirectionLabel(double towardDeg)
        {
            var d = ((towardDeg % 360) + 360) % 360;
            switch ((int)Math.Round(d / 45.0) % 8)
            {
                case 0: return "from the left";
                case 1: return "from the top-left";
                case 2: return "from the top";
                case 3: return "from the top-right";
                case 4: return "from the right";
                case 5: return "from the bottom-right";
                case 6: return "from the bottom";
                default: return "from the bottom-left";
            }
        }

        // ---------------------------------------------------------------- build-ups

        /// <summary>
        /// Dry weight per m2. Where the provider prints saturated weight and water storage, dry =
        /// saturated - storage (1 L = 1 kg). Otherwise it is estimated from the layers with
        /// typical dry densities (checked against ZinCo Sloped Sedum: 125 estimated vs 114 published).
        /// </summary>
        public static double DryWeightKgM2(AssemblyInput a, out string source)
        {
            if (a == null)
            {
                source = "no build-up data";
                return 0;
            }

            if (a.SaturatedKgM2.HasValue && a.WaterStorageLM2.HasValue && a.SaturatedKgM2.Value > 0)
            {
                source = "published (saturated - storage)";
                return Math.Max(0, a.SaturatedKgM2.Value - a.WaterStorageLM2.Value);
            }

            var intensive = string.Equals(a.Category, "intensive", StringComparison.OrdinalIgnoreCase);
            double total = 0;
            foreach (var l in a.Layers)
                total += l.ThicknessMm / 1000.0 * DryDensityKgM3(l.Function, l.Name, intensive);
            source = a.Layers.Count == 0 ? "no build-up data" : "estimated from the layers";
            return total;
        }

        static bool LooksLikeGravel(string name)
        {
            var n = (name ?? "").ToLowerInvariant();
            return n.Contains("gravel") || n.Contains("kies") || n.Contains("split") || n.Contains("grit") || n.Contains("sand");
        }

        static double DryDensityKgM3(string function, string name, bool intensive)
        {
            if (LooksLikeGravel(name)) return 1700;   // loose gravel / sand, bulk dry
            switch ((function ?? "").Trim().ToLowerInvariant())
            {
                case "vegetation": return 150;
                case "substrate": return intensive ? 1200 : 1000;
                case "filter": return 300;
                case "drainage": return 250;
                case "protection": return 400;
                case "root_barrier": return 1000;
                case "waterproofing": return 1000;
                case "wearing": return 2300;
                case "bedding": return 150;
                default: return 500;
            }
        }

        public static double SubstrateMm(AssemblyInput a)
        {
            if (a == null) return 0;
            double mm = 0;
            foreach (var l in a.Layers)
                if (string.Equals(l.Function, "substrate", StringComparison.OrdinalIgnoreCase)) mm += l.ThicknessMm;
            return mm;
        }

        /// <summary>What the surface of the build-up is made of, from its category and top layer.</summary>
        public static CoverKind Cover(AssemblyInput a)
        {
            if (a == null || a.Layers.Count == 0) return CoverKind.BareSubstrate;
            if (string.Equals(a.Category, "walkway", StringComparison.OrdinalIgnoreCase)) return CoverKind.Paved;

            var top = a.Layers[0];
            var name = (top.Name ?? "").ToLowerInvariant();
            var fn = (top.Function ?? "").ToLowerInvariant();

            if (fn == "wearing") return CoverKind.Paved;
            if (LooksLikeGravel(name)) return CoverKind.CoarseGravel;
            if (fn == "vegetation" && !(name.Contains("seed") || name.Contains("plug") || name.Contains("plant layer")))
                return CoverKind.MatureVegetation;

            // Everything else that is not paved or gravel is a planting bed: older exports name no
            // vegetation layer, but a garden parcel is planted by definition.
            return CoverKind.PlantedOrSeeded;
        }

        /// <summary>
        /// What the layer function of a legacy garden layer (older exports name layers instead of
        /// giving them a function) most likely is.
        /// </summary>
        public static string LegacyLayerFunction(string layerName, string material)
        {
            var s = ((layerName ?? "") + " " + (material ?? "")).ToLowerInvariant();
            if (s.Contains("drain") || s.Contains("reservoir") || s.Contains("retention")) return "drainage";
            if (s.Contains("filter") || s.Contains("fleece")) return "filter";
            if (s.Contains("protect")) return "protection";
            if (s.Contains("root")) return "root_barrier";
            if (s.Contains("waterproof") || s.Contains("membrane")) return "waterproofing";
            if (LooksLikeGravel(s)) return "bedding";
            return "substrate";
        }

        // ---------------------------------------------------------------- uplift + erosion at a point

        /// <summary>1.5 x suction against 0.9 x dry weight, at a point in roof zone <paramref name="z"/>.</summary>
        public static double UpliftUtilisation(RoofZone z, double qRoofPa, double dryKgM2)
        {
            var suction = -Cpe(z) * qRoofPa;
            var resist = GammaWeightFavourable * dryKgM2 * Gravity;
            return resist > 1e-9 ? GammaWind * suction / resist : double.PositiveInfinity;
        }

        /// <summary>Dry weight the build-up would need at a point in zone <paramref name="z"/> to reach utilisation 1.0.</summary>
        public static double RequiredDryKgM2(RoofZone z, double qRoofPa)
        {
            var suction = -Cpe(z) * qRoofPa;
            return GammaWind * suction / GammaWeightFavourable / Gravity;
        }

        /// <summary>
        /// The wind speed (10 m, terrain profile of the site) at which loose grains of this size
        /// start to move at a point where the roof speeds the wind up by <paramref name="speedUp"/>:
        /// Bagnold's threshold shear velocity u*_t = 0.1 sqrt((rho_p/rho_a) g d), carried to a
        /// speed 2 m above the surface with the log law (z0 = d/30) and back to the 10 m wind.
        /// Frequent winds, not the design storm, decide erosion - so this is an ONSET speed to
        /// compare with a Beaufort number, not a load.
        /// </summary>
        static double ErosionOnset(double grainMm, double grainDensity, double speedUp, WindSite site)
        {
            var d = grainMm / 1000.0;
            var uStarT = 0.1 * Math.Sqrt((grainDensity - AirDensity) / AirDensity * Gravity * d);
            var z0s = d / 30.0;
            var zRef = site.RoofElevation + ErosionReferenceHeightM;
            var terrainRatio = Math.Log(zRef / site.Z0) / Math.Log(10.0 / site.Z0);   // speed at zRef / speed at 10 m
            return uStarT * Math.Log(ErosionReferenceHeightM / z0s) / (VonKarman * speedUp * terrainRatio);
        }

        /// <summary>
        /// 10 m wind speed at which this surface starts to lose material, at a point in roof zone
        /// <paramref name="z"/>. <paramref name="established"/> = the planting has closed, which
        /// doubles the speed for a vegetated surface. Infinity for paved surfaces.
        /// </summary>
        public static double ErosionOnsetMs(CoverKind cover, bool established, RoofZone z, WindSite site)
        {
            if (cover == CoverKind.Paved) return double.PositiveInfinity;

            var s = SpeedUp(z);
            if (cover == CoverKind.CoarseGravel) return ErosionOnset(GravelGrainMm, GravelGrainDensity, s, site);

            var bare = ErosionOnset(LightweightGrainMm, LightweightGrainDensity, s, site);
            var closes = cover == CoverKind.MatureVegetation || cover == CoverKind.PlantedOrSeeded;
            return established && closes ? bare * EstablishedCoverFactor : bare;
        }

        // ---------------------------------------------------------------- compass

        static double Mod360(double deg)
        {
            return ((deg % 360.0) + 360.0) % 360.0;
        }

        /// <summary>
        /// The compass bearing a wind comes FROM, given the way it blows toward on the plan and the bearing of the top of
        /// the plan. Plan right is northDeg + 90; a wind blowing toward (right, up) = (cos t, -sin t) travels on the bearing
        /// northDeg + atan2(right, up), and comes from the opposite one.
        /// </summary>
        public static double FromBearing(double towardDeg, double northDeg)
        {
            var rad = towardDeg * Math.PI / 180.0;
            var travel = northDeg + Math.Atan2(Math.Cos(rad), -Math.Sin(rad)) * 180.0 / Math.PI;
            return Mod360(travel + 180.0);
        }

        public static string CompassLabel(double fromBearing)
        {
            switch ((int)Math.Round(Mod360(fromBearing) / 45.0) % 8)
            {
                case 0: return "from the north";
                case 1: return "from the north-east";
                case 2: return "from the east";
                case 3: return "from the south-east";
                case 4: return "from the south";
                case 5: return "from the south-west";
                case 6: return "from the west";
                default: return "from the north-west";
            }
        }

        /// <summary>"from the south-west (bottom-left)" when the orientation is known, else "from the bottom-left".</summary>
        public static string DirectionText(WindDirectionReport d)
        {
            if (string.IsNullOrEmpty(d.compassLabel)) return d.label;
            return d.compassLabel + " (" + d.label.Replace("from the ", "") + ")";
        }

        // ---------------------------------------------------------------- the analysis

        public static WindSite ResolveSite(WindInputs inputs)
        {
            var zoneGiven = !string.IsNullOrEmpty(inputs.WindZone);
            var site = new WindSite
            {
                WindZone = zoneGiven ? inputs.WindZone : "2",
                WindZoneAssumed = !zoneGiven,
                WindZoneSource = zoneGiven
                    ? (string.IsNullOrEmpty(inputs.WindZoneSource) ? "given with the layout" : inputs.WindZoneSource)
                    : "assumed: the layout gives no wind zone (set a site in Germany in the Site tab)",
                TerrainCategory = string.IsNullOrEmpty(inputs.TerrainCategory) ? "III" : inputs.TerrainCategory,
            };
            site.BasicWindSpeed = inputs.BasicWindSpeedMs > 0 ? inputs.BasicWindSpeedMs : BasicWindSpeed(site.WindZone);
            site.Z0 = RoughnessLength(site.TerrainCategory);
            site.ZMin = MinHeight(site.TerrainCategory);

            var z = inputs.RoofElevationM;
            if (inputs.RoofHeightAboveGroundM > 0.5 && inputs.RoofHeightAboveGroundM <= PlausibleGivenHeightMaxM)
            {
                // The height above ground itself, from the Revit model or typed by the designer.
                site.RoofElevation = inputs.RoofHeightAboveGroundM;
                site.ElevationSource = string.IsNullOrEmpty(inputs.RoofHeightSource) ? "given with the layout" : inputs.RoofHeightSource;
            }
            else if (z > 0.5 && z <= PlausibleRoofElevationMaxM)
            {
                site.RoofElevation = z;
                site.ElevationSource = "the roof's elevation in the Revit project, read as its height above ground";
            }
            else
            {
                site.ElevationAssumed = true;
                site.ElevationSource = "assumed";
                site.RoofElevation = AssumedRoofElevationM;
                site.ElevationNote = z > PlausibleRoofElevationMaxM
                    ? "The layout gives an elevation of " + F1(z) + " m, which reads as a datum height rather than a height above ground; " + F1(AssumedRoofElevationM) + " m is assumed."
                    : "The layout does not give the roof's height above ground; " + F1(AssumedRoofElevationM) + " m is assumed.";
            }

            site.QRoof = site.PeakPressureAt(site.RoofElevation);
            return site;
        }

        public static WindReport Analyse(WindInputs inputs)
        {
            var site = ResolveSite(inputs);
            var L = inputs.RoofLength;
            var W = inputs.RoofWidth;
            var h = site.RoofElevation;

            var report = new WindReport { ran = true };
            report.site = new WindSiteReport
            {
                windZone = site.WindZone,
                basicWindSpeedMs = (float)site.BasicWindSpeed,
                terrainCategory = site.TerrainCategory,
                roughnessLengthM = (float)site.Z0,
                roofElevationM = (float)h,
                roofElevationAssumed = site.ElevationAssumed,
                roofElevationNote = site.ElevationNote,
                peakPressureAtRoofPa = (float)site.QRoof,
                peakSpeedAtRoofMs = (float)Math.Sqrt(2.0 * site.QRoof / AirDensity),
                edgeZoneWidthM = (float)(Math.Min(Math.Max(L, W), 2.0 * h) / 10.0),
                windZoneSource = site.WindZoneSource,
                windZoneAssumed = site.WindZoneAssumed,
                roofHeightSource = site.ElevationSource,
                hasNorth = inputs.NorthDeg.HasValue,
                northDeg = (float)(inputs.NorthDeg ?? 0.0),
                prevailingFromBearingDeg = (float)PrevailingFromBearingDeg,
                prevailingDirectionIndex = -1,
            };
            report.assumptions.AddRange(Assumptions(site, inputs.NorthDeg));

            var dirs = DirectionsDeg;
            var dirReports = new WindDirectionReport[dirs.Length];
            for (var i = 0; i < dirs.Length; i++)
                dirReports[i] = new WindDirectionReport { towardAngleDeg = (float)dirs[i], label = DirectionLabel(dirs[i]), compassLabel = "" };

            if (inputs.NorthDeg.HasValue)
            {
                var bestGap = double.MaxValue;
                for (var i = 0; i < dirs.Length; i++)
                {
                    var from = FromBearing(dirs[i], inputs.NorthDeg.Value);
                    dirReports[i].fromBearingDeg = (float)from;
                    dirReports[i].compassLabel = CompassLabel(from);
                    var gap = Math.Abs(Mod360(from - PrevailingFromBearingDeg + 180.0) - 180.0);
                    if (gap < bestGap) { bestGap = gap; report.site.prevailingDirectionIndex = i; }
                }
                dirReports[report.site.prevailingDirectionIndex].isPrevailing = true;
            }

            foreach (var zone in inputs.Zones)
                report.zones.Add(AnalyseZone(zone, inputs, site, dirReports));

            foreach (var plant in inputs.Plants)
                report.plants.Add(AnalysePlant(plant, inputs, site, dirReports));

            for (var i = 0; i < dirs.Length; i++) report.directions.Add(dirReports[i]);

            Summarise(report);
            Recommend(report, site);
            return report;
        }

        static List<string> Assumptions(WindSite site, double? northDeg)
        {
            var list = new List<string>
            {
                "Screening model after EN 1991-1-4 concepts and the FLL green-roof guideline's wind concerns; not a structural design.",
                "Design wind: German wind zone " + site.WindZone + " (basic speed " + F1(site.BasicWindSpeed) + " m/s; " + site.WindZoneSource + "), terrain category " +
                    site.TerrainCategory + " (assumed: no site survey), roof surface " + F1(site.RoofElevation) + " m above ground (" + site.ElevationSource + ").",
                "Roof zones F/G/H/I with e = min(b, 2h), sharp eaves (no parapet), eight wind directions; the worst direction governs each item.",
                "Uplift: 1.5 x suction (c_pe,10 x q_p) against 0.9 x the dry weight of the build-up, which acts as ballast.",
                "Trees: leafy crown drag 0.5 on a crown-shaped silhouette; root ball a disc of watered substrate (1650 kg/m3), radius 25% of the crown diameter, tipping about its edge; 1.5 x wind moment against 0.9 x resisting moment.",
                "Erosion: onset speed at 10 m for grain motion (Bagnold threshold; 3 mm lightweight substrate, 8 mm gravel). Bare substrate is held to Beaufort 6 (10.8 m/s) while the planting establishes; closed planting doubles the onset speed and is held to Beaufort 8 (17.2 m/s).",
            };
            if (site.ElevationAssumed && !string.IsNullOrEmpty(site.ElevationNote)) list.Add(site.ElevationNote);
            if (northDeg.HasValue)
                list.Add("Orientation: the top of the plan is compass bearing " + F0(Mod360(northDeg.Value)) + " degrees. The prevailing wind is taken as generic for Germany (from about west-south-west, bearing " +
                         F0(PrevailingFromBearingDeg) + "), not site data; it only marks which direction is shown as prevailing, every direction is still checked.");
            else
                list.Add("Orientation not set: directions are named relative to the plan, and no direction is marked as prevailing.");
            return list;
        }

        // ----------------------------------------------------------- zones

        static ZoneWindResult AnalyseZone(ZoneInput zone, WindInputs inputs, WindSite site, WindDirectionReport[] dirReports)
        {
            var a = zone.Assembly ?? new AssemblyInput { Key = "(none)", System = "no build-up", Category = "extensive" };
            string weightSource;
            var dry = DryWeightKgM2(a, out weightSource);
            var known = dry > 0;
            var cover = Cover(a);
            var paved = cover == CoverKind.Paved;

            var L = inputs.RoofLength;
            var W = inputs.RoofWidth;
            var h = site.RoofElevation;
            var dirs = DirectionsDeg;
            var q = site.QRoof;

            var nx = Math.Max(1, (int)Math.Round(zone.Width / CellSizeM));
            var ny = Math.Max(1, (int)Math.Round(zone.Height / CellSizeM));
            var cw = zone.Width / nx;
            var ch = zone.Height / ny;
            var half = 0.5 * Math.Min(cw, ch);

            double maxU = 0, maxExtraKgM2 = 0;
            double onsetBare = double.PositiveInfinity, onsetEstablished = double.PositiveInfinity;
            var upliftFlagged = 0;
            var erosionFlagged = 0;
            var bareFlagged = 0;
            var upliftPerEdge = new int[4];                          // top, bottom, left, right
            var upliftDepth = new double[4];
            var erosionPerEdge = new int[4];
            var erosionDepth = new double[4];
            var zoneFlaggedInDir = new bool[dirs.Length];

            for (var iy = 0; iy < ny; iy++)
            {
                for (var ix = 0; ix < nx; ix++)
                {
                    var x = zone.X + (ix + 0.5) * cw;
                    var y = zone.Y + (iy + 0.5) * ch;
                    var cellMaxU = 0.0;
                    var cellMinBare = double.PositiveInfinity;
                    var cellMinEstablished = double.PositiveInfinity;

                    for (var d = 0; d < dirs.Length; d++)
                    {
                        var rz = Classify(x, y, dirs[d], L, W, h);

                        var s = SpeedUp(rz);
                        if (s > dirReports[d].maxSpeedUp) dirReports[d].maxSpeedUp = (float)s;

                        if (known)
                        {
                            var u = UpliftUtilisation(rz, q, dry);
                            if (u > cellMaxU) cellMaxU = u;
                            var extra = Math.Max(0, RequiredDryKgM2(rz, q) - dry);
                            if (extra > maxExtraKgM2) maxExtraKgM2 = extra;
                            if (u > 1.0) zoneFlaggedInDir[d] = true;
                        }

                        if (!paved)
                        {
                            var ob = ErosionOnsetMs(cover, false, rz, site);
                            var oe = ErosionOnsetMs(cover, true, rz, site);
                            if (ob < onsetBare) onsetBare = ob;
                            if (oe < onsetEstablished) onsetEstablished = oe;
                            if (ob < cellMinBare) cellMinBare = ob;
                            if (oe < cellMinEstablished) cellMinEstablished = oe;
                        }
                    }

                    if (cellMaxU > maxU) maxU = cellMaxU;

                    double dist;
                    var edge = NearestEdge(x, y, L, W, out dist);

                    if (known && cellMaxU > 1.0)
                    {
                        upliftFlagged++;
                        upliftPerEdge[edge]++;
                        if (dist + half > upliftDepth[edge]) upliftDepth[edge] = dist + half;
                    }

                    if (!paved && cellMinBare < StrongBreezeMs) bareFlagged++;

                    if (!paved && cellMinEstablished < GaleMs)
                    {
                        erosionFlagged++;
                        erosionPerEdge[edge]++;
                        if (dist + half > erosionDepth[edge]) erosionDepth[edge] = dist + half;
                    }
                }
            }

            for (var d = 0; d < dirs.Length; d++)
                if (zoneFlaggedInDir[d]) dirReports[d].zonesUpliftFlagged++;

            var upliftEdge = MostCells(upliftPerEdge);
            var erosionEdge = MostCells(erosionPerEdge);
            var total = nx * ny;

            var res = new ZoneWindResult
            {
                id = zone.Id,
                label = zone.Label,
                assemblyKey = a.Key,
                system = a.System,
                category = a.Category,
                areaM2 = (float)(zone.Width * zone.Height),
                dryWeightKgM2 = (float)dry,
                weightSource = weightSource,
                substrateMm = (float)SubstrateMm(a),
                coverKind = cover.ToString(),
                upliftUtilisationMax = (float)(known ? Math.Min(maxU, 999.0) : 0.0),
                upliftFlaggedAreaPercent = (float)(100.0 * upliftFlagged / total),
                worstEdge = upliftFlagged > 0 ? EdgeName(upliftEdge) : "",
                flaggedDepthM = upliftFlagged > 0 ? (float)RoundUp(upliftDepth[upliftEdge], 0.5) : 0f,
                requiredBallastMm = (float)(maxExtraKgM2 > 0 ? RoundUp(maxExtraKgM2 / GravelKgPerM2PerMm, 5) : 0),
                requiredDryWeightKgM2 = (float)(dry + maxExtraKgM2),
                erosionOnsetBareMs = paved ? 0f : (float)onsetBare,
                erosionOnsetEstablishedMs = paved ? 0f : (float)onsetEstablished,
                erosionBareAreaPercent = (float)(100.0 * bareFlagged / total),
                erosionAreaPercent = (float)(100.0 * erosionFlagged / total),
                erosionBandDepthM = erosionFlagged > 0 ? (float)RoundUp(erosionDepth[erosionEdge], 0.5) : 0f,
                erosionEdge = erosionFlagged > 0 ? EdgeName(erosionEdge) : "",
            };

            res.upliftStatus = !known ? "unknown" : (maxU > 1.0 ? "fails" : (maxU > MarginalFrom ? "marginal" : "ok"));

            if (paved) res.erosionRisk = "n/a";
            else if ((double)erosionFlagged / total >= ErosionHighShare) res.erosionRisk = "high";
            else if (erosionFlagged > 0) res.erosionRisk = "medium";
            else res.erosionRisk = "low";
            return res;
        }

        static int MostCells(int[] perEdge)
        {
            var best = 0;
            for (var e = 1; e < perEdge.Length; e++) if (perEdge[e] > perEdge[best]) best = e;
            return best;
        }

        /// <summary>The roof edge a point is closest to: 0 top (y = 0), 1 bottom (y = W), 2 left (x = 0), 3 right (x = L).</summary>
        public static int NearestEdge(double x, double y, double L, double W, out double distance)
        {
            var best = 0;
            distance = y;
            if (W - y < distance) { distance = W - y; best = 1; }
            if (x < distance) { distance = x; best = 2; }
            if (L - x < distance) { distance = L - x; best = 3; }
            return best;
        }

        public static string EdgeName(int e)
        {
            switch (e)
            {
                case 0: return "top";
                case 1: return "bottom";
                case 2: return "left";
                default: return "right";
            }
        }

        static double RoundUp(double v, double step)
        {
            return Math.Ceiling(v / step - 1e-9) * step;
        }

        // ----------------------------------------------------------- plants

        static ZoneInput ZoneAtPoint(WindInputs inputs, double x, double y)
        {
            foreach (var z in inputs.Zones)
                if (x >= z.X && x <= z.X + z.Width && y >= z.Y && y <= z.Y + z.Height) return z;
            return null;
        }

        /// <summary>Trees, and anything two metres tall or more, can be blown over; the rest counts as cover.</summary>
        public static bool NeedsStabilityCheck(PlantInput p)
        {
            return string.Equals(p.Form, "tree", StringComparison.OrdinalIgnoreCase) || p.HeightM >= 2.0;
        }

        /// <summary>What a tree presents to the wind and what holds it down, before it is placed anywhere.</summary>
        sealed class TreeLoad
        {
            public double Area, Lever, QPlant, PlateR, PlateArea, TreeWeightN;

            public TreeLoad(PlantInput p, WindSite site)
            {
                Area = Math.PI / 4.0 * p.CrownM * (CrownHeightFraction * p.HeightM);        // crown silhouette
                Lever = CentreOfPressureFraction * p.HeightM;
                QPlant = site.PeakPressureAt(site.RoofElevation + 0.7 * p.HeightM);
                PlateR = Math.Min(RootPlateRadiusCrownFraction * p.CrownM, RootPlateRadiusMaxM);
                PlateArea = Math.PI * PlateR * PlateR;
                TreeWeightN = TreeMassAtSixMetresKg * Math.Pow(p.HeightM / 6.0, 2) * Gravity;
            }

            /// <summary>Moment that holds it upright, tipping about the edge of the root plate, in N m.</summary>
            public double ResistingNm(double substrateMm)
            {
                var soilN = SaturatedSubstrateKgM3 * Gravity * PlateArea * (substrateMm / 1000.0);
                return (soilN + TreeWeightN) * PlateR;
            }
        }

        /// <summary>
        /// The worst utilisation of a tree standing at (x, y) over all wind directions, sampling the
        /// crown (its centre and four points on its rim) for the worst roof zone it reaches.
        /// </summary>
        static double TreeUtilisationAt(PlantInput p, TreeLoad t, WindInputs inputs, WindSite site, double x, double y,
                                        double resistingNm, float[] perDirection,
                                        out int worstDir, out RoofZone worstZone, out double worstSpeedUp, out double worstMoment)
        {
            var dirs = DirectionsDeg;
            var offsets = new[] { new[] { 0.0, 0.0 }, new[] { 1.0, 0.0 }, new[] { -1.0, 0.0 }, new[] { 0.0, 1.0 }, new[] { 0.0, -1.0 } };

            var worstU = 0.0;
            worstDir = 0;
            worstZone = RoofZone.I;
            worstSpeedUp = 1.0;
            worstMoment = 0.0;

            for (var d = 0; d < dirs.Length; d++)
            {
                var rz = RoofZone.I;
                foreach (var o in offsets)
                    rz = Worse(rz, Classify(x + o[0] * p.CrownM / 2.0, y + o[1] * p.CrownM / 2.0, dirs[d],
                                            inputs.RoofLength, inputs.RoofWidth, site.RoofElevation));

                var s = SpeedUp(rz);
                var m = TreeDragCoefficient * t.QPlant * s * s * t.Area * t.Lever;
                var u = resistingNm > 1e-9 ? GammaWind * m / (GammaWeightFavourable * resistingNm) : 999.0;
                if (perDirection != null) perDirection[d] = (float)Math.Min(u, 999.0);

                if (u > worstU) { worstU = u; worstDir = d; worstZone = rz; worstSpeedUp = s; worstMoment = m; }
            }
            return worstU;
        }

        static PlantWindResult AnalysePlant(PlantInput p, WindInputs inputs, WindSite site, WindDirectionReport[] dirReports)
        {
            var dirs = DirectionsDeg;
            var zone = ZoneAtPoint(inputs, p.X, p.Y);
            var substrateMm = zone != null ? SubstrateMm(zone.Assembly) : 0.0;

            var res = new PlantWindResult
            {
                id = p.Id, species = p.Species, form = p.Form, xM = (float)p.X, yM = (float)p.Y,
                heightM = (float)p.HeightM, crownM = (float)p.CrownM,
                zoneId = zone != null ? zone.Id : "",
                substrateMm = (float)substrateMm, minSubstrateMm = (float)p.MinSubstrateMm,
                status = "not-checked",
                utilisationByDirection = new float[dirs.Length],
                roofZone = "I",
                worstDirectionLabel = "",
                note = "",
            };

            if (!NeedsStabilityCheck(p))
            {
                res.note = "Low planting: overturning is not the issue; it counts as cover in the erosion check.";
                return res;
            }

            var t = new TreeLoad(p, site);
            var resisting = t.ResistingNm(substrateMm);

            int worstDir;
            RoofZone worstZone;
            double worstSpeedUp, moment;
            var worstU = TreeUtilisationAt(p, t, inputs, site, p.X, p.Y, resisting, res.utilisationByDirection,
                                           out worstDir, out worstZone, out worstSpeedUp, out moment);

            for (var d = 0; d < dirs.Length; d++)
            {
                var u = res.utilisationByDirection[d];
                if (u > 1.0) dirReports[d].plantsFailing++;
                else if (u > MarginalFrom) dirReports[d].plantsMarginal++;
            }

            res.roofZone = worstZone.ToString();
            res.peakSpeedMs = (float)(Math.Sqrt(2.0 * t.QPlant / AirDensity) * worstSpeedUp);
            res.overturningMomentKNm = (float)(moment / 1000.0);
            res.resistingMomentKNm = (float)(resisting / 1000.0);
            res.utilisation = (float)Math.Min(worstU, 999.0);
            res.status = worstU > 1.0 ? "fails" : (worstU > MarginalFrom ? "marginal" : "ok");
            res.worstTowardAngleDeg = (float)dirs[worstDir];
            res.worstDirectionLabel = DirectionLabel(dirs[worstDir]);

            if (res.status != "ok")
            {
                // what would fix it
                var neededMoment = GammaWind * moment / GammaWeightFavourable;          // resisting moment needed at the worst speed-up
                var neededSoilN = neededMoment / t.PlateR - t.TreeWeightN;
                var requiredMm = neededSoilN > 0 ? neededSoilN / (SaturatedSubstrateKgM3 * Gravity * t.PlateArea) * 1000.0 : 0.0;
                res.requiredAnchorageKNm = (float)Math.Max(0, (GammaWind * moment - GammaWeightFavourable * resisting) / 1000.0);
                res.requiredSubstrateMm = (float)RoundUp(Math.Max(requiredMm, p.MinSubstrateMm), 10);

                // Would a better spot in the same bed help? Every wind direction has its own windward
                // edges, so on a narrow roof there may be no calm spot at all; search rather than guess.
                if (zone != null) FindBetterSpot(p, t, inputs, site, zone, resisting, worstU, res);
            }

            if (zone == null) res.note = "Not on a build-up: nothing roots this plant.";
            else if (p.MinSubstrateMm > substrateMm)
                res.note = "Build-up gives " + F0(substrateMm) + " mm of substrate; the species wants " + F0(p.MinSubstrateMm) + " mm.";
            return res;
        }

        static void FindBetterSpot(PlantInput p, TreeLoad t, WindInputs inputs, WindSite site, ZoneInput zone,
                                   double resistingNm, double currentU, PlantWindResult res)
        {
            const double margin = 0.5;      // keep the trunk this far inside the bed
            var bestU = currentU;
            double bestX = p.X, bestY = p.Y;

            for (var x = zone.X + margin; x <= zone.X + zone.Width - margin + 1e-9; x += CellSizeM)
            {
                for (var y = zone.Y + margin; y <= zone.Y + zone.Height - margin + 1e-9; y += CellSizeM)
                {
                    int wd;
                    RoofZone wz;
                    double ws, wm;
                    var u = TreeUtilisationAt(p, t, inputs, site, x, y, resistingNm, null, out wd, out wz, out ws, out wm);
                    if (u < bestU - 1e-6) { bestU = u; bestX = x; bestY = y; }
                }
            }

            if (bestU < currentU - 0.02)
            {
                res.betterXM = (float)bestX;
                res.betterYM = (float)bestY;
                res.betterUtilisation = (float)bestU;
                res.canMoveToFix = bestU <= 1.0;
            }
        }

        // ----------------------------------------------------------- summary + advice

        static void Summarise(WindReport r)
        {
            var s = new WindSummary { zonesChecked = r.zones.Count, lowestBareOnsetMs = 0f };
            foreach (var p in r.plants)
            {
                if (p.status == "not-checked") continue;
                s.plantsChecked++;
                if (p.status == "fails") s.plantsFailing++;
                else if (p.status == "marginal") s.plantsMarginal++;
            }

            double planted = 0, upliftArea = 0, erosionArea = 0, bareArea = 0;
            var lowest = double.PositiveInfinity;
            foreach (var z in r.zones)
            {
                if (z.upliftStatus == "fails") s.zonesUpliftFlagged++;
                if (z.erosionRisk == "n/a") continue;

                planted += z.areaM2;
                upliftArea += z.areaM2 * z.upliftFlaggedAreaPercent / 100.0;
                erosionArea += z.areaM2 * z.erosionAreaPercent / 100.0;
                bareArea += z.areaM2 * z.erosionBareAreaPercent / 100.0;
                if (z.erosionRisk == "high") s.zonesErosionHigh++;
                if (z.erosionOnsetBareMs < lowest) lowest = z.erosionOnsetBareMs;
            }
            s.plantedAreaM2 = (float)planted;
            s.percentPlantedAreaUpliftFlagged = planted > 0 ? (float)(100.0 * upliftArea / planted) : 0f;
            s.percentPlantedAreaErosionFlagged = planted > 0 ? (float)(100.0 * erosionArea / planted) : 0f;
            s.percentPlantedAreaBareErodes = planted > 0 ? (float)(100.0 * bareArea / planted) : 0f;
            s.lowestBareOnsetMs = double.IsInfinity(lowest) ? 0f : (float)lowest;
            r.summary = s;
        }

        public static string F0(double v) { return v.ToString("0", CultureInfo.InvariantCulture); }
        public static string F1(double v) { return v.ToString("0.#", CultureInfo.InvariantCulture); }

        /// <summary>One line naming what was analysed: the title card, the results file and the dialog all say this.</summary>
        public static string CaseStudy(WindInputs inputs)
        {
            var trees = 0;
            foreach (var p in inputs.Plants) if (NeedsStabilityCheck(p)) trees++;
            var others = inputs.Plants.Count - trees;

            var parts = new List<string>();
            parts.Add(inputs.Zones.Count + " green-roof zone" + (inputs.Zones.Count == 1 ? "" : "s"));
            if (trees > 0) parts.Add(trees + " tree" + (trees == 1 ? "" : "s"));
            if (others > 0) parts.Add(others + " smaller plant" + (others == 1 ? "" : "s"));
            return string.Join(", ", parts.ToArray()) + " on a " + F1(inputs.RoofLength) + " x " + F1(inputs.RoofWidth) + " m roof";
        }

        static void Recommend(WindReport r, WindSite site)
        {
            foreach (var z in r.zones)
            {
                if (z.upliftStatus == "fails")
                {
                    var paved = z.erosionRisk == "n/a";
                    var wholeZone = z.upliftFlaggedAreaPercent > 60;
                    r.recommendations.Add(new WindRecommendation
                    {
                        kind = paved ? "fix-paving" : (wholeZone ? "heavier-build-up" : "ballast"),
                        target = z.label + " (" + z.system + ")",
                        edge = z.worstEdge,
                        text = z.label + ": " + z.system + " weighs " + F0(z.dryWeightKgM2) + " kg/m2 dry; wind uplift governs " + F0(z.upliftFlaggedAreaPercent) +
                               "% of its area, reaching " + F1(z.flaggedDepthM) + " m in from the " + z.worstEdge + " edge. " +
                               (paved
                                   ? "Fix or ballast the paving in that band (at least " + F0(z.requiredDryWeightKgM2) + " kg/m2)."
                                   : wholeZone
                                       ? "Use a system of at least " + F0(z.requiredDryWeightKgM2) + " kg/m2 dry, or add " + F0(z.requiredBallastMm) + " mm of gravel."
                                       : "Add " + F0(z.requiredBallastMm) + " mm of gravel ballast in that band (about +" + F0(z.requiredBallastMm * GravelKgPerM2PerMm) + " kg/m2)."),
                    });
                }

                if (z.erosionRisk == "high")
                {
                    var gravel = z.coverKind == CoverKind.CoarseGravel.ToString();
                    r.recommendations.Add(new WindRecommendation
                    {
                        kind = "erosion-control",
                        target = z.label,
                        edge = z.erosionEdge,
                        text = z.label + (gravel ? ": the gravel starts to move in a gale" : ": even closed planting loses substrate in a gale") +
                               " (Beaufort 8, 17 m/s) in the strip within " + F1(z.erosionBandDepthM) +
                               " m of the " + z.erosionEdge + " edge (" + F0(z.erosionAreaPercent) + "% of the zone). " +
                               (gravel ? "Use a coarser grain (32/63) or a fixed edge restraint there."
                                       : "Keep that strip in coarse gravel (16/32) or under an erosion mat."),
                    });
                }
            }

            if (r.summary.percentPlantedAreaBareErodes > 0)
            {
                r.recommendations.Add(new WindRecommendation
                {
                    kind = "erosion-control",
                    target = "All planted zones",
                    edge = "",
                    text = "Until the planting closes (first one to two seasons) bare substrate starts to move at " + F1(r.summary.lowestBareOnsetMs) +
                           " m/s at the most exposed point, and on " + F0(r.summary.percentPlantedAreaBareErodes) + "% of the planted area in a strong breeze (10.8 m/s). " +
                           "Use pre-vegetated mats or an erosion mat, and water in after wind.",
                });
            }

            foreach (var p in r.plants)
            {
                if (p.status != "fails") continue;
                var options = new List<string>();
                if (p.requiredAnchorageKNm > 0.05) options.Add("anchorage rated for " + F0(p.requiredAnchorageKNm) + " kN m");
                options.Add(F0(p.requiredSubstrateMm) + " mm of substrate");
                if (p.canMoveToFix) options.Add("moving it to about (" + F1(p.betterXM) + ", " + F1(p.betterYM) + ") in the same bed (wind load " + F1(p.betterUtilisation) + "x)");
                r.recommendations.Add(new WindRecommendation
                {
                    kind = "anchor-or-relocate",
                    target = p.species,
                    edge = "",
                    text = p.species + " at (" + F1(p.xM) + ", " + F1(p.yM) + "): wind load is " + F1(p.utilisation) + "x what it can resist (" + p.worstDirectionLabel +
                           ", zone " + p.roofZone + "). Needs " + string.Join(" or ", options.ToArray()) + ".",
                });
            }
        }
    }
}
