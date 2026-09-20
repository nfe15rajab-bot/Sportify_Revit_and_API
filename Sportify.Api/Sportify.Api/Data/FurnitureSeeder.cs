using Microsoft.EntityFrameworkCore;
using Sportify.Api.Models;

namespace Sportify.Api.Data
{
    /// <summary>
    /// Site furniture from German manufacturers.
    ///
    /// Entered by hand from manufacturer and reseller pages rather than
    /// scraped: their terms prohibit automated collection, and redistributing
    /// their files is a licensing problem to put in the middle of a pipeline.
    /// The data is what matters, and the data is on the product page.
    ///
    /// ── What is verified and what is not ──
    /// Where a figure came off a published page it is marked published. Where
    /// it is a normal value for that product type it is marked typical, and
    /// wants checking against a datasheet before anyone tenders from it. Same
    /// rule the build-up thicknesses follow, for the same reason: geometry
    /// needs a number for everything, and someone writing a tender has to know
    /// which numbers came from the supplier.
    ///
    /// ── DIN 276 ──
    /// 560 Einbauten in Außenanlagen for furniture; 550 Technische Anlagen in
    /// Außenanlagen for a bollard light, because an illuminated bollard is an
    /// electrical installation rather than a fitting.
    /// </summary>
    public static class FurnitureSeeder
    {
        private static FurnitureItem F(
            string key, string manufacturer, string product, string category,
            double lengthM, double widthM, double heightM, bool dimsPublished,
            int seats, double? weightKg, bool weightPublished,
            double? price, string? material, string? description,
            string costGroup, string? sourceUrl, double? litres = null) =>
            new()
            {
                Key = key, Manufacturer = manufacturer, ManufacturerCountry = "Germany",
                ProductName = product, Category = category,
                LengthM = lengthM, WidthM = widthM, HeightM = heightM,
                DimensionsPublished = dimsPublished,
                Seats = seats, WeightKg = weightKg, WeightPublished = weightPublished,
                CapacityLitres = litres,
                PriceValue = price, PriceUnit = price == null ? null : "EUR/each",
                PriceSource = price == null ? null
                    : "Estimated German market rate unless the source says otherwise, ~2025",
                PriceIsQuoted = false,
                CostGroupDin276 = costGroup,
                Material = material, Description = description, SourceUrl = sourceUrl,
            };

