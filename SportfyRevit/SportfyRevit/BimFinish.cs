using Autodesk.Revit.DB;

namespace SportfyRevit
{
    /// <summary>
    /// What turns an imported piece into a BIM element rather than a shape (user, 2026-10-01: "check the 3d model itself, make sure it matches the criteria of a bim model"; the audit of the
    /// Goldbeck model found it missing): a host level (a family placed without one belonged to no storey: not in a list by level, not in an IFC storey) and the DIN 276 / DIN 277 classes
    /// the Sportify template schedules by (they were only written when the template was applied, so everything imported afterwards was unclassed). Run at the end of every import and by the
    /// unattended SubmissionFix step "bim" over a whole project. Never replaces a value a person typed.
    /// </summary>
    internal static class BimFinish
    {
        internal sealed record Result(int Levelled, int Classed, int Floors, int Pieces, string Notes);

        /// <summary>The level an element stands on: the highest one at or below the given height (feet), with 5 cm of give for a slab surface a hair under its level.</summary>
        internal static Level? LevelBelow(IEnumerable<Level> levels, double zFt) =>
            levels.Where(l => l.Elevation <= zFt + 0.05).OrderByDescending(l => l.Elevation).FirstOrDefault();

        /// <summary>
        /// Gives a family instance that has no host level its "Schedule Level" (the parameter Revit keeps for an instance that is not level-hosted), the level its origin stands on.
        /// False when it already has a level, has no such parameter, or the parameter is read-only.
        /// </summary>
        internal static bool EnsureLevel(Element e, IReadOnlyCollection<Level> levels) => EnsureLevel(e, levels, out _);

        /// <summary>The same, saying why when nothing was set (for the report).</summary>
        internal static bool EnsureLevel(Element e, IReadOnlyCollection<Level> levels, out string why)
        {
            why = "";
            try
            {
                if (e is not FamilyInstance) { why = "not a family instance"; return false; }
                if (e.LevelId != ElementId.InvalidElementId) { why = "already hosted"; return false; }
                // a level-based instance made without a level: its own Level parameter, when Revit lets it be set (the piece is then put back at the height it stood)
                var own = e.get_Parameter(BuiltInParameter.FAMILY_LEVEL_PARAM);
                var z0 = (e.Location as LocationPoint)?.Point.Z ?? e.get_BoundingBox(null)?.Min.Z;
                if (own != null && !own.IsReadOnly && z0 != null && LevelBelow(levels, z0.Value) is { } own1)
                {
                    try
                    {
                        if (own.Set(own1.Id))
                        {
                            e.Document.Regenerate();
                            var z1 = (e.Location as LocationPoint)?.Point.Z ?? e.get_BoundingBox(null)?.Min.Z;
                            if (z1 != null && Math.Abs(z1.Value - z0.Value) > 1e-6) ElementTransformUtils.MoveElement(e.Document, e.Id, new XYZ(0, 0, z0.Value - z1.Value));
                            return true;
                        }
                    }
                    catch (Exception) { /* falls through to Schedule Level */ }
                }
                var prm = e.get_Parameter(BuiltInParameter.INSTANCE_SCHEDULE_ONLY_LEVEL_PARAM);
                if (prm == null) { why = "no Schedule Level parameter"; return false; }
                if (prm.IsReadOnly) { why = "Schedule Level is read-only"; return false; }
                var current = prm.AsElementId();
                if (current != null && current.Value != ElementId.InvalidElementId.Value) { why = "Schedule Level already set"; return false; }
                var z = (e.Location as LocationPoint)?.Point.Z ?? e.get_BoundingBox(null)?.Min.Z;
                if (z == null) { why = "no height to read"; return false; }
                var level = LevelBelow(levels, z.Value);
                if (level == null) { why = "no level at or below " + z.Value.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + " ft"; return false; }
                if (prm.Set(level.Id)) return true;
                // refused for the level below: say which levels it does take (a level that is not a building story, say), nearest first
                var taken = levels.OrderBy(l => Math.Abs(l.Elevation - z.Value)).FirstOrDefault(l => { try { return prm.Set(l.Id); } catch (Exception) { return false; } });
                if (taken != null) return true;
                why = $"Revit refused every level (Level parameter {(own == null ? "missing" : own.IsReadOnly ? "read-only" : "settable")}; Schedule Level parameter \"{prm.Definition?.Name}\", {prm.StorageType}, value \"{prm.AsValueString()}\", level tried \"{level.Name}\")"; return false;
            }
            catch (Exception ex) { why = ex.Message; return false; }
        }

        /// <summary>The language the project's Sportify view templates are in, or null when it has none (a project not on the template does not schedule by the norm classes).</summary>
        internal static TemplateLanguage? LanguageOf(Document doc)
        {
            var names = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Where(v => v.IsTemplate).Select(v => v.Name).ToList();
            return SportifyTemplateSpec.Detect(names);
        }

        /// <summary>For the elements an import just made. Inside the caller's transaction.</summary>
        internal static Result Apply(Document doc, IEnumerable<ElementId> ids)
        {
            var elements = ids.Select(doc.GetElement).Where(e => e != null && e is not ElementType).Cast<Element>().ToList();
            return Apply(doc, elements);
        }

        /// <summary>For every Sportify element of the project (a family instance with a Sportify_Category, a "Sportify - " floor). Inside the caller's transaction.</summary>
        internal static Result ApplyAll(Document doc)
        {
            var all = new List<Element>();
            foreach (var fi in new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>())
                if (!string.IsNullOrWhiteSpace(SportifyElementScan.CategoryOf(fi))) all.Add(fi);
            foreach (var f in new FilteredElementCollector(doc).OfClass(typeof(Floor)).Cast<Floor>())
                if (BimRules.IsSportifyTypeName(doc.GetElement(f.GetTypeId())?.Name)) all.Add(f);
            return Apply(doc, all);
        }

        static Result Apply(Document doc, IReadOnlyCollection<Element> elements)
        {
            var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().ToList();
            var levelled = 0; var whys = new Dictionary<string, int>();
            foreach (var e in elements)
            {
                if (EnsureLevel(e, levels, out var why)) levelled++;
                else if (why.Length > 0 && why != "already hosted" && why != "not a family instance") whys[why] = whys.GetValueOrDefault(why) + 1;
            }
            var notes = whys.Count == 0 ? "" : "host level not set: " + string.Join("; ", whys.Select(kv => kv.Value + " x " + kv.Key));
            if (notes.Length > 0) SportifyLog.Info("bim", notes);
            int classed = 0, floors = 0, pieces = 0;
            if (LanguageOf(doc) is TemplateLanguage language)
            {
                var norms = NormParameters.Assign(doc, elements, language);
                classed = norms.Changed; floors = norms.Floors; pieces = norms.Pieces;
            }
            return new Result(levelled, classed, floors, pieces, notes);
        }
    }
}
