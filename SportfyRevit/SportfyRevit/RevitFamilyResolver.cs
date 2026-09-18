using System.Text.Json;
using Autodesk.Revit.DB;

namespace SportfyRevit
{
    /// <summary>
    /// Turns a web-app "place THIS family, at THIS size, in THESE materials"
    /// reference into a ready-to-place FamilySymbol.
    ///
    /// The whole reason this class exists is that dimensions and materials on
    /// a real family are TYPE parameters, not instance parameters — the
    /// basketball court tested against stores its size in a type literally
    /// named "26000 x 14000 mm". Writing a new length onto that type would
    /// resize every court already placed from it. So a size or material the
    /// document doesn't already have means a new type, which is exactly how
    /// such families are organised by hand anyway.
    ///
    /// Order of business: reuse a type that already matches, and only
    /// duplicate when nothing does. Re-importing the same layout therefore
    /// converges on the same set of types rather than breeding a new one per
    /// run — which matters a lot for AutoImportSync, where this can be
    /// reached every couple of seconds.
    /// </summary>
    internal static class RevitFamilyResolver
    {
        /// <summary>Two lengths within a millimeter are the same size; below that is float noise, not intent.</summary>
        private const double LengthToleranceM = 0.001;

        /// <summary>
        /// Null means "this reference couldn't be honored" — the family isn't
        /// loaded, or names nothing in this document. The caller falls back to
        /// its normal matching rather than placing something the user didn't ask
        /// for.
        /// </summary>
        public static FamilySymbol? Resolve(Document doc, RevitFamilyRefDto reference)
        {
            string name = reference.FamilyName ?? "(unnamed)";
            if (string.IsNullOrWhiteSpace(reference.FamilyName))
            {
                ImportDiagnostics.ExplicitFailed(name, "no family_name in the payload");
                return null;
            }

            var family = new FilteredElementCollector(doc)
                .OfClass(typeof(Family))
                .Cast<Family>()
                .FirstOrDefault(f => string.Equals(f.Name, reference.FamilyName, StringComparison.OrdinalIgnoreCase));
            if (family == null)
            {
                ImportDiagnostics.ExplicitFailed(name, "family is not loaded in this project — run Load Families first");
                return null;
            }

            var symbols = family.GetFamilySymbolIds()
                .Select(id => doc.GetElement(id) as FamilySymbol)
                .Where(s => s != null)
                .Cast<FamilySymbol>()
                .ToList();
            if (symbols.Count == 0)
            {
                ImportDiagnostics.ExplicitFailed(name, "family has no types");
                return null;
            }

            var baseSymbol = symbols.FirstOrDefault(s =>
                                 string.Equals(s.Name, reference.TypeName, StringComparison.OrdinalIgnoreCase))
                             ?? symbols[0];

            var wanted = ReadWantedValues(baseSymbol, reference.Parameters, out string skipped);
            if (wanted.Count == 0)
            {
                ImportDiagnostics.ExplicitFailed(name,
                    $"none of the {reference.Parameters?.Count ?? 0} requested parameters could be written. {skipped}");
                return Activate(baseSymbol);
            }

            // An existing type that already carries every requested value —
            // including the one we started from — is reused as-is.
            var match = symbols.FirstOrDefault(s => SatisfiesAll(s, wanted));
            if (match != null)
            {
                ImportDiagnostics.ExplicitResolved(name, $"{match.Name} (reused)");
                return Activate(match);
            }

            var newName = UniqueTypeName(doc, family, baseSymbol.Name, wanted);
            if (baseSymbol.Duplicate(newName) is not FamilySymbol duplicate)
            {
                ImportDiagnostics.ExplicitFailed(name, $"Revit refused to duplicate type \"{baseSymbol.Name}\"");
                return Activate(baseSymbol);
            }

            ApplyAll(duplicate, wanted);
            ImportDiagnostics.ExplicitResolved(name, $"{newName} (new)");
            return Activate(duplicate);
        }

        /// <summary>A symbol must be activated before NewFamilyInstance will accept it.</summary>
        private static FamilySymbol Activate(FamilySymbol symbol)
        {
            if (!symbol.IsActive) symbol.Activate();
            return symbol;
        }

        /// <summary>One requested parameter value, already matched to a real parameter on the type.</summary>
        private readonly record struct WantedValue(string Name, double? LengthM, ElementId? MaterialId);

        /// <summary>
        /// Matches the web app's free-form parameter bag against what this
        /// family actually exposes. A name the family doesn't have, or a value
        /// shape that doesn't fit the parameter's spec, is skipped silently —
        /// the bag is shaped by the customer's own family, and carrying a few
        /// entries this code can't use is normal rather than an error.
        /// </summary>
        private static List<WantedValue> ReadWantedValues(FamilySymbol symbol, Dictionary<string, JsonElement>? requested,
                                                         out string skippedReasons)
        {
            var list = new List<WantedValue>();
            var skipped = new List<string>();
            skippedReasons = "";
            if (requested == null) return list;

            foreach (var (name, element) in requested)
            {
                var parameter = symbol.LookupParameter(name);
                if (parameter == null) { skipped.Add($"'{name}': no such parameter on type"); continue; }
                if (parameter.IsReadOnly) { skipped.Add($"'{name}': read-only"); continue; }

                if (element.ValueKind == JsonValueKind.Number
                    && parameter.StorageType == StorageType.Double
                    && IsLength(parameter)
                    && element.TryGetDouble(out double meters))
                {
                    list.Add(new WantedValue(name, meters, null));
                    continue;
                }

                if (element.ValueKind == JsonValueKind.Object
                    && parameter.StorageType == StorageType.ElementId
                    && element.TryGetProperty("material_id", out var idElement))
                {
                    string? raw = idElement.ValueKind == JsonValueKind.String
                        ? idElement.GetString()
                        : idElement.ToString();
                    if (long.TryParse(raw, out long id))
                        list.Add(new WantedValue(name, null, new ElementId(id)));
                    else
                        skipped.Add($"'{name}': material_id '{raw}' isn't a number");
                    continue;
                }

                skipped.Add($"'{name}': value is {element.ValueKind} but parameter stores {parameter.StorageType}"
                            + (parameter.StorageType == StorageType.Double ? $" (length spec: {IsLength(parameter)})" : ""));
            }
            skippedReasons = skipped.Count == 0 ? "" : string.Join("; ", skipped);
            return list;
        }

