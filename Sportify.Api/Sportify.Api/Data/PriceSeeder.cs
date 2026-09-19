using Microsoft.EntityFrameworkCore;
using Sportify.Api.Models;

namespace Sportify.Api.Data
{
    /// <summary>
    /// Fills in prices that are missing, and only those.
    ///
    /// Runs on every startup rather than once against an empty database,
    /// because ReferenceDataSeeder bails out the moment the catalog exists and
    /// these columns were added after there was already data in it. Anything
    /// that already carries a price is left alone — including a price someone
    /// typed in through the Data tab, which must never be overwritten by a
    /// figure we guessed.
    ///
    /// ── What these numbers are ──
    /// Estimated German market rates, material plus installation, at roughly
    /// 2025 levels. Every one is written with PriceIsQuoted = false, because
    /// none of them came from a supplier quote for this project. That flag is
    /// the whole point: the same distinction the build-up thicknesses already
    /// make between a manufacturer's published figure and a typical value.
    /// Replace them with real quotes and set the flag — nothing else changes.
    ///
    /// ── PriceUnit does two jobs ──
    /// "EUR/m3" also means "take this off by volume", "EUR/m2" means by area.
    /// One field so the price and the quantity can never disagree about how a
    /// layer is measured: substrate is bought by the cubic metre, a drainage
    /// board by the square metre whatever its thickness.
    ///
    /// ── DIN 276 ──
    /// The cost group each line belongs to. A German estimate has to arrive
    /// sorted into these; a bare total cannot be benchmarked or used to derive
    /// a fee. 363 Dachbeläge, 530 Oberbau/Deckschichten, 560 Einbauten,
    /// 570 Vegetationsflächen. Reasonable classifications, not authoritative
    /// ones — a Kostenplaner should confirm them.
    /// </summary>
    public static class PriceSeeder
    {
        private const string Src = "Estimated German market rate (material + installation), ~2025";

        /// <summary>
        /// True when this row still carries a figure THIS class wrote. Our own
        /// estimates get refreshed as they are corrected; a supplier quote, or
        /// anything a user typed in the Data tab, is left exactly alone.
        /// </summary>
        private static bool IsOurEstimate(double? value, string? source, bool quoted)
            => value == null || (!quoted && (source ?? "").StartsWith("Estimated German market rate"));

        // ── Sport, playground and roofing materials ──
        private static readonly Dictionary<string, (double Value, string Unit, string Kg)> MaterialPrices = new()
        {
            ["PVC sheet, single-layer (economy)"]                        = (32,  "EUR/m2", "530"),
            ["Sports vinyl / PVC flooring"]                              = (55,  "EUR/m2", "530"),
            ["Wood sprung floor / parquet"]                              = (95,  "EUR/m2", "530"),
            ["Poured polyurethane / synthetic rubber flooring"]          = (70,  "EUR/m2", "530"),
            ["Artificial turf"]                                          = (40,  "EUR/m2", "530"),
            ["Elastic underlay"]                                         = (18,  "EUR/m2", "530"),
            ["Impact-attenuating rubber safety surfacing"]               = (85,  "EUR/m2", "530"),
            // Seating is really priced per seat; per m2 of stand footprint is
            // what this app can actually measure, so that is what is stored.
            ["Aluminium tip-up grandstand seating"]                      = (480, "EUR/m2", "560"),
            ["Steel modular bleacher bench"]                             = (260, "EUR/m2", "560"),
            ["Extensive substrate (mineral, lightweight)"]               = (50,  "EUR/m3", "570"),
            ["Intensive substrate (deep, high water-capacity)"]          = (62,  "EUR/m3", "570"),
            ["Root barrier membrane (HDPE/PE)"]                          = (5,   "EUR/m2", "363"),
            ["Drainage & water-retention board"]                         = (24,  "EUR/m2", "363"),
            ["Bituminous / EPDM roof waterproofing membrane"]            = (38,  "EUR/m2", "363"),
        };

