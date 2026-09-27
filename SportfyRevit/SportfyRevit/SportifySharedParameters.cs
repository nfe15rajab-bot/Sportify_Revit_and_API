using Autodesk.Revit.DB;

namespace SportfyRevit
{
    /// <summary>
    /// Stamps a fixed set of Shared Parameters — the "unified parameter
    /// contract" worked out across categories (Category/TypeId/Variant/
    /// QualityLevel/Norm/LengthM/WidthM/ReferenceMaterial/ReferenceProvider/
    /// QualityKey) — onto every placed element, real family instance or
    /// placeholder box alike. This is the placeholder-geometry equivalent
    /// of what Moamen's eventual "sportified" families are expected to
    /// expose as native family parameters; project-bound shared parameters
    /// are the interim way to get the same queryable data into Revit's
    /// Properties panel/Schedules today, before real families exist.
    ///
    /// Genuinely unverified beyond compilation: creating/binding shared
    /// parameters is a real Revit document mutation with no dry-run, and
    /// this could not be tested inside an actual Revit session from here.
    /// Every failure mode is therefore caught and swallowed rather than
    /// surfaced — geometry placement (which already works) must never
    /// break because parameter-stamping didn't.
    /// </summary>
    internal static class SportifySharedParameters
    {
        private const string GroupName = "Sportify";
        private static readonly string[] ParamNames =
        {
            "Sportify_Category", "Sportify_TypeId", "Sportify_Variant", "Sportify_QualityLevel",
            "Sportify_Norm", "Sportify_LengthM", "Sportify_WidthM",
            "Sportify_ReferenceMaterial", "Sportify_ReferenceProvider", "Sportify_QualityKey",
            // the two the German templates schedule by (GermanNorms): the DIN 277 class of the area and the DIN 276 Kostengruppe
            "Sportify_DIN277", "Sportify_KG",
        };

        // one attempt per document (a project opened later in the same session gets its own): the parameters are bound in a document, not in the session
        private static readonly HashSet<Document> Attempted = new();
        private static bool _ready;

