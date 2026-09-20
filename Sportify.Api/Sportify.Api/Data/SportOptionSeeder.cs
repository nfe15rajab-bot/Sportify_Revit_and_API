using Microsoft.EntityFrameworkCore;
using Sportify.Api.Models;

namespace Sportify.Api.Data
{
    /// <summary>
    /// The options padel and basketball offer, moved out of the web app's
    /// JavaScript where they started.
    ///
    /// Idempotent and keyed on sport + group + key, like every other top-up
    /// here: ReferenceDataSeeder bails the moment the catalog exists, and these
    /// arrived later. Adding a fourth surface is now a row, not a code change,
    /// which was the whole point.
    ///
    /// Weights and prices are estimates, flagged as such, same as everywhere.
    /// </summary>
    public static class SportOptionSeeder
    {
        private const string Src = "Estimated German market rate (material + installation), ~2025";

        private static SportOption O(string sport, string group, string key, string label, string? note,
            int order, double? kgM2 = null, double? kgEach = null, double? thicknessMm = null,
            string? hex = null, string? texture = null,
            double? price = null, string? unit = null, string? kg276 = null) =>
            new()
            {
                Sport = sport, OptionGroup = group, Key = key, Label = label, Note = note, SortOrder = order,
                WeightKgM2 = kgM2, WeightKgEach = kgEach, ThicknessMm = thicknessMm,
                ColourHex = hex, TextureHint = texture,
                PriceValue = price, PriceUnit = unit, CostGroupDin276 = kg276,
                PriceSource = price == null ? null : Src, PriceIsQuoted = false,
            };

        /// <summary>
        /// The table itself. ReferenceDbContext is built with EnsureCreated(),
        /// which lays down the schema once and never revisits it — so a table
        /// added to the model after the database exists is simply absent, and
        /// the first query against it throws. Same reason the price columns are
        /// added by hand; deleting the file would fix both and would also throw
        /// away everything a user has entered.
        /// </summary>
        private static void EnsureTable(ReferenceDbContext db)
        {
            db.Database.ExecuteSqlRaw(@"
                CREATE TABLE IF NOT EXISTS SportOptions (
                    Id               INTEGER NOT NULL CONSTRAINT PK_SportOptions PRIMARY KEY AUTOINCREMENT,
                    Sport            TEXT    NOT NULL,
                    OptionGroup      TEXT    NOT NULL,
                    Key              TEXT    NOT NULL,
                    Label            TEXT    NOT NULL,
                    Note             TEXT    NULL,
                    SortOrder        INTEGER NOT NULL DEFAULT 0,
                    WeightKgM2       REAL    NULL,
                    WeightKgEach     REAL    NULL,
                    ThicknessMm      REAL    NULL,
                    ColourHex        TEXT    NULL,
                    TextureHint      TEXT    NULL,
                    PriceValue       REAL    NULL,
                    PriceUnit        TEXT    NULL,
                    PriceSource      TEXT    NULL,
                    PriceIsQuoted    INTEGER NOT NULL DEFAULT 0,
                    CostGroupDin276  TEXT    NULL
                )");
        }

        public static void Seed(ReferenceDbContext db)
        {
            EnsureTable(db);
            var wanted = new[]
            {
                // ── Padel ──
                O("padel", "court_type", "double", "Double — 20 × 10 m", "The standard court.", 0),
                O("padel", "court_type", "single", "Single — 20 × 6 m", "Narrower, for singles play.", 1),

                O("padel", "wall_system", "panoramic", "Panoramic",
                  "Frameless glass ends — no corner posts in the sightline. The premium build.", 0,
                  thicknessMm: 12, price: 210, unit: "EUR/m2", kg276: "530"),
                O("padel", "wall_system", "standard", "Standard framed",
                  "Glass panels in a steel frame. More posts, lower cost.", 1,
                  thicknessMm: 10, price: 150, unit: "EUR/m2", kg276: "530"),

                O("padel", "surface", "artificial_grass", "Artificial grass",
                  "20–25 mm pile with sand infill. The usual choice.", 0,
                  kgM2: 15, texture: "pile", price: 38, unit: "EUR/m2", kg276: "530"),
                O("padel", "surface", "concrete", "Porous concrete",
                  "Hard, fast, low maintenance.", 1,
                  kgM2: 0, texture: "speckle", price: 46, unit: "EUR/m2", kg276: "530"),
                O("padel", "surface", "acrylic", "Acrylic",
                  "Hard court finish over a bound base.", 2,
                  kgM2: 6, texture: "sheen", price: 52, unit: "EUR/m2", kg276: "530"),

                O("padel", "surface_colour", "blue", "Blue", null, 0, hex: "#2f6fb5"),
                O("padel", "surface_colour", "green", "Green", null, 1, hex: "#3f8f52"),
                O("padel", "surface_colour", "terracotta", "Terracotta", null, 2, hex: "#b5613a"),

                // ── Basketball ──
                O("basketball", "hoops", "two", "Two — full court", "A basket at each end.", 0),
                O("basketball", "hoops", "one", "One — half court", "The usual choice where space is short.", 1),

                // On a roof the ballast IS the fixing — there is nothing to bolt
                // into without penetrating the waterproofing. One row, so the
                // panel shows no picker: a choice of one is not a choice, and
                // offering "bolted" invited a court that cannot be built.
                O("basketball", "basket", "ballasted", "Ballasted, freestanding",
                  "No fixing to the deck. The ballast is the weight.", 0,
                  kgEach: 900, price: 4200, unit: "EUR/each", kg276: "560"),

                O("basketball", "surface", "acrylic", "Acrylic hard court",
                  "Painted acrylic over a bound base. The outdoor default.", 0,
                  kgM2: 6, texture: "flat", price: 52, unit: "EUR/m2", kg276: "530"),
                O("basketball", "surface", "polyurethane", "Poured polyurethane",
                  "Seamless, more forgiving underfoot, more expensive.", 1,
                  kgM2: 8, texture: "sheen", price: 70, unit: "EUR/m2", kg276: "530"),
                O("basketball", "surface", "tiles", "Modular tiles",
                  "Clipped polypropylene. Drains, and lifts for access.", 2,
                  kgM2: 5, texture: "tiles", price: 44, unit: "EUR/m2", kg276: "530"),

                O("basketball", "court_colour", "blue", "Blue", null, 0, hex: "#2f6fb5"),
                O("basketball", "court_colour", "green", "Green", null, 1, hex: "#3f8f52"),
                O("basketball", "court_colour", "terracotta", "Terracotta", null, 2, hex: "#b5613a"),
                O("basketball", "court_colour", "grey", "Grey", null, 3, hex: "#6d737c"),


            };

            bool added = false;
            foreach (var o in wanted)
            {
                if (db.SportOptions.Any(x => x.Sport == o.Sport && x.OptionGroup == o.OptionGroup && x.Key == o.Key))
                    continue;
                db.SportOptions.Add(o);
                added = true;
            }
            if (added) db.SaveChanges();
        }
    }
}
