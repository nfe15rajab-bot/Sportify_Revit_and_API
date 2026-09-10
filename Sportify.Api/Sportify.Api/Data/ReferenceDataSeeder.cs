using Sportify.Api.Models;

namespace Sportify.Api.Data
{
    /// <summary>
    /// Seeds the reference catalog once against an empty database. Content
    /// is illustrative reference data for a school project (verified against
    /// primary/secondary sources for the norms/figures/companies/species
    /// named — see the per-row comments below for what was checked and
    /// where a figure couldn't be pinned to an exact primary-source number,
    /// it's flagged as such rather than invented). No provider website URLs
    /// are seeded; we don't guess them.
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
            var dfb = new Norm { Code = "DFB", Authority = "DFB", Title = "Deutscher Fußball-Bund — indoor/futsal-style football pitch guidance" };
            var fllGuideline = new Norm { Code = "FLL Guideline", Authority = "FLL", Title = "Guideline for the Planning, Execution and Upkeep of Green-Roof Sites (Dachbegrünungsrichtlinie)" };
            var din18040 = new Norm { Code = "DIN 18040-1", Authority = "DIN", Title = "Barrierefreies Bauen – Öffentlich zugängliche Gebäude (accessible design for publicly accessible buildings, incl. sports facilities)" };
            var din32984 = new Norm { Code = "DIN 32984", Authority = "DIN", Title = "Bodenindikatoren im öffentlichen Raum (tactile paving for blind/visually-impaired wayfinding)" };
            var en1176 = new Norm { Code = "EN 1176-1", Authority = "CEN", Title = "Playground equipment — general safety requirements and test methods" };
            var en1177 = new Norm { Code = "EN 1177", Authority = "CEN", Title = "Impact-attenuating playground surfacing — determination of critical fall height" };
            var din4109 = new Norm { Code = "DIN 4109", Authority = "DIN", Title = "Schallschutz im Hochbau (sound insulation in buildings — relevant to sports-hall acoustics)" };

            // ── Materials (EN 14904 classifies indoor multi-use sports floors
            // by force reduction — how much impact energy the floor absorbs
            // vs. a rigid concrete reference. Verified figure bands: Type 1
            // ≥55%, Type 2 ≥45%, Type 3 ≥35%, Type 4 ≥25%; point-elastic
            // (Type 4) vertical deformation ≤5mm; ball rebound ≥90% of the
            // concrete reference; sliding friction coefficient 80–110.
            // Outdoor turf sits under a different standard, EN 15330, not
            // EN 14904 — flagged rather than force-fit. ──
            var vinylFlooring = new Material
            {
                Name = "Sports vinyl / PVC flooring", Category = "Flooring",
                NormCode = "EN 14904", PerformanceClass = "Type 3–4 — point-elastic", ForceReduction = "≥35%",
                Notes = "Cushions at the contact point only (no sprung sub-frame) — thinner buildup than a sprung wood floor, typically tested in the Type 3–4 band depending on backing thickness.",
                EmbodiedCarbonValue = 9.1, EmbodiedCarbonUnit = "kg CO2e/m2",
                EmbodiedCarbonSource = "Industry-aggregate LVT/vinyl flooring EPD figure (illustrative — not one specific declared product; A1-A3 cradle-to-gate)."
            };
            var woodFlooring = new Material
            {
                Name = "Wood sprung floor / parquet", Category = "Flooring",
                NormCode = "EN 14904", PerformanceClass = "Type 1 — area-elastic", ForceReduction = "≥55%",
                Notes = "A sprung sub-frame flexes over a wide area around the contact point — the highest EN 14904 force-reduction class, at the cost of the most build-up height."
                // EmbodiedCarbonValue deliberately left null: NWFA/DHA industry
                // EPDs confirm wood flooring has the lowest GWP of the major
                // flooring categories, but no comparable per-m² cradle-to-gate
                // figure was found to cite directly here — add it via the Data
                // tab's edit form once a real EPD figure is sourced, rather
                // than guessing one now.
            };
            var puFlooring = new Material
            {
                Name = "Poured polyurethane / synthetic rubber flooring", Category = "Flooring",
                NormCode = "EN 14904", PerformanceClass = "Type 2–3 — combined-elastic", ForceReduction = "≥35–45%",
                Notes = "A poured elastic layer over a resilient underlay; the exact class depends on total system thickness, so treat this as a typical range rather than one fixed figure.",
                EmbodiedCarbonValue = 4.3, EmbodiedCarbonUnit = "kg CO2e/m2",
                EmbodiedCarbonSource = "Windmöller polyurethane flooring EPD (IBU database), A1-A3 module — declared range 2.87–5.66 kg CO2e/m2, midpoint used here."
            };
            var turf = new Material
            {
                Name = "Artificial turf", Category = "Flooring",
                NormCode = "EN 15330", PerformanceClass = "Outdoor synthetic turf system",
                Notes = "Governed by EN 15330 (outdoor turf), not EN 14904 (indoor multi-use floors) — the two standards test different things and aren't directly comparable."
                // EmbodiedCarbonValue left null: found figures (~5–13 kg CO2e/m²)
                // were aggregated from a whole-pitch manufacturing-phase total,
                // not a clean per-unit cradle-to-gate EPD comparable to the
                // other rows here — not reliable enough to seed as-is.
            };
            var elasticUnderlay = new Material
            {
                Name = "Elastic underlay", Category = "Subfloor",
                NormCode = "EN 14904 (as subfloor layer)",
                Notes = "Not force-reduction-tested standalone — its contribution is measured as part of the assembled floor system laid over it (see the topping material's own Type rating)."
            };