        // ── Build-up layers, by the manufacturer's own product name ──
        private static readonly Dictionary<string, (double Value, string Unit, string Kg)> LayerPrices = new()
        {
            ["Plant layer per plant list"]                               = (25, "EUR/m2", "570"),
            ["Growing media Zincoblend I"]                               = (62, "EUR/m3", "570"),
            ["Filter Sheet SF"]                                          = (3,  "EUR/m2", "363"),
            ["Floradrain FD 60 neo, filled with Zincoblend M"]           = (28, "EUR/m2", "363"),
            ["Protection Mat ISM 50"]                                    = (6,  "EUR/m2", "363"),
            ["Root Barrier WSB 100-PO"]                                  = (5,  "EUR/m2", "363"),

            ["Plant community Sloped Sedum"]                             = (20, "EUR/m2", "570"),
            ["Growing media Zincoblend E"]                               = (55, "EUR/m3", "570"),
            ["Georaster Elements"]                                       = (30, "EUR/m2", "363"),
            ["Protection Mat WSM 150"]                                   = (7,  "EUR/m2", "363"),

            ["Mature sedum blanket"]                                     = (23, "EUR/m2", "570"),
            ["Extensive substrate"]                                      = (50, "EUR/m3", "570"),
            ["Water retention and filter layer"]                         = (13, "EUR/m2", "363"),
            ["Root-resistant waterproofing"]                             = (38, "EUR/m2", "363"),

            ["Seed mix / plug planting"]                                 = (14, "EUR/m2", "570"),
            ["Filter fleece 105"]                                        = (3,  "EUR/m2", "363"),
            ["Drainage element FKD 25"]                                  = (17, "EUR/m2", "363"),
            ["Protection mat RMS 500"]                                   = (7,  "EUR/m2", "363"),

            ["Hardwood deck boards"]                                     = (95, "EUR/m2", "530"),
            ["Aluminium joists"]                                         = (28, "EUR/m2", "530"),
            ["Adjustable pedestals"]                                     = (26, "EUR/m2", "530"),
            ["Washed round gravel 16/32"]                                = (55, "EUR/m3", "530"),
            ["Filter fleece"]                                            = (3,  "EUR/m2", "363"),
            ["Resin-bound aggregate"]                                    = (78, "EUR/m2", "530"),
            ["Permeable base course"]                                    = (45, "EUR/m3", "530"),
            ["Concrete paving slab"]                                     = (48, "EUR/m2", "530"),
            ["Adjustable pedestal"]                                      = (26, "EUR/m2", "530"),
            ["Protection mat"]                                           = (6,  "EUR/m2", "363"),
        };

        // ── Plants, priced per plant as a nursery sells them ──
        private static readonly Dictionary<string, double> PlantPrices = new()
        {
            ["Cornus mas"]                    = 420,
            ["Pyrus salicifolia Pendula"]     = 360,
            ["Pinus parviflora Glauca"]       = 540,
            ["Carpinus japonica"]             = 390,
            ["Lavandula angustifolia"]        = 7,
            ["Festuca glauca"]                = 5,
            ["Sedum mix"]                     = 4,
        };

        /// <summary>
        /// Anything not named above still gets a figure, from its category, so
        /// a total is never quietly short. Coarse on purpose — a species with
        /// its own row is always worth more than this fallback.
        /// </summary>
        private static double FallbackPlantPrice(string? category)
        {
            var c = (category ?? "").ToLowerInvariant();
            // Categories are free text — "Groundcover / succulent", "Shrub /
            // perennial" — so match on what the string contains. Order matters:
            // the woodiest match wins, since "Shrub / perennial" is both.
            if (c.Contains("tree")) return 350;
            if (c.Contains("shrub")) return 25;
            if (c.Contains("grass")) return 6;
            if (c.Contains("groundcover") || c.Contains("ground cover") || c.Contains("succulent")) return 4;
            if (c.Contains("perennial") || c.Contains("herb")) return 8;
            return 15;
        }