        public static void Seed(ReferenceDbContext db)
        {
            EnsureTable(db);

            var wanted = new[]
            {
                // ── Benches ──
                // Seat length, seat height and weight are all on the product
                // page; depth is typical for a bench of this type.
                F("abes_parkbank_1114", "ABES Public Design", "Parkbank 1.114", "bench",
                  1.80, 0.70, 0.80, true, 3, 49, true, 560,
                  "Hot-dip galvanised and powder-coated steel",
                  "Three-seat park bench with backrest and armrests.",
                  "560", "https://abes-online.com/produkt/stadtmobiliar-parkbank-1-114"),

                F("hds_b50021", "HDS Stadtmobiliar", "B 50021", "bench",
                  1.80, 0.452, 0.46, true, 3, 42, false, 430,
                  "Steel frame with timber slats",
                  "Backless bench — sits either way, which suits an open deck.",
                  "560", "https://www.stadtmobiliar.de/de/produkte/B%C3%A4nke_%26_Tische/Parkb%C3%A4nke/"),

                F("hds_b50022", "HDS Stadtmobiliar", "B 50022", "bench",
                  1.80, 0.60, 0.80, true, 3, 52, false, 510,
                  "Steel frame with timber slats",
                  "The same bench with a backrest. Longer dwell time, more comfortable.",
                  "560", "https://www.stadtmobiliar.de/de/produkte/B%C3%A4nke_%26_Tische/Parkb%C3%A4nke/"),

                // ── Table ──
                // A picnic set is a table plus two benches, so its seat count is
                // what both benches hold.
                F("hds_picknickset", "HDS Stadtmobiliar", "Picknickset", "table",
                  2.00, 1.80, 0.75, false, 6, 145, false, 1180,
                  "Steel frame with timber top and seats",
                  "Table with a bench each side. The seat count is both benches together.",
                  "560", "https://www.stadtmobiliar.de/de/produkte/B%C3%A4nke_%26_Tische/"),

                // ── Bins ──
                // Product names and capacities are from ERLAU's own listing;
                // the footprint is typical for a bin of that volume.
                F("erlau_cambio_90", "ERLAU (RUD Erlau AG)", "CAMBIO 90", "bin",
                  0.42, 0.42, 0.95, false, 0, 24, false, 410,
                  "Metal, galvanised steel inner container",
                  "Octagonal bin, 90 litres, all-metal with a stabilising inner frame.",
                  "560", "https://erlau-freiraum.de/abfallbehaelter", litres: 90),

                F("erlau_community_110", "ERLAU (RUD Erlau AG)", "COMMUNITY 110", "bin",
                  0.48, 0.48, 1.00, false, 0, 19, false, 390,
                  "Durapol polyethylene with galvanised inner container",
                  "110 litres — the largest here, for a roof that gets real use.",
                  "560", "https://erlau-freiraum.de/abfallbehaelter", litres: 110),

                F("erlau_vasura_quadri_80", "ERLAU (RUD Erlau AG)", "VASURA QUADRI 80", "bin",
                  0.40, 0.40, 0.92, false, 0, 22, false, 380,
                  "Hot-dip galvanised, optionally powder-coated",
                  "Square bin, 80 litres.",
                  "560", "https://erlau-freiraum.de/abfallbehaelter", litres: 80),

                // ── Bollards ──
                // Height, diameter and weight are from the product page.
                F("poller_konisch_900", "poller24 (Stadtmobiliar)", "Absperrpoller konisch Ø102/76", "bollard",
                  0.102, 0.102, 0.90, true, 0, 7.5, true, 145,
                  "Hot-dip galvanised steel, powder-coated anthracite",
                  "Conical steel bollard, 900 mm above ground.",
                  "560", "https://poller24.de/Poller/"),

                // A light is an electrical installation, which is why it sits in
                // a different cost group from everything else here.
                F("pollerleuchte_led_900", "poller24 (Stadtmobiliar)", "LED-Leuchtpoller Ø102", "light",
                  0.102, 0.102, 0.90, true, 0, 9, false, 420,
                  "Galvanised steel with LED head",
                  "Bollard light for circulation. Marks a route without lighting the sky.",
                  "550", "https://poller24.de/Poller/Leuchtpoller/"),
            };

            bool added = false;
            foreach (var f in wanted)
            {
                if (db.FurnitureItems.Any(x => x.Key == f.Key)) continue;
                db.FurnitureItems.Add(f);
                added = true;
            }
            if (added) db.SaveChanges();
        }

        /// <summary>
        /// Created explicitly, like SportOptions and the price columns:
        /// EnsureCreated() lays the schema down once and never revisits it, so a
        /// table added to the model afterwards is simply absent and the first
        /// query throws.
        /// </summary>
        private static void EnsureTable(ReferenceDbContext db)
        {
            db.Database.ExecuteSqlRaw(@"
                CREATE TABLE IF NOT EXISTS FurnitureItems (
                    Id                   INTEGER NOT NULL CONSTRAINT PK_FurnitureItems PRIMARY KEY AUTOINCREMENT,
                    Key                  TEXT    NOT NULL,
                    Manufacturer         TEXT    NOT NULL,
                    ManufacturerCountry  TEXT    NULL,
                    ProductName          TEXT    NOT NULL,
                    Category             TEXT    NOT NULL,
                    Description          TEXT    NULL,
                    Material             TEXT    NULL,
                    LengthM              REAL    NOT NULL DEFAULT 0,
                    WidthM               REAL    NOT NULL DEFAULT 0,
                    HeightM              REAL    NOT NULL DEFAULT 0,
                    DimensionsPublished  INTEGER NOT NULL DEFAULT 0,
                    Seats                INTEGER NOT NULL DEFAULT 0,
                    WeightKg             REAL    NULL,
                    WeightPublished      INTEGER NOT NULL DEFAULT 0,
                    CapacityLitres       REAL    NULL,
                    PriceValue           REAL    NULL,
                    PriceUnit            TEXT    NULL,
                    PriceSource          TEXT    NULL,
                    PriceIsQuoted        INTEGER NOT NULL DEFAULT 0,
                    CostGroupDin276      TEXT    NULL,
                    SourceUrl            TEXT    NULL,
                    ImageUrl             TEXT    NULL,
                    ImageCredit          TEXT    NULL
                )");

            // And for a database that already has the table from before these
            // two existed — CREATE TABLE IF NOT EXISTS does nothing to it.
            foreach (var col in new[] { "ImageUrl TEXT NULL", "ImageCredit TEXT NULL" })
            {
                try { db.Database.ExecuteSqlRaw($"ALTER TABLE FurnitureItems ADD COLUMN {col}"); }
                catch { /* already there */ }
            }
        }
    }
}
