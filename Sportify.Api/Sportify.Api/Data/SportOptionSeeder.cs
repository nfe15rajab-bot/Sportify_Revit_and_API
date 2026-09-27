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



                // ── Volleyball ──
                // Play type decides the court and its markings. Beach is not a
                // variant of indoor: it is 16 x 8 with no attack lines, and it
                // is laid on sand, which is the whole story on a roof.
                O("volleyball", "play_type", "indoor", "Indoor — 18 × 9 m",
                  "Attack lines 3 m from the net. A hard or synthetic surface.", 0),
                O("volleyball", "play_type", "beach", "Beach — 16 × 8 m",
                  "No attack lines. Laid on sand, which the deck has to carry.", 1),

                // Net height is a real choice, not a detail — it is what makes
                // the court a men's, women's or junior court.
                O("volleyball", "net_height", "men", "Men — 2.43 m", "FIVB senior men.", 0, thicknessMm: 2430),
                O("volleyball", "net_height", "women", "Women — 2.24 m", "FIVB senior women.", 1, thicknessMm: 2240),
                O("volleyball", "net_height", "junior", "Junior / mixed — 2.35 m",
                  "Between the two. Federations vary; confirm against the local rule.", 2, thicknessMm: 2350),

                // ── Football ──────────────────────────────────────────────
                // The court type carries the size: a futsal court is 40 x 20 m
                // because FIFA says so, not because someone picked "standard".
                // That is why football has no size variant — the type IS the
                // size, and two controls saying the same thing can disagree.
                O("football", "court_type", "futsal", "Futsal — 40 x 20 m",
                  "FIFA Futsal, international size. Goals 3 x 2 m.", 0),
                O("football", "court_type", "small_sided", "Small-sided — 30 x 20 m",
                  "Five-a-side. Inside FIFA Futsal's non-international range (25-42 x 16-25 m).", 1),
                O("football", "court_type", "mini", "Mini pitch — 22 x 14 m",
                  "A Bolzplatz with rebound boards and 2 x 1 m goals. The smallest that still plays.", 2),

                // Turf is the one that matters on a roof. A 3G carpet is light;
                // the sand and rubber INFILL that makes it play is not, and it
                // is the infill that the deck carries. Said as one figure here
                // because that is what gets built, but it is why turf weighs
                // three times what a poured surface does.
                O("football", "surface", "artificial_turf", "Artificial turf, 3G",
                  "Carpet with sand and rubber infill. Plays like grass; the infill is most of the weight.", 0,
                  kgM2: 22, texture: "pile", price: 48, unit: "EUR/m2", kg276: "530"),
                O("football", "surface", "needle_punch", "Needle-punch turf",
                  "Unfilled carpet. A third of the weight, harder underfoot.", 1,
                  kgM2: 4, texture: "pile", price: 34, unit: "EUR/m2", kg276: "530"),
                O("football", "surface", "polyurethane", "Poured polyurethane",
                  "Seamless and fast. Reads as a hard court rather than a pitch.", 2,
                  kgM2: 8, texture: "sheen", price: 70, unit: "EUR/m2", kg276: "530"),
                O("football", "surface", "tiles", "Modular tiles",
                  "Clipped polypropylene. Drains, and lifts for access.", 3,
                  kgM2: 5, texture: "tiles", price: 44, unit: "EUR/m2", kg276: "530"),

                O("football", "court_colour", "green", "Green", "The one people expect of a pitch.", 0, hex: "#3f7d3a"),
                O("football", "court_colour", "blue", "Blue", "Reads as a court rather than a lawn.", 1, hex: "#2f6fb5"),
                O("football", "court_colour", "terracotta", "Terracotta", "Warmer, and it hides wear.", 2, hex: "#a8553a"),

                // Same reasoning as the basketball basket: on a roof the
                // ballast IS the fixing, because there is nothing to bolt into
                // that does not go through the waterproofing.
                O("football", "goals", "ballasted", "Ballasted, freestanding",
                  "No fixing to the deck. Counted as a pair.", 0,
                  kgEach: 320, price: 1900, unit: "EUR/each", kg276: "560"),

                O("football", "boards", "none", "No rebound boards", "Ball out of play at the touchline.", 0),
                O("football", "boards", "low", "Low rebound boards, 1 m",
                  "Keeps the ball in. Standard on a mini pitch, optional on the rest.", 1,
                  kgEach: 45, price: 190, unit: "EUR/each", kg276: "560"),

                O("volleyball", "surface", "polyurethane", "Poured polyurethane",
                  "Seamless and forgiving — the indoor default outdoors too.", 0,
                  kgM2: 8, texture: "sheen", price: 70, unit: "EUR/m2", kg276: "530"),
                O("volleyball", "surface", "acrylic", "Acrylic hard court",
                  "Painted acrylic over a bound base.", 1,
                  kgM2: 6, texture: "flat", price: 52, unit: "EUR/m2", kg276: "530"),
                O("volleyball", "surface", "tiles", "Modular tiles",
                  "Clipped polypropylene. Drains, and lifts for access.", 2,
                  kgM2: 5, texture: "tiles", price: 44, unit: "EUR/m2", kg276: "530"),
                // Sand is the reason a beach court is a structural question
                // rather than a surface choice. 400 mm is the FIVB minimum, and
                // washed silica runs about 1,600 kg/m3 dry — so this row alone
                // is roughly 640 kg per square metre.
                O("volleyball", "surface", "sand", "Beach sand, 400 mm",
                  "FIVB minimum depth. Washed, rounded silica — and about 640 kg per m2.", 3,
                  kgM2: 640, texture: "speckle", price: 58, unit: "EUR/m2", kg276: "570"),

                O("volleyball", "court_colour", "blue", "Blue", null, 0, hex: "#2f6fb5"),
                O("volleyball", "court_colour", "green", "Green", null, 1, hex: "#3f8f52"),
                O("volleyball", "court_colour", "terracotta", "Terracotta", null, 2, hex: "#b5613a"),
                O("volleyball", "court_colour", "sand", "Sand", null, 3, hex: "#d8c193"),

                // Posts and net, as one counted item. Free-standing with
                // ballast, for the same reason a basket is: you cannot socket a
                // post into a roof deck.
                O("volleyball", "net_system", "ballasted", "Ballasted posts and net",
                  "Free-standing. No sockets cut into the deck.", 0,
                  kgEach: 340, price: 2800, unit: "EUR/each", kg276: "560"),
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