        private static bool IsLength(Parameter p)
        {
            try { return p.Definition.GetDataType() == SpecTypeId.Length; }
            catch (Exception) { return false; }
        }

        private static bool SatisfiesAll(FamilySymbol symbol, List<WantedValue> wanted)
        {
            foreach (var w in wanted)
            {
                var p = symbol.LookupParameter(w.Name);
                if (p == null) return false;

                if (w.LengthM.HasValue)
                {
                    double currentM = UnitUtils.ConvertFromInternalUnits(p.AsDouble(), UnitTypeId.Meters);
                    if (Math.Abs(currentM - w.LengthM.Value) > LengthToleranceM) return false;
                }
                else if (w.MaterialId != null)
                {
                    if (p.AsElementId() != w.MaterialId) return false;
                }
            }
            return true;
        }

        private static void ApplyAll(FamilySymbol symbol, List<WantedValue> wanted)
        {
            foreach (var w in wanted)
            {
                var p = symbol.LookupParameter(w.Name);
                if (p == null || p.IsReadOnly) continue;

                if (w.LengthM.HasValue)
                    p.Set(UnitUtils.ConvertToInternalUnits(w.LengthM.Value, UnitTypeId.Meters));
                else if (w.MaterialId != null)
                    p.Set(w.MaterialId);
            }
        }

        /// <summary>
        /// Names a generated type after its dimensions — "Court [28.00x14.00m]" —
        /// mirroring how such families are already named by hand ("26000 x 14000
        /// mm") so the result looks native in a type selector rather than
        /// machine-generated.
        ///
        /// Two placements at the same size but different materials would land on
        /// the same name, so a numeric suffix keeps them distinct instead of one
        /// silently overwriting the other's materials.
        /// </summary>
        private static string UniqueTypeName(Document doc, Family family, string baseName, List<WantedValue> wanted)
        {
            var lengths = wanted.Where(w => w.LengthM.HasValue)
                                .Select(w => w.LengthM!.Value)
                                .OrderByDescending(v => v)
                                .ToList();

            // Revit rejects { } [ ] | ; < > ? ` ~ in a type name — brackets looked
            // natural and threw ArgumentException on every duplicate, which then
            // fell back to keyword matching and silently placed the ORIGINAL size.
            //
            // Better than avoiding them: follow the family's own convention. These
            // families name their types by size ("26000 x 14000 mm"), so a
            // generated 20 x 12 type becomes "20000 x 12000 mm" — indistinguishable
            // from a hand-made one in the type selector.
            string stem;
            if (lengths.Count >= 2 && LooksLikeMillimeterSizeName(baseName))
                stem = $"{lengths[0] * 1000:0} x {lengths[1] * 1000:0} mm";
            else if (lengths.Count >= 2)
                stem = $"{baseName} - {lengths[0]:0.##}x{lengths[1]:0.##}m";
            else if (lengths.Count == 1)
                stem = $"{baseName} - {lengths[0]:0.##}m";
            else
                stem = $"{baseName} - Sportify";

            stem = SanitizeTypeName(stem);

            var taken = family.GetFamilySymbolIds()
                .Select(id => (doc.GetElement(id) as FamilySymbol)?.Name)
                .Where(n => n != null)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (!taken.Contains(stem)) return stem;
            for (int i = 2; i < 1000; i++)
            {
                string candidate = $"{stem} ({i})";
                if (!taken.Contains(candidate)) return candidate;
            }
            return $"{stem} {Guid.NewGuid():N}";
        }

        /// <summary>Does this type name follow the "26000 x 14000 mm" convention these families use?</summary>
        private static bool LooksLikeMillimeterSizeName(string name)
        {
            return System.Text.RegularExpressions.Regex.IsMatch(
                name, @"^\s*\d+(\.\d+)?\s*[xX×]\s*\d+(\.\d+)?\s*mm\s*$");
        }

        /// <summary>
        /// Revit's own prohibited set for element names. Belt and braces: the
        /// names built above avoid these already, but a family's own base name
        /// is carried into some of them, and that name came from a customer's
        /// file rather than from this code.
        /// </summary>
        private static string SanitizeTypeName(string name)
        {
            var cleaned = new string(name.Where(c => !"{}[]|;<>?`~".Contains(c)).ToArray()).Trim();
            return cleaned.Length == 0 ? "Sportify type" : cleaned;
        }
    }
}
