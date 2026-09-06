using Sportify.Api.Models;

namespace Sportify.Api.Data
{
    /// <summary>
    /// Seeds the reference catalog once against an empty database. Content
    /// is illustrative reference data for a school project (verified against
    /// primary sources for the norms/companies/species named — see the
    /// implementation plan — but not a substitute for the user's own
    /// verification if they lean on it for real sourcing decisions). No
    /// provider website URLs are seeded; we don't guess them.
    /// </summary>
    public static class ReferenceDataSeeder
    {
        public static void Seed(ReferenceDbContext db)
        {
            if (db.Sports.Any()) return;

            // ── Norms ──
            var din18032 = new Norm { Code = "DIN 18032", Authority = "DIN", Title = "Sporthallen – Halls and rooms for sport and multi-purpose use (school/club/general/recreational sport; excludes athletics, climbing, cycling, equestrian, tennis, martial arts)" };
            var fiba = new Norm { Code = "FIBA", Authority = "FIBA", Title = "FIBA Basketball equipment & venue regulations" };
            var ihf = new Norm { Code = "IHF", Authority = "IHF", Title = "IHF Rules of the Game — court & equipment" };
            var fivb = new Norm { Code = "FIVB", Authority = "FIVB", Title = "FIVB Volleyball rules — court & equipment" };
            var bwf = new Norm { Code = "BWF", Authority = "BWF", Title = "BWF Statutes — court & equipment" };
            var fllGuideline = new Norm { Code = "FLL Guideline", Authority = "FLL", Title = "Guideline for the Planning, Execution and Upkeep of Green-Roof Sites (Dachbegrünungsrichtlinie)" };

            // ── Materials ──
            var vinylFlooring = new Material { Name = "Sports vinyl / PVC flooring", Category = "Flooring" };
            var woodFlooring = new Material { Name = "Wood sprung floor / parquet", Category = "Flooring" };
            var puFlooring = new Material { Name = "Poured polyurethane / synthetic rubber flooring", Category = "Flooring" };
            var turf = new Material { Name = "Artificial turf", Category = "Flooring" };
            var elasticUnderlay = new Material { Name = "Elastic underlay", Category = "Subfloor" };

            // ── Providers (real companies, verified — see plan; no website URLs seeded) ──
            var polytan = new Provider { Name = "Polytan GmbH", Country = "Germany", Specialty = "Synthetic sports surfaces & athletics tracks" };
            var bsw = new Provider { Name = "Berleburger Schaumstoffwerk (BSW)", Country = "Germany", Specialty = "Elastic sports flooring & underlays (incl. Regupol)" };
            var gerflor = new Provider { Name = "Gerflor", Country = "France", Specialty = "Modular sports flooring systems" };

            // ── Sports (matching the frontend's own FIELD_SPORTS) ──
            db.Sports.AddRange(
                new Sport { Name = "Polyvalent (multi-sport)", Category = "Indoor court", Norms = { din18032 }, Materials = { vinylFlooring, woodFlooring }, Providers = { polytan, bsw } },
                new Sport { Name = "Basketball", Category = "Indoor court", Norms = { din18032, fiba }, Materials = { vinylFlooring, woodFlooring }, Providers = { polytan, gerflor } },
                new Sport { Name = "Handball", Category = "Indoor court", Norms = { din18032, ihf }, Materials = { vinylFlooring, puFlooring }, Providers = { polytan, bsw } },
                new Sport { Name = "Volleyball", Category = "Indoor court", Norms = { din18032, fivb }, Materials = { vinylFlooring, woodFlooring }, Providers = { gerflor } },
                new Sport { Name = "Badminton", Category = "Indoor court", Norms = { din18032, bwf }, Materials = { vinylFlooring }, Providers = { gerflor, polytan } },
                new Sport { Name = "Football (indoor)", Category = "Indoor court", Norms = { din18032 }, Materials = { turf, vinylFlooring }, Providers = { polytan } }
            );

            // ── Plants (real extensive/intensive green-roof species) ──
            var sedumAcre = new Plant { CommonName = "Biting stonecrop", ScientificName = "Sedum acre", Category = "Groundcover / succulent", SunRequirement = "Full sun", DroughtTolerance = "High" };
            var sedumAlbum = new Plant { CommonName = "White stonecrop", ScientificName = "Sedum album", Category = "Groundcover / succulent", SunRequirement = "Full sun", DroughtTolerance = "High" };
            var sedumSpurium = new Plant { CommonName = "Caucasian stonecrop", ScientificName = "Sedum spurium", Category = "Groundcover / succulent", SunRequirement = "Full sun", DroughtTolerance = "High" };
            var sempervivum = new Plant { CommonName = "Common houseleek", ScientificName = "Sempervivum tectorum", Category = "Groundcover / succulent", SunRequirement = "Full sun", DroughtTolerance = "High" };
            var festuca = new Plant { CommonName = "Sheep's fescue", ScientificName = "Festuca ovina", Category = "Grass", SunRequirement = "Full sun", DroughtTolerance = "Moderate" };
            var achillea = new Plant { CommonName = "Yarrow", ScientificName = "Achillea millefolium", Category = "Perennial herb", SunRequirement = "Full sun", DroughtTolerance = "Moderate–high" };
            var origanum = new Plant { CommonName = "Wild marjoram", ScientificName = "Origanum vulgare", Category = "Perennial herb", SunRequirement = "Full sun", DroughtTolerance = "Moderate" };
            var lavandula = new Plant { CommonName = "Lavender", ScientificName = "Lavandula angustifolia", Category = "Shrub / perennial", SunRequirement = "Full sun", DroughtTolerance = "Moderate" };

            db.PlantPalettes.AddRange(
                new PlantPalette
                {
                    Name = "Extensive Sedum-Moss Roof", Type = "extensive",
                    Description = "Classic low-maintenance, shallow-substrate green roof — the FLL Guideline's standard extensive system.",
                    Plants = { sedumAcre, sedumAlbum, sedumSpurium }, Norms = { fllGuideline },
                },
                new PlantPalette
                {
                    Name = "Extensive Wildflower & Grass Roof", Type = "extensive",
                    Description = "Biodiverse extensive mix on the same shallow substrate depth, favoring pollinators over a pure sedum mat.",
                    Plants = { festuca, achillea, origanum }, Norms = { fllGuideline },
                },
                new PlantPalette
                {
                    Name = "Intensive Roof Garden", Type = "intensive",
                    Description = "Deeper substrate, higher structural load and irrigation needs — a walkable roof garden rather than a maintenance-free mat.",
                    Plants = { lavandula, sempervivum, achillea }, Norms = { fllGuideline },
                }
            );

            db.SaveChanges();
        }
    }
}