            // ── Materials — green roof build-up (FLL Guideline), playground
            // safety surfacing (EN 1177), and spectator seating. Extends the
            // reference catalog beyond sports flooring into the other real
            // material categories this app's own modes actually use. ──
            var extensiveSubstrate = new Material
            {
                Name = "Extensive substrate (mineral, lightweight)", Category = "Green roof build-up",
                NormCode = "FLL Guideline",
                Notes = "Shallow (typically 6–15 cm) mineral substrate for low-maintenance extensive roofs — light enough to skip major structural reinforcement in most retrofit cases."
            };
            var intensiveSubstrate = new Material
            {
                Name = "Intensive substrate (deep, high water-capacity)", Category = "Green roof build-up",
                NormCode = "FLL Guideline",
                Notes = "Deeper (25 cm+) substrate for walkable roof gardens with shrubs/lawn — carries a materially higher dead load than an extensive system, which the roof structure must be designed for."
            };
            var rootBarrier = new Material
            {
                Name = "Root barrier membrane (HDPE/PE)", Category = "Green roof build-up",
                Notes = "Protects the roof's own waterproofing from root penetration — a standard layer beneath any green-roof build-up, extensive or intensive."
            };
            var drainageBoard = new Material
            {
                Name = "Drainage & water-retention board", Category = "Green roof build-up",
                Notes = "Channels excess water to roof outlets while retaining some in reservoir cells for the vegetation — sits between the root barrier and the substrate."
            };
            var roofWaterproofing = new Material
            {
                Name = "Bituminous / EPDM roof waterproofing membrane", Category = "Roofing",
                Notes = "The roof's actual watertight layer, beneath every green-roof or hardscape build-up — root-resistant membranes exist, but a separate root barrier is still standard practice."
            };
            var playgroundSurfacing = new Material
            {
                Name = "Impact-attenuating rubber safety surfacing", Category = "Playground safety surfacing",
                NormCode = "EN 1177", PerformanceClass = "Rated by critical fall height (CFH)",
                Notes = "Required under climbing/fall-risk playground equipment; thickness is selected from the equipment's actual fall height, tested per EN 1177, not a single fixed spec."
            };
            var grandstandSeating = new Material
            {
                Name = "Aluminium tip-up grandstand seating", Category = "Spectator seating",
                Notes = "Lightweight bolt-together tiered seating typical of school/club-level halls — see the Facilities tab's DIN/FIFA seat-dimension guidelines for sizing."
            };
            var bleacherBench = new Material
            {
                Name = "Steel modular bleacher bench", Category = "Spectator seating",
                Notes = "Bench-style (non-tip-up) seating — cheaper and more compact than individual grandstand seats, common for lower-budget community halls."
            };