        /// <summary>
        /// The columns themselves. ReferenceDbContext is created with
        /// EnsureCreated(), which builds the schema once and then never touches
        /// it again — so a column added to a model after the database exists is
        /// simply absent, and every query against it throws.
        ///
        /// Deleting the file would fix it and would also throw away anything a
        /// user has added through the Data tab, so instead each column is added
        /// in place. SQLite has no "ADD COLUMN IF NOT EXISTS", and re-running is
        /// normal here, so a duplicate-column error is the expected outcome on
        /// every start after the first and is swallowed deliberately.
        /// </summary>
        private static void EnsureColumns(ReferenceDbContext db)
        {
            var columns = new[]
            {
                "PriceValue REAL NULL", "PriceUnit TEXT NULL",
                "PriceSource TEXT NULL", "PriceIsQuoted INTEGER NOT NULL DEFAULT 0",
                "CostGroupDin276 TEXT NULL",
            };
            foreach (var table in new[] { "Materials", "Plants", "RoofAssemblyLayers" })
            {
                foreach (var col in columns)
                {
                    try { db.Database.ExecuteSqlRaw($"ALTER TABLE {table} ADD COLUMN {col}"); }
                    catch { /* already there */ }
                }
            }
        }

        /// <summary>
        /// The app offers three quality tiers — Economy, Standard, Premium —
        /// but the catalog only had a surface for two of them, so Economy and
        /// Standard had to share a material and therefore a price. This adds
        /// the missing economy row rather than mapping two tiers onto one
        /// product, which would have made the tier choice free.
        /// </summary>
        private static void EnsureEconomyFlooring(ReferenceDbContext db)
        {
            const string name = "PVC sheet, single-layer (economy)";
            if (db.Materials.Any(m => m.Name == name)) return;
            db.Materials.Add(new Material
            {
                Name = name,
                Category = "Flooring",
                NormCode = "EN 14904",
                PerformanceClass = "Type 4 — point-elastic",
                Notes = "Entry-level single-layer PVC sports surface. Lower force reduction and shorter service life than a two-layer sports vinyl.",
            });
            db.SaveChanges();
        }

        public static void Backfill(ReferenceDbContext db)
        {
            EnsureColumns(db);
            EnsureEconomyFlooring(db);
            int touched = 0;

            foreach (var m in db.Materials)
            {
                if (!IsOurEstimate(m.PriceValue, m.PriceSource, m.PriceIsQuoted)) continue;
                if (!MaterialPrices.TryGetValue(m.Name, out var p)) continue;
                m.PriceValue = p.Value; m.PriceUnit = p.Unit;
                m.CostGroupDin276 = p.Kg; m.PriceSource = Src; m.PriceIsQuoted = false;
                touched++;
            }

            foreach (var l in db.RoofAssemblyLayers)
            {
                if (!IsOurEstimate(l.PriceValue, l.PriceSource, l.PriceIsQuoted)) continue;
                if (!LayerPrices.TryGetValue(l.Name, out var p)) continue;
                l.PriceValue = p.Value; l.PriceUnit = p.Unit;
                l.CostGroupDin276 = p.Kg; l.PriceSource = Src; l.PriceIsQuoted = false;
                touched++;
            }

            foreach (var pl in db.Plants)
            {
                if (!IsOurEstimate(pl.PriceValue, pl.PriceSource, pl.PriceIsQuoted)) continue;
                bool named = PlantPrices.TryGetValue(pl.ScientificName ?? "", out var each);
                pl.PriceValue = named ? each : FallbackPlantPrice(pl.Category);
                pl.PriceUnit = "EUR/each";
                pl.CostGroupDin276 = "570";
                pl.PriceSource = named ? Src : Src + " — category average, no figure for this species";
                pl.PriceIsQuoted = false;
                touched++;
            }

            if (touched > 0) db.SaveChanges();
        }
    }
}
