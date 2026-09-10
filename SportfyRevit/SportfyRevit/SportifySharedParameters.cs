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
        };

        private static bool _attempted;
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
            if (_attempted) return _ready;
            _attempted = true;

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
                }

                _ready = true;
                return true;
            }
            catch (System.Exception)
            {
                return false;
            }
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
