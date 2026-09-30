using System.IO;
using System.Reflection;
using System.Text.Json;
using Autodesk.Revit.DB;

namespace SportfyRevit
{
    /// <summary>
    /// The design team's own families, placed as they authored them.
    ///
    /// Everything else in this folder BUILDS geometry: a padel court, a bench, a
    /// tree are ours to model, so we generate a family and bake the size into
    /// it. A planter is not ours. Debjyoti authored it in Revit, with its own
    /// parameters and its own formulas, and the web app mirrors those parameters
    /// so a person can set them there.
    ///
    /// So this builder builds nothing. It finds his family — loaded in the
    /// project already, or loaded from the library that ships with the add-in —
    /// and the instance is then configured from the payload
    /// (ApplyParameters below). His family stays parametric in Revit, which a
    /// generated one could never be: change Rim Height in Revit afterwards and
    /// his formulas still recompute.
    ///
    /// ── Why the names need no translation table ──
    /// The web app mirrors the family's parameters in camel case, one for one:
    /// "Substrate Depth" is `substrateDepth`, "Seat Cap" is `seatCap`. So the
    /// name is DERIVED rather than looked up in a list someone has to maintain,
    /// and a parameter added to the family in Revit flows through to here
    /// without this file changing.
    /// </summary>
    internal static class SportifyPlanterFamilyBuilder
    {
        /// <summary>
        /// Which family file each kit item is, and what it is called once loaded.
        /// The planters are two families, not two types of one — that is how they
        /// were authored. Adding a family the design team sends is one line here
        /// plus the .rfa in Library\Families.
        /// </summary>
        private static readonly Dictionary<string, (string File, string FamilyName)> FamilyFor = new(StringComparer.OrdinalIgnoreCase)
        {
            ["planter_s"]      = ("Sportify_PlanterS",     "Planter S"),
            ["planter_t"]      = ("Sportify_PlanterT",     "Planter T"),
            // Same parameters as the two above, checked value for value against
            // the shipped .rfa — the pedestal is real geometry here rather than a
            // dimension the tray floor sits above, so it is a separate family.
            ["planter_s_pedestal"] = ("Sportify_PlanterSPedestal", "PlanterS ( With Pedestal)"),
            ["planter_t_pedestal"] = ("Sportify_PlanterTPedestal", "PlanterT (With Pedestal)"),
            ["green_roof_module"]  = ("Sportify_GreenRoofModule",  "Green Roof Module"),
            // Authored in millimetres, unlike the three above them — the payload
            // carries the unit per family, so nothing here has to know that.
            ["trampoline"]   = ("Sportify_TrampolineSandPit", "Trampoline-SandPit"),
            ["urban_bocce"]  = ("Sportify_BocceCourt",        "Bocce Court"),
            ["sprint_lane"]  = ("Sportify_SprintLane",        "Sprint Lane"),
            ["climbing_tower"] = ("Sportify_ClimbingTower", "Climbing Tower"),
            ["locker_module"]  = ("Sportify_LockerBank",    "Locker Bank"),
            ["dressing_cabin"] = ("Sportify_DressingCabin", "Dressing_Cabin"),
            ["yoga_deck"]      = ("Sportify_YogaDeck",      "Yoga Deck"),
        };

        /// <summary>
        /// The symbol for this kit item, or null when neither the project nor
        /// the library has the family — in which case the caller falls back to
        /// a placeholder, and the report says the family was not found rather
        /// than pretending a box is a planter.
        /// </summary>
        /// <summary>The registry key ("green_roof_module") of a design family named by its file or family name ("Sportify_GreenRoofModule"); null when unknown.</summary>
        internal static string? KeyForFamilyName(string? name) =>
            string.IsNullOrEmpty(name) ? null
                : FamilyFor.FirstOrDefault(kv => string.Equals(kv.Value.File, name, StringComparison.OrdinalIgnoreCase) || string.Equals(kv.Value.FamilyName, name, StringComparison.OrdinalIgnoreCase)).Key;

