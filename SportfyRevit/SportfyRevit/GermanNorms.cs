namespace SportfyRevit
{
    /// <summary>
    /// How a Sportify element is classed by the two German norms the templates schedule by, without Revit so that Tools/AddinCheck can test it:
    ///   DIN 277 (Grundflächen und Rauminhalte): an area is a Nutzungsfläche (NUF 1 ... 7), Technikfläche (TF) or Verkehrsfläche (VF), and lies in a Bereich: a covered and closed (a), covered
    ///   and open (b) or NOT covered (c). A roof is Bereich c. A sports court on it is a Nutzungsfläche of the class "sonstige Nutzung" (NUF 7); a path or an entry is Verkehrsfläche.
    ///   DIN 276 (Kosten im Bauwesen), Kostengruppen: 363 Dachbeläge (the build-up of a roof, green roof included), 570 Pflanz- und Saatflächen (planting), 550 Einbauten in Außenanlagen
    ///   und Freiflächen (fixed equipment: sport and play equipment), 610 Allgemeine Ausstattung (furniture).
    /// The classes are written in the template's language (German, or English words for the same classes); the codes are the norms' own and never change.
    /// These assignments are PROPOSALS: the norms leave room, and which class a roof garden's areas belong to is a decision for the team's cost planner. They are stated here in one place, and
    /// the schedules show them in a parameter (Sportify_DIN277, Sportify_KG) that can be overwritten in the model.
    /// </summary>
    internal static class GermanNorms
    {
        public const string Review = "Vorschlag, vom Team zu prüfen";
        public const string ReviewEn = "proposal, for the team to review";

        internal sealed record NormClass(string Code, string Text, string TextEn);

        public static readonly NormClass RoofBuildUp = new("363", "Dachbeläge", "Roof coverings");
        public static readonly NormClass Planting = new("570", "Pflanz- und Saatflächen", "Planting and seeded areas");
        public static readonly NormClass FixedEquipment = new("550", "Einbauten in Außenanlagen und Freiflächen", "Fixtures in outdoor facilities and open areas");
        public static readonly NormClass Furniture = new("610", "Allgemeine Ausstattung", "General furnishings");

        public static readonly NormClass UsableSport = new("NUF 7", "Nutzungsfläche, sonstige Nutzung (Sport), Bereich c (nicht überdeckt)", "Usable area, other use (sport), area c (not covered)");
        public static readonly NormClass UsableGarden = new("NUF 7", "Nutzungsfläche, sonstige Nutzung (Aufenthalt im Grünen), Bereich c (nicht überdeckt)", "Usable area, other use (green space), area c (not covered)");
        public static readonly NormClass RoofArea = new("Bereich c", "Dachfläche, nicht überdeckt (keine Grundfläche im Sinne der Geschosse)", "Roof area, not covered (not a storey floor area)");
        public static readonly NormClass Circulation = new("VF", "Verkehrsfläche, Bereich c (nicht überdeckt)", "Circulation area, area c (not covered)");

        /// <summary>The Kostengruppe (DIN 276) of what a Sportify piece is, by its Sportify_Category ("field", "activity", "furniture", "vegetation", "garden").</summary>
        public static NormClass CostGroupOfPiece(string? category)
        {
            switch ((category ?? "").Trim().ToLowerInvariant())
            {
                case "vegetation": case "garden": return Planting;
                case "furniture": return Furniture;
                default: return FixedEquipment;          // fields (courts), activities: fixed equipment on the roof
            }
        }

        /// <summary>The DIN 277 class of the area a piece takes: a court or activity is NUF 7 (sport), planting and furniture stand in the roof area they are placed in.</summary>
        public static NormClass AreaOfPiece(string? category)
        {
            switch ((category ?? "").Trim().ToLowerInvariant())
            {
                case "field": case "sport": case "sports": case "activity": return UsableSport;
                case "vegetation": case "garden": return UsableGarden;
                default: return RoofArea;
            }
        }

        /// <summary>The Kostengruppe of a floor (a zone's or the roof finish's build-up): 363 Dachbeläge.</summary>
        public static NormClass CostGroupOfFloor() => RoofBuildUp;

        /// <summary>The DIN 277 class of a floor of the roof: a garden zone is usable green (NUF 7), the roof finish is roof area.</summary>
        public static NormClass AreaOfFloor(bool isGardenZone) => isGardenZone ? UsableGarden : RoofArea;

        /// <summary>A roof finish (gravel ballast, paving) rather than a planted build-up: by the words the build-up's type name has (the importer names types "Sportify - &lt;provider&gt; &lt;system&gt;").</summary>
        public static bool IsRoofFinishTypeName(string? typeName)
        {
            var n = (typeName ?? "").ToLowerInvariant();
            return n.Contains("ballast") || n.Contains("gravel") || n.Contains("kies") || n.Contains("paving") || n.Contains("pavers") || n.Contains("asphalt");
        }

        public static string Text(NormClass c) => c.Code + " " + c.Text;

        /// <summary>"363 Dachbeläge" in German, "363 Roof coverings" in English.</summary>
        public static string Text(NormClass c, TemplateLanguage language) => c.Code + " " + (language == TemplateLanguage.De ? c.Text : c.TextEn);
    }
}
