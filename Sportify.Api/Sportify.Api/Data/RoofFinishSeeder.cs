using Sportify.Api.Models;

namespace Sportify.Api.Data
{
    /// <summary>
    /// Build-ups for the roof itself — the surface everything else sits in.
    ///
    /// The catalog had green roof systems and one paved walkway, which was
    /// enough while the only ground being drawn was planted. But a roof is not
    /// planted edge to edge: what is left between the courts and the beds is
    /// the circulation, and it is made of something. That leftover needs a real
    /// build-up for the same three reasons a green zone does — it has a cost,
    /// it has a weight the deck carries, and it becomes a floor in Revit.
    ///
    /// Category "finish" marks a system meant for that leftover rather than for
    /// a planted zone. The existing paved walkway qualifies too and is
    /// re-categorised here.
    ///
    /// Idempotent, like the price backfill: ReferenceDataSeeder bails the
    /// moment the catalog exists, and these arrived later.
    /// </summary>
    public static class RoofFinishSeeder
    {
        private static RoofAssembly Finish(
            string key, string provider, string system, string description,
            params (string name, string fn, double mm, string src)[] layers)
        {
            var a = new RoofAssembly
            {
                Key = key, Provider = provider, ProviderCountry = "Germany",
                SystemName = system, Category = "finish", Description = description,
            };
            for (int i = 0; i < layers.Length; i++)
            {
                a.Layers.Add(new RoofAssemblyLayer
                {
                    LayerOrder = i, Name = layers[i].name, Function = layers[i].fn,
                    ThicknessMm = layers[i].mm, ThicknessSource = layers[i].src,
                });
            }
            return a;
        }

        public static void Seed(ReferenceDbContext db)
        {
            // The paved walkway was catalogued before there was such a thing as
            // a roof finish. It is one, so it should appear as one.
            var walkway = db.RoofAssemblies.FirstOrDefault(a => a.Key == "zinco_paved_walkway");
            if (walkway != null && walkway.Category == "walkway")
            {
                walkway.Category = "finish";
                db.SaveChanges();
            }

            var wanted = new[]
            {
                Finish("timber_deck_pedestals", "Generic", "Timber Deck on Pedestals",
                    "Hardwood decking on adjustable pedestals. Drains beneath, lifts off for access.",
                    ("Hardwood deck boards", "wearing", 25, "typical"),
                    ("Aluminium joists", "bedding", 60, "typical"),
                    ("Adjustable pedestals", "bedding", 50, "typical"),
                    ("Protection mat", "protection", 5, "typical")),

                Finish("gravel_ballast", "Generic", "Washed Gravel Ballast",
                    "Loose ballast. The cheapest way to hold a membrane down, but it is not a walking surface.",
                    ("Washed round gravel 16/32", "wearing", 50, "typical"),
                    ("Filter fleece", "filter", 2, "typical"),
                    ("Protection mat", "protection", 5, "typical")),

                Finish("resin_bound_paving", "Generic", "Resin-Bound Mineral Surfacing",
                    "Seamless bound aggregate. Water-permeable, level, and comfortable to walk on.",
                    ("Resin-bound aggregate", "wearing", 18, "typical"),
                    ("Permeable base course", "bedding", 40, "typical"),
                    ("Protection mat", "protection", 5, "typical")),
            };

            foreach (var a in wanted)
            {
                if (db.RoofAssemblies.Any(x => x.Key == a.Key)) continue;
                db.RoofAssemblies.Add(a);
            }
            db.SaveChanges();
        }
    }
}