        public static FamilySymbol? GetOrLoadSymbol(Document doc, DesignFamilyDto block)
        {
            if (block.Type == null || !FamilyFor.TryGetValue(block.Type, out var known)) return null;
            var fileBase = known.File;

            // Already in the project: use it. A person who has loaded their own
            // version of the family gets theirs, not ours — the same rule the
            // furniture builder follows for a firm's own content.
            var loaded = FindLoaded(doc, known.FamilyName) ?? FindLoaded(doc, block.Family)
                      ?? FindLoaded(doc, fileBase) ?? FindLoaded(doc, block.Label);
            if (loaded != null) return Activate(loaded);

            var path = LibraryPath(fileBase);
            if (path == null) return null;

            try
            {
                if (!doc.LoadFamily(path, new OverwriteFamilyLoadOptions(), out Family family) && family == null)
                    return null;
                var symbol = FirstSymbol(family) ?? FindLoaded(doc, fileBase);
                return symbol == null ? null : Activate(symbol);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// The same "the project's own copy wins, else load ours from the library" rule as <see cref="GetOrLoadSymbol"/>, generalized for a
        /// curated quality_key match (FamilyMatchRules) rather than a design-team kit item: <paramref name="fileBase"/> and
        /// <paramref name="familyName"/> are the same string for a family authored and named the same as its file.
        /// </summary>
        public static FamilySymbol? FindOrLoad(Document doc, string fileBase, string familyName)
        {
            var loaded = FindLoaded(doc, familyName) ?? FindLoaded(doc, fileBase);
            if (loaded != null) return Activate(loaded);

            var path = LibraryPath(fileBase);
            if (path == null) return null;

            try
            {
                if (!doc.LoadFamily(path, new OverwriteFamilyLoadOptions(), out Family family) && family == null)
                    return null;
                var symbol = FirstSymbol(family) ?? FindLoaded(doc, fileBase);
                return symbol == null ? null : Activate(symbol);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// Puts the web app's values onto the placed instance.
        ///
        /// Only what the family will accept: a parameter the family computes by
        /// formula is read-only, so Rim Level and the rest are offered and
        /// quietly declined — which is correct, because the family works them
        /// out itself and the web app only mirrors them to show its own drawing.
        /// </summary>
        public static void ApplyParameters(Element instance, DesignFamilyDto block)
        {
            if (block.Params == null) return;

            // Each family is mirrored in the unit it was authored in, so the
            // numbers match its own dialog on both sides.
            var unit = string.Equals(block.Units, "m", StringComparison.OrdinalIgnoreCase)
                ? UnitTypeId.Meters : UnitTypeId.Millimeters;

            foreach (var (key, value) in block.Params)
            {
                // Some families are mirrored in camel case ("substrateDepth"),
                // others under their own spelling ("Tower_Height"). Try the name
                // as given first, then the camel-case reading of it.
                var p = LookupAnywhere(instance, key) ?? LookupAnywhere(instance, RevitParameterName(key));
                if (p == null || p.IsReadOnly) continue;

                try
                {
                    switch (value.ValueKind)
                    {
                        case JsonValueKind.True:  p.Set(1); break;
                        case JsonValueKind.False: p.Set(0); break;
                        case JsonValueKind.Number when p.StorageType == StorageType.Double:
                            // Revit stores feet whatever the family says.
                            p.Set(UnitUtils.ConvertToInternalUnits(value.GetDouble(), unit));
                            break;
                        case JsonValueKind.Number when p.StorageType == StorageType.Integer:
                            p.Set((int)value.GetDouble()); break;
                        case JsonValueKind.String when p.StorageType == StorageType.String:
                            p.Set(value.GetString()); break;
                    }
                }
                catch (Exception) { /* one parameter the family would not take is not worth losing the import over */ }
            }
        }

        /// <summary>On the instance, or on its type when the family holds the parameter there.</summary>
        private static Parameter? LookupAnywhere(Element instance, string name)
        {
            var p = instance.LookupParameter(name);
            if (p != null) return p;
            return instance.Symbol()?.LookupParameter(name);
        }

        /// <summary>"substrateDepth" → "Substrate Depth". The family's own spelling, derived rather than tabulated.</summary>
        internal static string RevitParameterName(string camelCaseKey)
        {
            if (string.IsNullOrEmpty(camelCaseKey)) return camelCaseKey;
            var sb = new System.Text.StringBuilder();
            sb.Append(char.ToUpperInvariant(camelCaseKey[0]));
            for (int i = 1; i < camelCaseKey.Length; i++)
            {
                if (char.IsUpper(camelCaseKey[i])) sb.Append(' ');
                sb.Append(camelCaseKey[i]);
            }
            return sb.ToString();
        }

        /* ── Finding the family ──────────────────────────────────────────── */

        private static FamilySymbol? FindLoaded(Document doc, string? familyName)
        {
            if (string.IsNullOrWhiteSpace(familyName)) return null;
            return new FilteredElementCollector(doc)
                .OfClass(typeof(FamilySymbol))
                .Cast<FamilySymbol>()
                .FirstOrDefault(s => string.Equals(s.Family?.Name, familyName, StringComparison.OrdinalIgnoreCase));
        }

        private static FamilySymbol? FirstSymbol(Family? family)
        {
            if (family == null) return null;
            var doc = family.Document;
            foreach (var id in family.GetFamilySymbolIds())
                if (doc.GetElement(id) is FamilySymbol s) return s;
            return null;
        }

        private static FamilySymbol Activate(FamilySymbol symbol)
        {
            if (!symbol.IsActive)
            {
                symbol.Activate();
                symbol.Document.Regenerate();
            }
            return symbol;
        }

        /// <summary>
        /// The family file that ships with the add-in. Beside the assembly in a
        /// normal install; the repository layout is tried too, so a developer
        /// running from a build output finds it without an install.
        /// </summary>
        internal static string? LibraryPath(string fileBase)
        {
            var dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            if (dir == null) return null;

            var candidates = new[]
            {
                Path.Combine(dir, "Library", "Families", fileBase + ".rfa"),
                Path.Combine(dir, "..", "Library", "Families", fileBase + ".rfa"),
                Path.Combine(dir, "..", "..", "..", "..", "Library", "Families", fileBase + ".rfa"),
            };
            foreach (var c in candidates)
            {
                var full = Path.GetFullPath(c);
                if (File.Exists(full)) return full;
            }
            return null;
        }

        /// <summary>
        /// Answers Revit's "this family is already loaded" prompt in code, so an
        /// import never stops to ask. Overwrite, values included: the library's
        /// copy is the one the add-in ships and should win over a stale one.
        /// </summary>
        private sealed class OverwriteFamilyLoadOptions : IFamilyLoadOptions
        {
            public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues)
            {
                overwriteParameterValues = true;
                return true;
            }

            public bool OnSharedFamilyFound(Family sharedFamily, bool familyInUse,
                out FamilySource source, out bool overwriteParameterValues)
            {
                source = FamilySource.Family;
                overwriteParameterValues = true;
                return true;
            }
        }
    }

    internal static class PlanterElementExtensions
    {
        /// <summary>The instance's type, for parameters the family holds on the type rather than the instance.</summary>
        public static FamilySymbol? Symbol(this Element element) =>
            element is FamilyInstance fi ? fi.Symbol : null;
    }
}