            // ── Providers (real companies, verified — see plan; no website URLs seeded) ──
            var polytan = new Provider { Name = "Polytan GmbH", Country = "Germany", Specialty = "Synthetic sports surfaces & athletics tracks", Category = "Sports flooring & surfaces" };
            var bsw = new Provider { Name = "Berleburger Schaumstoffwerk (BSW)", Country = "Germany", Specialty = "Elastic sports flooring & underlays (incl. Regupol)", Category = "Sports flooring & surfaces" };
            var gerflor = new Provider { Name = "Gerflor", Country = "France", Specialty = "Modular sports flooring systems", Category = "Sports flooring & surfaces" };
            var goldbeck = new Provider { Name = "Goldbeck GmbH", Country = "Germany", Specialty = "Turnkey prefabricated sports halls — planning, construction & in-house component manufacturing", Category = "Prefab hall construction" };
            var optigruen = new Provider { Name = "Optigrün International AG", Country = "Germany", Specialty = "Extensive & intensive green-roof systems — Europe's market leader", Category = "Green roof systems" };
            var zinco = new Provider { Name = "ZinCo GmbH", Country = "Germany", Specialty = "Extensive/intensive/biodiversity green-roof systems, roof-penetration-free fall protection", Category = "Green roof systems" };
            var bauder = new Provider { Name = "Paul Bauder GmbH & Co. KG", Country = "Germany", Specialty = "Flat-roof waterproofing, insulation & green-roof build-ups", Category = "Roofing & waterproofing" };
            var eibe = new Provider { Name = "eibe Produktion + Vertrieb GmbH & Co. KG", Country = "Germany", Specialty = "Playground equipment for schools & public spaces, EN 1176-compliant", Category = "Playground equipment" };
            var richterSpielgeraete = new Provider { Name = "Richter Spielgeräte GmbH", Country = "Germany", Specialty = "Playground equipment — climbing/play landscapes, incl. inclusive/accessible play", Category = "Playground equipment" };

