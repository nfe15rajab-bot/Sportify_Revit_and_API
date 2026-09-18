using Autodesk.Revit.DB;

namespace SportfyRevit
{
    /// <summary>
    /// Turns a provider build-up from the web app into a real Revit FloorType.
    ///
    /// This is the surface counterpart to family placement. A green roof or a
    /// paved walkway is not a family — it is a Floor, and a FloorType is exactly
    /// what the web app's assembly already describes: ordered layers, each with
    /// a material and a thickness. The translation is direct, with nothing to
    /// infer.
    ///
    /// Far cheaper than the family work, and for a structural reason: a floor
    /// type is a system family, so creating one is a duplicate plus a layer
    /// table inside the open document. No family document, no template, no save
    /// and reload.
    ///
    /// Types are named "Sportify - {provider} {system}" so anything this tool
    /// created is identifiable in a project template someone else maintains,
    /// and reused on the next import rather than duplicated — the same
    /// converge-don't-multiply rule the family type resolver follows, and it
    /// matters just as much here because AutoImportSync can run every two
    /// seconds.
    /// </summary>
    internal static class SportifyFloorTypeBuilder
    {
        /// <summary>Two thicknesses within a tenth of a millimetre are the same layer.</summary>
        private const double ThicknessToleranceFt = 0.0003;

        /// <summary>
        /// Maps the web app's layer functions onto Revit's own layer roles, so a
        /// schedule reads correctly and the structural layer is the one carrying
        /// the load. Revit requires exactly one Structure layer in a compound
        /// structure, which the caller guarantees below.
        /// </summary>
        private static MaterialFunctionAssignment FunctionFor(string? webFunction) => webFunction switch
        {
            "vegetation" => MaterialFunctionAssignment.Finish1,
            "substrate" => MaterialFunctionAssignment.Finish2,
            "wearing" => MaterialFunctionAssignment.Finish1,
            "bedding" => MaterialFunctionAssignment.Finish2,
            "filter" => MaterialFunctionAssignment.Membrane,
            "root_barrier" => MaterialFunctionAssignment.Membrane,
            "waterproofing" => MaterialFunctionAssignment.Membrane,
            "drainage" => MaterialFunctionAssignment.Substrate,
            "protection" => MaterialFunctionAssignment.Substrate,
            _ => MaterialFunctionAssignment.Structure,
        };

        /// <summary>
        /// Finds or creates the floor type for one assembly. Returns null if it
        /// cannot be built — the caller reports that rather than substituting
        /// something the user did not ask for.
        /// </summary>
        public static FloorType? GetOrCreate(Document doc, AssemblyDto assembly)
        {
            string name = assembly.RevitTypeName;
            if (string.IsNullOrWhiteSpace(name))
                name = $"Sportify - {assembly.Provider} {assembly.SystemName}";

            var existing = new FilteredElementCollector(doc)
                .OfClass(typeof(FloorType))
                .Cast<FloorType>()
                .FirstOrDefault(ft => string.Equals(ft.Name, name, StringComparison.OrdinalIgnoreCase));

            if (existing != null)
            {
                // Reused, but refreshed: a build-up edited in the web app should
                // reach a type that already exists, or the second import would
                // silently keep the first import's thicknesses.
                if (!MatchesAssembly(doc, existing, assembly))
                    ApplyStructure(doc, existing, assembly);
                ImportDiagnostics.FloorTypeReused(name);
                return existing;
            }

            // Duplicate any floor type that has a compound structure to edit —
            // FloorType cannot be constructed directly, only duplicated.
            var template = new FilteredElementCollector(doc)
                .OfClass(typeof(FloorType))
                .Cast<FloorType>()
                .FirstOrDefault(ft => ft.GetCompoundStructure() != null);

            if (template == null)
            {
                ImportDiagnostics.FloorTypeFailed(name, "this project has no floor type to duplicate from");
                return null;
            }

            if (template.Duplicate(name) is not FloorType created)
            {
                ImportDiagnostics.FloorTypeFailed(name, $"Revit refused to duplicate \"{template.Name}\"");
                return null;
            }

            if (!ApplyStructure(doc, created, assembly))
            {
                ImportDiagnostics.FloorTypeFailed(name, "layers could not be applied");
                return created;
            }

            ImportDiagnostics.FloorTypeCreated(name, assembly.Layers?.Count ?? 0, assembly.TotalThicknessM);
            return created;
        }