        /// <summary>
        /// Idempotent per Revit session (first call does the real work,
        /// every later call just returns the cached result): points Revit
        /// at a Sportify-owned shared parameter file (creating an empty one
        /// if it doesn't exist yet — Revit writes the real file format
        /// itself once definitions are created in it, so nothing here
        /// hand-authors that format), then binds all Sportify_* parameters
        /// as instance parameters on Generic Model + Planting — the two
        /// categories PlacePlaceholderBox actually uses today. A real
        /// family's own categories would need adding here too once one
        /// exists.
        /// </summary>
        public static bool EnsureBound(Document doc)
        {
            if (!Attempted.Add(doc)) return _ready;

            try
            {
                var app = doc.Application;
                string path = System.IO.Path.Combine(
                    System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData),
                    "Autodesk", "Revit", "Addins", "2025", "SportifySharedParameters.txt");

                if (!System.IO.File.Exists(path))
                    System.IO.File.WriteAllText(path, string.Empty);

                app.SharedParametersFilename = path;
                var defFile = app.OpenSharedParameterFile();
                if (defFile == null) return false;

                var group = defFile.Groups.get_Item(GroupName) ?? defFile.Groups.Create(GroupName);

                var categorySet = app.Create.NewCategorySet();
                categorySet.Insert(doc.Settings.Categories.get_Item(BuiltInCategory.OST_GenericModel));
                categorySet.Insert(doc.Settings.Categories.get_Item(BuiltInCategory.OST_Planting));
                categorySet.Insert(doc.Settings.Categories.get_Item(BuiltInCategory.OST_Floors));      // the zones' and the roof finish's floors carry the norm classes
                categorySet.Insert(doc.Settings.Categories.get_Item(BuiltInCategory.OST_StructuralFoundation));    // ...and so do zones an earlier import made as foundation slabs
                var binding = app.Create.NewInstanceBinding(categorySet);

                foreach (var name in ParamNames)
                {
                    var definition = group.Definitions.get_Item(name);
                    if (definition == null)
                    {
                        var options = new ExternalDefinitionCreationOptions(name, SpecTypeId.String.Text);
                        definition = group.Definitions.Create(options);
                    }

                    if (!doc.ParameterBindings.Contains(definition))
                        doc.ParameterBindings.Insert(definition, binding, GroupTypeId.Data);
                    else
                        WidenBinding(doc, definition, categorySet);
                }

                _ready = true;
                return true;
            }
            catch (System.Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Makes sure every Sportify parameter is bound to all the categories that carry them (floors included), in a document where EnsureBound may have run long ago (an import in an earlier
        /// session, or an older add-in that bound fewer categories). Inside a transaction. Returns how many parameters were widened.
        /// </summary>
        public static int EnsureCategories(Document doc)
        {
            int widened = 0;
            try
            {
                var app = doc.Application;
                var wanted = app.Create.NewCategorySet();
                wanted.Insert(doc.Settings.Categories.get_Item(BuiltInCategory.OST_GenericModel));
                wanted.Insert(doc.Settings.Categories.get_Item(BuiltInCategory.OST_Planting));
                wanted.Insert(doc.Settings.Categories.get_Item(BuiltInCategory.OST_Floors));
                wanted.Insert(doc.Settings.Categories.get_Item(BuiltInCategory.OST_StructuralFoundation));
                var it = doc.ParameterBindings.ForwardIterator();
                var definitions = new List<Definition>();
                while (it.MoveNext()) if (it.Key is Definition d && ParamNames.Contains(d.Name)) definitions.Add(d);
                foreach (var d in definitions) if (WidenBinding(doc, d, wanted)) widened++;
            }
            catch (System.Exception ex) { SportifyLog.Warn("templates", "the Sportify parameters' categories could not be checked: " + ex.Message); }
            return widened;
        }

        /// <summary>The categories one Sportify parameter is bound to, as text, for the log ("Generic Models, Planting"), or null when it is not bound.</summary>
        public static string? BoundCategories(Document doc, string parameterName)
        {
            try
            {
                var it = doc.ParameterBindings.ForwardIterator();
                while (it.MoveNext())
                    if (it.Key is Definition d && d.Name == parameterName && it.Current is ElementBinding b)
                        return string.Join(", ", b.Categories.Cast<Category>().Select(c => c.Name));
            }
            catch (System.Exception) { /* nothing to report */ }
            return null;
        }

        /// <summary>
        /// A parameter bound by an older import sits on fewer categories (before the norm classes, Generic Model and Planting only): add the missing ones, keeping what is bound, so a project
        /// imported earlier gets its floors classed too.
        /// </summary>
        private static bool WidenBinding(Document doc, Definition definition, CategorySet wanted)
        {
            try
            {
                if (doc.ParameterBindings.get_Item(definition) is not InstanceBinding existing) return false;
                var have = new HashSet<long>();
                foreach (Category c in existing.Categories) have.Add(c.Id.Value);
                var missing = new List<Category>();
                foreach (Category c in wanted) if (!have.Contains(c.Id.Value)) missing.Add(c);
                if (missing.Count == 0) return false;

                var union = doc.Application.Create.NewCategorySet();
                foreach (Category c in existing.Categories) union.Insert(c);
                foreach (var c in missing) union.Insert(c);
                doc.ParameterBindings.ReInsert(definition, doc.Application.Create.NewInstanceBinding(union), GroupTypeId.Data);
                SportifyLog.Info("templates", $"{definition.Name}: bound to {missing.Count} more categor{(missing.Count == 1 ? "y" : "ies")} ({string.Join(", ", missing.Select(c => c.Name))})");
                return true;
            }
            catch (System.Exception ex) { SportifyLog.Warn("templates", definition.Name + ": its categories could not be widened: " + ex.Message); return false; }
        }

        /// <summary>Best-effort: any single parameter that isn't found or is read-only is skipped rather than failing the whole set.</summary>
        public static void SetValues(Element element, string? category, string? typeId, string? variant,
            string? qualityLevel, string? norm, double? lengthM, double? widthM,
            string? referenceMaterial, string? referenceProvider, string? qualityKey)
        {
            if (!_ready) return;

            TrySet(element, "Sportify_Category", category);
            TrySet(element, "Sportify_TypeId", typeId);
            TrySet(element, "Sportify_Variant", variant);
            TrySet(element, "Sportify_QualityLevel", qualityLevel);
            TrySet(element, "Sportify_Norm", norm);
            TrySet(element, "Sportify_LengthM", lengthM?.ToString("0.##"));
            TrySet(element, "Sportify_WidthM", widthM?.ToString("0.##"));
            TrySet(element, "Sportify_ReferenceMaterial", referenceMaterial);
            TrySet(element, "Sportify_ReferenceProvider", referenceProvider);
            TrySet(element, "Sportify_QualityKey", qualityKey);
        }

        private static void TrySet(Element element, string paramName, string? value)
        {
            if (value == null) return;
            try
            {
                var parameter = element.LookupParameter(paramName);
                if (parameter != null && !parameter.IsReadOnly)
                    parameter.Set(value);
            }
            catch (System.Exception)
            {
                // Best-effort — one parameter failing to set must not lose the rest.
            }
        }
    }
}