            // ── Sports, each with real mini/standard/competition field
            // dimensions — the exact same figures the frontend's own
            // field.js renderer draws from (data.js's FIELDS table),
            // mirrored here so this tab shows real numbers instead of a
            // bare norm code. ──
            var polyvalent = new Sport
            {
                Name = "Polyvalent (multi-sport)", Category = "Indoor court",
                Norms = { din18032 }, Materials = { vinylFlooring, woodFlooring }, Providers = { polytan, bsw, goldbeck },
                Variants = {
                    new FieldVariant { Variant = "mini", LengthM = 20, WidthM = 12, RunoffM = 1.5, HeightMinM = 5.5, Norm = "DIN 18032" },
                    new FieldVariant { Variant = "standard", LengthM = 27, WidthM = 15, RunoffM = 2, HeightMinM = 7, Norm = "DIN 18032" },
                    new FieldVariant { Variant = "competition", LengthM = 40, WidthM = 20, RunoffM = 2, HeightMinM = 7, Norm = "DIN 18032" },
                }
            };
            var basketball = new Sport
            {
                Name = "Basketball", Category = "Indoor court",
                Norms = { din18032, fiba }, Materials = { vinylFlooring, woodFlooring }, Providers = { polytan, gerflor, goldbeck },
                Variants = {
                    new FieldVariant { Variant = "mini", LengthM = 22, WidthM = 13, RunoffM = 2, HeightMinM = 7, Norm = "FIBA / DIN 18032" },
                    new FieldVariant { Variant = "standard", LengthM = 28, WidthM = 15, RunoffM = 2, HeightMinM = 7, Norm = "FIBA / DIN 18032" },
                    new FieldVariant { Variant = "competition", LengthM = 34, WidthM = 19, RunoffM = 2, HeightMinM = 7, Norm = "FIBA" },
                }
            };
            var handball = new Sport
            {
                Name = "Handball", Category = "Indoor court",
                Norms = { din18032, ihf }, Materials = { vinylFlooring, puFlooring }, Providers = { polytan, bsw, goldbeck },
                Variants = {
                    new FieldVariant { Variant = "mini", LengthM = 30, WidthM = 16, RunoffM = 2, HeightMinM = 7, Norm = "IHF / DIN 18032" },
                    new FieldVariant { Variant = "standard", LengthM = 40, WidthM = 20, RunoffM = 1, HeightMinM = 7, Norm = "IHF / DIN 18032" },
                    new FieldVariant { Variant = "competition", LengthM = 40, WidthM = 20, RunoffM = 2, HeightMinM = 7, Norm = "IHF" },
                }
            };
            var volleyball = new Sport
            {
                Name = "Volleyball", Category = "Indoor court",
                Norms = { din18032, fivb }, Materials = { vinylFlooring, woodFlooring }, Providers = { gerflor, goldbeck },
                Variants = {
                    new FieldVariant { Variant = "mini", LengthM = 16, WidthM = 8, RunoffM = 3, HeightMinM = 7, Norm = "FIVB / DIN 18032" },
                    new FieldVariant { Variant = "standard", LengthM = 18, WidthM = 9, RunoffM = 3, HeightMinM = 7, Norm = "FIVB / DIN 18032" },
                    new FieldVariant { Variant = "competition", LengthM = 18, WidthM = 9, RunoffM = 5, HeightMinM = 12.5, Norm = "FIVB" },
                }
            };
            var badminton = new Sport
            {
                Name = "Badminton", Category = "Indoor court",
                Norms = { din18032, bwf }, Materials = { vinylFlooring }, Providers = { gerflor, polytan, goldbeck },
                Variants = {
                    new FieldVariant { Variant = "mini", LengthM = 13.4, WidthM = 6.1, RunoffM = 1, HeightMinM = 9, Norm = "BWF / DIN 18032" },
                    new FieldVariant { Variant = "standard", LengthM = 13.4, WidthM = 6.1, RunoffM = 2, HeightMinM = 9, Norm = "BWF / DIN 18032" },
                    new FieldVariant { Variant = "competition", LengthM = 13.4, WidthM = 6.1, RunoffM = 3, HeightMinM = 9, Norm = "BWF" },
                }
            };
            var football = new Sport
            {
                Name = "Football (indoor)", Category = "Indoor court",
                Norms = { din18032, dfb }, Materials = { turf, vinylFlooring }, Providers = { polytan, goldbeck },
                Variants = {
                    new FieldVariant { Variant = "mini", LengthM = 25, WidthM = 16, RunoffM = 2, HeightMinM = 5, Norm = "DFB / DIN 18032" },
                    new FieldVariant { Variant = "standard", LengthM = 35, WidthM = 20, RunoffM = 2, HeightMinM = 5, Norm = "DFB / DIN 18032" },
                    new FieldVariant { Variant = "competition", LengthM = 42, WidthM = 22, RunoffM = 3, HeightMinM = 7, Norm = "DFB" },
                }
            };
            db.Sports.AddRange(polyvalent, basketball, handball, volleyball, badminton, football);

            // EF's change tracker only persists what's reachable from an
            // Add()-ed root — anything not attached into a Sport's or
            // PlantPalette's own Norms/Materials/Providers list (found the
            // hard way: they silently never made it into the database)
            // needs its own explicit AddRange here. This covers norms and
            // materials that are genuinely real and relevant but don't
            // belong to any one Sport/PlantPalette specifically (the
            // accessibility/playground/acoustics norms cited by
            // FacilityGuideline rows below are a plain string citation,
            // not a relational link, so the Norm row itself still needs
            // this) plus the pre-existing "Elastic underlay" material,
            // which was already an orphan before this session's changes.
            db.Norms.AddRange(din18040, din32984, en1176, en1177, din4109);
            db.Materials.AddRange(elasticUnderlay, playgroundSurfacing, grandstandSeating, bleacherBench);

