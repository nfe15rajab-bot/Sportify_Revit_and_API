using Autodesk.Revit.DB;

namespace SportfyRevit
{
    /// <summary>
    /// Writes the two German norm classes onto Sportify's elements (GermanNorms): Sportify_KG, the DIN 276 Kostengruppe, and Sportify_DIN277, the DIN 277 class of the area. They are shared
    /// parameters bound to floors, generic models and planting (SportifySharedParameters), so the schedules can group by them and a person can overwrite a value in the model. A value that is
    /// already there is never replaced: what the cost planner typed stays.
    /// </summary>
    internal static class NormParameters
    {
        public const string Kg = "Sportify_KG";
        public const string Din277 = "Sportify_DIN277";

        internal sealed record Result(int Changed, int Floors, int Pieces, int WithoutParameter);

        /// <summary>
        /// Fills the empty norm parameters of the given elements and of every Sportify floor in the project (a floor whose type is named "Sportify - ...": the import's ledger may not list the zones'
        /// floors, and a floor is what DIN 276 KG 363 is about). Returns how many elements got a value, how many were floors and pieces, and how many could not be classed because the
        /// parameter is not there. Call inside a transaction.
        /// </summary>
        public static Result Assign(Document doc, IEnumerable<Element> elements, TemplateLanguage language)
        {
            var all = new Dictionary<ElementId, Element>();
            foreach (var el in elements) all[el.Id] = el;
            foreach (var floor in new FilteredElementCollector(doc).OfClass(typeof(Floor)).Cast<Floor>())
                if (!all.ContainsKey(floor.Id) && BimRules.IsSportifyTypeName(doc.GetElement(floor.GetTypeId())?.Name)) all[floor.Id] = floor;

            int changed = 0, floors = 0, pieces = 0, without = 0;
            foreach (var el in all.Values)
            {
                GermanNorms.NormClass kg, area;
                if (el is Floor floor)
                {
                    var typeName = doc.GetElement(floor.GetTypeId())?.Name;
                    if (!BimRules.IsSportifyTypeName(typeName)) continue;               // a floor of the user's own
                    kg = GermanNorms.CostGroupOfFloor();
                    area = GermanNorms.AreaOfFloor(isGardenZone: !GermanNorms.IsRoofFinishTypeName(typeName));
                    floors++;
                }
                else if (el is FamilyInstance)
                {
                    var category = SportifyElementScan.CategoryOf(el);
                    kg = GermanNorms.CostGroupOfPiece(category);
                    area = GermanNorms.AreaOfPiece(category);
                    pieces++;
                }
                else continue;

                if (el.LookupParameter(Kg) == null || el.LookupParameter(Din277) == null)
                {
                    if (without++ == 0)        // the first one says what the element does have, for the log
                        SportifyLog.Info("templates", $"norm classes: {el.GetType().Name} {el.Id.Value} ({el.Category?.Name}) has no {Kg}/{Din277}; its Sportify parameters: " +
                            string.Join(", ", el.Parameters.Cast<Parameter>().Select(q => q.Definition.Name).Where(n => n.StartsWith("Sportify")).DefaultIfEmpty("none")));
                    continue;
                }
                var a = SetIfEmpty(el, Kg, GermanNorms.Text(kg, language));
                var b = SetIfEmpty(el, Din277, GermanNorms.Text(area, language));
                if (a || b) changed++;
            }
            return new Result(changed, floors, pieces, without);
        }

        private static bool SetIfEmpty(Element el, string parameterName, string value)
        {
            try
            {
                var p = el.LookupParameter(parameterName);
                if (p == null || p.IsReadOnly || !string.IsNullOrEmpty(p.AsString())) return false;
                return p.Set(value);
            }
            catch (Exception) { return false; }
        }
    }
}