        /// <summary>
        /// Writes the assembly's layers onto a floor type.
        ///
        /// Revit demands exactly one Structure layer, so the thickest layer is
        /// promoted to that role — on a green roof that is the growing medium,
        /// which is both the deepest layer and the one actually bearing the
        /// build-up above it.
        /// </summary>
        private static bool ApplyStructure(Document doc, FloorType floorType, AssemblyDto assembly)
        {
            var source = assembly.Layers;
            if (source == null || source.Count == 0) return false;

            var ordered = source.OrderBy(l => l.Order).ToList();
            int structuralIndex = IndexOfThickest(ordered);

            var layers = new List<CompoundStructureLayer>();
            for (int i = 0; i < ordered.Count; i++)
            {
                var l = ordered[i];
                double widthFt = UnitUtils.ConvertToInternalUnits(l.ThicknessM, UnitTypeId.Meters);

                // Revit rejects a zero-width layer outright. A membrane quoted as
                // 0 mm is still a real product line on a drawing, so it is kept
                // at Revit's own minimum rather than dropped from the build-up.
                if (widthFt <= 0) widthFt = UnitUtils.ConvertToInternalUnits(0.001, UnitTypeId.Meters);

                var function = i == structuralIndex
                    ? MaterialFunctionAssignment.Structure
                    : FunctionFor(l.Function);

                layers.Add(new CompoundStructureLayer(widthFt, function, FindOrCreateMaterial(doc, l.Name)));
            }

            try
            {
                var structure = CompoundStructure.CreateSimpleCompoundStructure(layers);
                floorType.SetCompoundStructure(structure);
                StampIdentity(floorType, assembly);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static int IndexOfThickest(List<AssemblyLayerDto> layers)
        {
            int best = 0;
            for (int i = 1; i < layers.Count; i++)
                if (layers[i].ThicknessM > layers[best].ThicknessM) best = i;
            return best;
        }

        /// <summary>
        /// Does the type already carry this build-up? Compared on layer count and
        /// thickness rather than on material names, since a firm may have
        /// remapped materials deliberately and rewriting those would undo their
        /// work on every import.
        /// </summary>
        private static bool MatchesAssembly(Document doc, FloorType floorType, AssemblyDto assembly)
        {
            try
            {
                var cs = floorType.GetCompoundStructure();
                var wanted = assembly.Layers?.OrderBy(l => l.Order).ToList();
                if (cs == null || wanted == null) return false;

                var existing = cs.GetLayers();
                if (existing.Count != wanted.Count) return false;

                for (int i = 0; i < existing.Count; i++)
                {
                    double wantFt = UnitUtils.ConvertToInternalUnits(wanted[i].ThicknessM, UnitTypeId.Meters);
                    if (Math.Abs(existing[i].Width - wantFt) > ThicknessToleranceFt) return false;
                }
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Layer materials are matched by the product name the provider uses
        /// ("Growing media Zincoblend I"), and created when the project has no
        /// such material. Creating one is the right call rather than leaving the
        /// layer blank: an unnamed layer schedules as nothing, and the product
        /// name is the only thing a contractor can order against.
        /// </summary>
        private static ElementId FindOrCreateMaterial(Document doc, string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return ElementId.InvalidElementId;

            var existing = new FilteredElementCollector(doc)
                .OfClass(typeof(Material))
                .Cast<Material>()
                .FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));
            if (existing != null) return existing.Id;

            try
            {
                // Revit rejects the same characters in a material name as in a
                // type name, and these come from a provider's own product naming.
                return Material.Create(doc, SanitizeName(name!));
            }
            catch (Exception)
            {
                return ElementId.InvalidElementId;
            }
        }

        /// <summary>
        /// Records where the build-up came from, on the type itself. Without it
        /// a generated floor type is untraceable: someone opening the project in
        /// a year has no way to tell which supplier's system it represents, or
        /// which figures were published rather than assumed.
        /// </summary>
        private static void StampIdentity(FloorType floorType, AssemblyDto assembly)
        {
            TrySetParameter(floorType, BuiltInParameter.ALL_MODEL_MANUFACTURER, assembly.Provider);
            TrySetParameter(floorType, BuiltInParameter.ALL_MODEL_MODEL, assembly.SystemName);
            TrySetParameter(floorType, BuiltInParameter.ALL_MODEL_URL, assembly.SourceUrl);

            var notes = new List<string>();
            if (assembly.SaturatedKgM2 != null) notes.Add($"Saturated {assembly.SaturatedKgM2} kg/m2");
            if (assembly.WaterStorageLM2 != null) notes.Add($"Water storage {assembly.WaterStorageLM2} l/m2");
            if (assembly.BuildUpMm != null) notes.Add($"Published build-up {assembly.BuildUpMm} mm");

            int assumed = assembly.Layers?.Count(l => l.ThicknessSource != "published") ?? 0;
            if (assumed > 0) notes.Add($"{assumed} layer thickness(es) are typical values, not published by {assembly.Provider}");

            if (notes.Count > 0)
                TrySetParameter(floorType, BuiltInParameter.ALL_MODEL_DESCRIPTION, string.Join(" · ", notes));
        }

        private static void TrySetParameter(Element el, BuiltInParameter bip, string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            try
            {
                var p = el.get_Parameter(bip);
                if (p != null && !p.IsReadOnly && p.StorageType == StorageType.String) p.Set(value);
            }
            catch (Exception) { /* identity data is a nice-to-have, never worth failing over */ }
        }

        private static string SanitizeName(string name)
        {
            var cleaned = new string(name.Where(c => !"{}[]|;<>?`~".Contains(c)).ToArray()).Trim();
            return cleaned.Length == 0 ? "Sportify material" : cleaned;
        }
    }
}