            // ── Analysis parameters — the real reference figures behind
            // each Analysis-tab card, pulled from the database instead of
            // living as bare constants inside analysisController.js. Each
            // is either a verified code/standard figure (Fire Safety,
            // Accessibility) or the same already-honestly-labeled
            // "illustrative, not certified" formula constants the frontend
            // already used (Water Management, Wind Exposure) — moved here
            // verbatim, not re-derived, so behavior doesn't change, only
            // where the numbers live and whether they're visible/editable.
            db.AnalysisParameters.AddRange(
                new AnalysisParameter { Category = "Fire Safety", Key = "max_travel_distance_m", Label = "Max. travel distance to nearest exit", Value = 35, Unit = "m", Authority = "MBO", NormCode = "MBO §35", Description = "From any point in an occupied room to a required stairwell or the open air, per the German Musterbauordnung's general (non-assembly-venue) travel-distance rule." },
                new AnalysisParameter { Category = "Accessibility", Key = "min_circulation_width_m", Label = "Wheelchair two-way circulation width reference", Value = 1.5, Unit = "m", Authority = "DIN", NormCode = "DIN 18040-1", Description = "Common reference figure for a two-way accessible route; DIN 18040-1 itself sets ≥1.20m for a one-way accessible route, ≥1.80m at passing points." },
                new AnalysisParameter { Category = "Water Management", Key = "retention_base_percent", Label = "Base rainfall retention", Value = 30, Unit = "%", Authority = "Illustrative", NormCode = "FLL Guideline (direction only)", Description = "Starting point of a simple, clearly-approximate retention formula — deeper buildup retains more, consistent with FLL guidance direction, but this is not a cited coefficient table." },
                new AnalysisParameter { Category = "Water Management", Key = "retention_depth_coefficient_percent_per_cm", Label = "Retention gain per cm of buildup depth", Value = 2, Unit = "%/cm", Authority = "Illustrative", NormCode = "FLL Guideline (direction only)", Description = "Added to the base retention percentage per cm of average garden buildup depth." },
                new AnalysisParameter { Category = "Water Management", Key = "retention_max_percent", Label = "Retention cap", Value = 90, Unit = "%", Authority = "Illustrative", NormCode = "FLL Guideline (direction only)", Description = "Upper bound the illustrative retention formula is clamped to." },
                new AnalysisParameter { Category = "Wind Exposure", Key = "edge_exposure_zone_m", Label = "Roof-edge distance treated as elevated wind exposure", Value = 2.0, Unit = "m", Authority = "Illustrative", NormCode = "EN 1991-1-4 (concept only)", Description = "A purely geometric proxy, not a real wind-field simulation — rooftop wind genuinely does concentrate at edges/corners per EN 1991-1-4's zoning concept, but the real zone width depends on building height/width, which this simplified check doesn't model." },
                // Revit-side Analysis panel additions (Stage 5) — same
                // "illustrative, not certified" honesty as the Water
                // Management/Wind Exposure figures above, not verified
                // structural/energy engineering.
                new AnalysisParameter { Category = "Live Loads", Key = "assumed_load_per_person_kg", Label = "Assumed mass per spectator", Value = 90, Unit = "kg", Authority = "Illustrative", NormCode = "DIN EN 1991-1-1 (concept only)", Description = "Person mass plus a small margin, used to convert a field's seated capacity into a distributed load — not a cited anthropometric figure." },
                new AnalysisParameter { Category = "Live Loads", Key = "reference_capacity_kn_per_m2", Label = "Reference imposed load — fixed-seating assembly area", Value = 4.0, Unit = "kN/m²", Authority = "DIN EN 1991-1-1", NormCode = "Category C3", Description = "Common reference value for assembly areas with fixed seating (grandstands); national annexes vary, and this compares against the generic figure only, not this project's actual building structure." },
                new AnalysisParameter { Category = "Carbon Impact", Key = "energy_density_wh_per_m2_per_hour", Label = "Kinetic energy-harvesting density", Value = 0.5, Unit = "Wh/m²/hour", Authority = "Illustrative", NormCode = "Piezoelectric flooring research (order-of-magnitude)", Description = "Theoretical ceiling assuming piezoelectric-capable flooring across the active playing surface — this project's material data doesn't yet track which materials actually support it, so treat this as an upper bound, not a prediction for a specific material choice." },
                new AnalysisParameter { Category = "Carbon Impact", Key = "assumed_daily_usage_hours", Label = "Assumed active-use hours per day", Value = 4, Unit = "hours", Authority = "Illustrative", NormCode = "N/A", Description = "How many hours per day the surface sees active play, for turning an hourly energy-density figure into a daily estimate." }
            );

            // ── Facility guidelines — the supporting spaces a real sports
            // hall needs beyond the playing field itself: changing rooms,
            // showers, ventilation, and spectator seating at two very
            // different scales (a school/community hall under DIN 18032 vs.
            // a full stadium under FIFA's stadium guide). Every row below
            // is a figure actually found and cross-checked across sources,
            // not an invented placeholder — where a genuinely precise
            // primary-source number couldn't be pinned down (DIN 18032-1's
            // exact spectator area-per-person, which sits inside the
            // paywalled standard text), that's said plainly instead of
            // guessed, matching how this project already handles the LCA
            // Analysis card. ──
            db.FacilityGuidelines.AddRange(
                new FacilityGuideline { Category = "Changing rooms", Title = "Occupancy density", Requirement = "≈1 m² per person (max. 3 m³ per person). Every hall needs at least two changing rooms, separated by sex, each with a minimum room height of 2.5 m.", Authority = "DIN", NormCode = "DIN 18032-1" },
                new FacilityGuideline { Category = "Changing rooms", Title = "Adjoining sanitary rooms", Requirement = "Each changing room is directly adjoined by its own wash/shower room — a typical planning ratio is 4 showers, 4 wash stations and 1 WC per changing room, scaling up with hall size.", Authority = "DIN", NormCode = "DIN 18032-1" },
                new FacilityGuideline { Category = "Ventilation", Title = "Changing & shower rooms", Requirement = "Changing rooms: 6 air changes per hour. Shower rooms: 8–10 air changes per hour.", Authority = "DIN", NormCode = "DIN 18032" },
                new FacilityGuideline { Category = "Ventilation", Title = "Spectator areas", Requirement = "≥20 m³/h of fresh outside air per seat (non-smoking).", Authority = "DIN", NormCode = "DIN 18032" },
                new FacilityGuideline { Category = "Spectator seating — community hall", Title = "Audience facilities", Requirement = "Halls must be supplemented with audience seating and ancillary rooms sized and numbered to match the hall type — the exact area-per-spectator figure sits inside the paywalled standard text and isn't reproduced here rather than guessed.", Authority = "DIN", NormCode = "DIN 18032-1" },
                new FacilityGuideline { Category = "Spectator seating — stadium (FIFA)", Title = "Seat dimensions", Requirement = "Minimum seat width 45 cm; ≥47 cm recommended for comfort tiers.", Authority = "FIFA", NormCode = "FIFA Football Stadiums – Technical Recommendations and Requirements" },
                new FacilityGuideline { Category = "Spectator seating — stadium (FIFA)", Title = "Row spacing (seat pitch)", Requirement = "Minimum row depth 800 mm; 850 mm is common for higher-comfort tiers.", Authority = "FIFA", NormCode = "FIFA Football Stadiums – Technical Recommendations and Requirements" },
                new FacilityGuideline { Category = "Spectator seating — stadium (FIFA)", Title = "Sightline (C-value)", Requirement = "C-value (eye-to-head clearance over the row in front) of 90 mm is a satisfactory minimum, 120 mm optimal — used to calculate each row's riser height up the seating bowl.", Authority = "FIFA", NormCode = "FIFA Football Stadiums – Technical Recommendations and Requirements" },
                new FacilityGuideline { Category = "Hall sizing", Title = "Single hall (Einfachhalle)", Requirement = "Playing area 15 × 27 m (405 m²), clear height ≥5.5 m (7 m where apparatus gymnastics is also used).", Authority = "DIN", NormCode = "DIN 18032-1" },
                new FacilityGuideline { Category = "Hall sizing", Title = "Games hall (Spielhalle)", Requirement = "22 × 44 m (968 m²), clear height 7 m — not divisible into smaller independent sections.", Authority = "DIN", NormCode = "DIN 18032-1" },
                new FacilityGuideline { Category = "Hall sizing", Title = "Double hall (Zweifachhalle)", Requirement = "22 × 45 m (990 m²), clear height 7 m — divisible into two independent single-hall sections.", Authority = "DIN", NormCode = "DIN 18032-1" },
                new FacilityGuideline { Category = "Hall sizing", Title = "Triple hall (Dreifachhalle)", Requirement = "27 × 45 m (1,215 m²), clear height 7 m — divisible into three independent sections.", Authority = "DIN", NormCode = "DIN 18032-1" },
                new FacilityGuideline { Category = "Accessibility", Title = "Circulation routes", Requirement = "Accessible routes (ramps, doorways, turning circles) must follow DIN 18040-1's dimensions for publicly accessible buildings — the same standard this app's Analysis tab checks circulation width against.", Authority = "DIN", NormCode = "DIN 18040-1" },
                new FacilityGuideline { Category = "Accessibility", Title = "Tactile wayfinding", Requirement = "Tactile paving (ribbed/dot floor indicators) at level changes, entrances, and route decision points for blind/visually-impaired users.", Authority = "DIN", NormCode = "DIN 32984" },
                new FacilityGuideline { Category = "Playground & activity safety", Title = "Equipment safety", Requirement = "Playground equipment (climbing towers, slides, swings) must meet EN 1176-1's general design and test requirements.", Authority = "CEN", NormCode = "EN 1176-1" },
                new FacilityGuideline { Category = "Playground & activity safety", Title = "Fall-impact surfacing", Requirement = "Surfacing under fall-risk equipment is sized to the equipment's actual fall height via a critical-fall-height (CFH) test — not one fixed thickness for every piece.", Authority = "CEN", NormCode = "EN 1177" },
                new FacilityGuideline { Category = "Acoustics", Title = "Sound insulation", Requirement = "Sports-hall acoustics (airborne/impact sound between the hall and adjoining rooms) fall under DIN 4109's general sound-insulation-in-buildings requirements.", Authority = "DIN", NormCode = "DIN 4109" }
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
                    Materials = { extensiveSubstrate, rootBarrier, drainageBoard }, Providers = { optigruen, zinco },
                },
                new PlantPalette
                {
                    Name = "Extensive Wildflower & Grass Roof", Type = "extensive",
                    Description = "Biodiverse extensive mix on the same shallow substrate depth, favoring pollinators over a pure sedum mat.",
                    Plants = { festuca, achillea, origanum }, Norms = { fllGuideline },
                    Materials = { extensiveSubstrate, drainageBoard }, Providers = { zinco, bauder },
                },
                new PlantPalette
                {
                    Name = "Intensive Roof Garden", Type = "intensive",
                    Description = "Deeper substrate, higher structural load and irrigation needs — a walkable roof garden rather than a maintenance-free mat.",
                    Plants = { lavandula, sempervivum, achillea }, Norms = { fllGuideline },
                    Materials = { intensiveSubstrate, drainageBoard, roofWaterproofing }, Providers = { optigruen, bauder },
                }
            );

            db.SaveChanges();
        }
    }
}
