using Autodesk.Revit.DB;

namespace SportfyRevit
{
    /// <summary>What a placement gets: a family (found, built or referenced) or, with the reason, none.</summary>
    internal sealed class FamilyResolution
    {
        public ElementId? SymbolId { get; init; }
        /// <summary>One of the ImportDiagnostics.How... texts.</summary>
        public string How { get; init; } = "";
        public string? FamilyName { get; init; }
        public string? TypeName { get; init; }
        /// <summary>Why there is no family: what the placeholder box report says.</summary>
        public string? Reason { get; init; }

        public static FamilyResolution None(string reason) => new() { Reason = reason };
    }

    /// <summary>The answers of FamilyPreparation, one per placement (by reference: the layout object lives through both phases).</summary>
    internal sealed class PreparedFamilies
    {
        private readonly Dictionary<PlacementDto, FamilyResolution> _byPlacement = new(ReferenceEqualityComparer.Instance);

        internal void Set(PlacementDto p, FamilyResolution r) => _byPlacement[p] = r;

        public FamilyResolution For(PlacementDto p) =>
            _byPlacement.TryGetValue(p, out var r) ? r : FamilyResolution.None("no family was prepared for this piece (the import ran without the preparation step)");

        public int Count => _byPlacement.Count;
    }

    /// <summary>
    /// The families of a layout, settled BEFORE the import transaction opens.
    ///
    /// Building a family is real document mutation with real failure modes (a new family document, geometry, ~28 parameters, save, LoadFamily,
    /// Activate), and it used to happen inside the import transaction, piece by piece, with every failure swallowed. Now each distinct family is
    /// resolved here in a small transaction of its own, so a family that cannot be built is one failed line in the report and cannot take the import
    /// with it; the import itself only places what was resolved. The order of the decision is the old one minus the fuzzy matching:
    ///   a specified sport (padel, basketball, volleyball: its own builder), a plant (its species family), a family the designer referenced,
    ///   a family that is already in the project and is EXACTLY this piece's (a curated rule, or the generated one for its quality_key and size),
    ///   otherwise a generated family, otherwise a placeholder box and the reason why.
    /// There is no keyword matching: sharing one word with some loaded family used to be enough to place a piece as that family.
    /// </summary>
    internal static class FamilyPreparation
    {
        public static PreparedFamilies Prepare(Document doc, SportifyLayout layout, bool allowTemplateDialog)
        {
            var prepared = new PreparedFamilies();
            var placements = layout.Placements ?? new List<PlacementDto>();
            var assemblyKeys = new HashSet<string>((layout.Assemblies ?? new List<AssemblyDto>()).Where(a => a.Key != null).Select(a => a.Key!), StringComparer.OrdinalIgnoreCase);

            // The template first, once, outside any transaction (it may open a file, and asks a person only for a manual import).
            bool needsBuilder = placements.Any(p => !IsFloor(p, assemblyKeys) && (p.Parameters?.Padel != null || p.Parameters?.Basketball != null || p.Parameters?.Volleyball != null
                                                                                  || p.Parameters?.Vegetation != null || p.Parameters?.Furniture != null || SportifyFamilyGenerator.FamilyNameFor(p) != null));
            if (needsBuilder) GenericFamilyTemplateLocator.Prepare(doc.Application, allowTemplateDialog);

            var byKey = new Dictionary<string, FamilyResolution>(StringComparer.OrdinalIgnoreCase);
            TransactionGroup? group = null;
            try
            {
                foreach (var p in placements)
                {
                    var label = p.Label ?? p.Id ?? "(piece)";
                    if (IsFloor(p, assemblyKeys))
                    {
                        prepared.Set(p, FamilyResolution.None("it is a parcel drawn as a floor from its build-up"));
                        continue;
                    }

                    var key = KeyOf(p);
                    if (key != null && byKey.TryGetValue(key, out var shared))
                    {
                        prepared.Set(p, shared);
                        continue;
                    }

                    group ??= StartGroup(doc);
                    var resolution = ResolveInOwnTransaction(doc, p, label);
                    if (key != null) byKey[key] = resolution;
                    prepared.Set(p, resolution);
                }
            }
            finally
            {
                EndGroup(group);
            }

            SportifyLog.Info("families", $"{placements.Count} placement(s), {byKey.Count} distinct family key(s), " +
                                          $"{prepared.Count} resolved; failures: {placements.Count(p => prepared.For(p).SymbolId == null && !IsFloor(p, assemblyKeys))}");
            return prepared;
        }

        /// <summary>A parcel that names a build-up the layout carries becomes a Floor, not a family (SportifyLayoutBuilder.TryCreateAssemblyFloor).</summary>
        private static bool IsFloor(PlacementDto p, HashSet<string> assemblyKeys)
        {
            var key = p.Parameters?.Garden?.Assembly?.Key;
            return key != null && assemblyKeys.Contains(key);
        }

        /// <summary>What makes two placements the same family: the specified sports by their own name, the rest by generated name; null = do not share.</summary>
        private static string? KeyOf(PlacementDto p)
        {
            // one product placed many times is one family: shared by the product's key and size
            if (p.Parameters?.Furniture is { } furniture && !string.IsNullOrWhiteSpace(furniture.Key))
                return "furniture::" + FurnitureShape.FamilyName(furniture.RevitFamilyName, furniture.Label ?? furniture.Product, furniture.Key, furniture.LengthM, furniture.WidthM, furniture.HeightM);
            if (p.Parameters?.Padel != null || p.Parameters?.Basketball != null || p.Parameters?.Volleyball != null || p.Parameters?.Vegetation != null || p.Parameters?.RevitFamily != null || p.Parameters?.Furniture != null)
                return null;   // their builders cache by themselves; a shared key would have to repeat their naming
            return SportifyFamilyGenerator.FamilyNameFor(p);
        }

        private static FamilyResolution ResolveInOwnTransaction(Document doc, PlacementDto p, string label)
        {
            using var t = new Transaction(doc, "Sportify: family for " + label);
            try
            {
                t.Start();
                var resolution = Resolve(doc, p, label);
                if (t.GetStatus() == TransactionStatus.Started) t.Commit();
                return resolution;
            }
            catch (Exception ex)
            {
                try { if (t.GetStatus() == TransactionStatus.Started) t.RollBack(); } catch (Exception) { /* already gone */ }
                SportifyLog.Error("families", "family for \"" + label + "\" failed", ex);
                return FamilyResolution.None("the family could not be built: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static FamilyResolution Resolve(Document doc, PlacementDto p, string label)
        {
            // 1-3. What has its own builder. They cache by themselves and never start a transaction of their own, so this transaction is theirs.
            if (p.Parameters?.Padel is { } padel)
            {
                var s = SportifyPadelCourtBuilder.GetOrCreateSymbol(doc, padel);
                if (s == null) return FamilyResolution.None("the padel court builder returned no family");
                ImportDiagnostics.PadelCourtBuilt(padel.CourtType ?? "double", padel.WallSystem ?? "panoramic", padel.Surface ?? "", padel.WeightKg);
                return Found(s, ImportDiagnostics.HowSpecified);
            }
            if (p.Parameters?.Basketball is { } basket)
            {
                var s = SportifyBasketballCourtBuilder.GetOrCreateSymbol(doc, basket);
                if (s == null) return FamilyResolution.None("the basketball court builder returned no family");
                ImportDiagnostics.BasketballCourtBuilt(basket.Variant ?? "standard", basket.Hoops, basket.Mounting ?? "", basket.Surface ?? "", basket.WeightKg);
                return Found(s, ImportDiagnostics.HowSpecified);
            }
            if (p.Parameters?.Volleyball is { } volley)
            {
                var s = SportifyVolleyballCourtBuilder.GetOrCreateSymbol(doc, volley);
                if (s == null) return FamilyResolution.None("the volleyball court builder returned no family");
                ImportDiagnostics.VolleyballCourtBuilt(volley.PlayType ?? "indoor", volley.NetHeightM, volley.Surface ?? "", volley.SandVolumeM3, volley.WeightKg);
                return Found(s, ImportDiagnostics.HowSpecified);
            }

            // 4. A plant is its species family.
            if (p.Parameters?.Vegetation is { } plant)
            {
                var s = SportifyPlantFamilyBuilder.GetOrCreateSymbol(doc, plant);
                return s == null ? FamilyResolution.None("the species family builder returned no family") : Found(s, ImportDiagnostics.HowPlant);
            }

            // 4b. Furniture is its product's family: the firm's own of that product when the project has one, else one built from the catalogue size (FurnitureShape).
            if (p.Parameters?.Furniture is { } furniture)
            {
                var s = SportifyFurnitureFamilyBuilder.GetOrCreateSymbol(doc, furniture, out var firmsOwn);
                return s == null ? FamilyResolution.None("the furniture family builder returned no family") : Found(s, firmsOwn ? ImportDiagnostics.HowReference : ImportDiagnostics.HowFurniture);
            }

            // 5. A family the designer referenced in the Families tab.
            if (p.Parameters?.RevitFamily is { } reference)
            {
                var s = RevitFamilyResolver.Resolve(doc, reference);
                if (s != null) return Found(s, ImportDiagnostics.HowReference);
                return FamilyResolution.None("the referenced family \"" + (reference.FamilyName ?? "(unnamed)") + "\" is not loaded in this project");
            }

            var qualityKey = p.Parameters?.QualityKey;
            if (string.IsNullOrWhiteSpace(qualityKey))
                return FamilyResolution.None("it has no quality_key, and nothing builds a family for this kind of piece yet");

            // 6. A family a person named for this quality_key (FamilyMatchRules), when it is in the project.
            if (FamilyMatchRules.Lookup.TryGetValue(qualityKey, out var curated))
            {
                var loaded = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
                    .FirstOrDefault(s => string.Equals(s.Family?.Name, curated, StringComparison.OrdinalIgnoreCase));
                if (loaded != null) return Found(loaded, ImportDiagnostics.HowMatched);
            }

            // 7. The family generated for exactly this quality_key and size, when an earlier import already put it in the project.
            var familyName = SportifyFamilyGenerator.FamilyNameFor(p)!;
            var existing = SportifyFamilyGenerator.FindLoaded(doc, familyName);
            if (existing != null) return Found(existing, ImportDiagnostics.HowMatched);

            // 8. Generate it. Any failure is a thrown exception with the reason, recorded by the caller.
            var generated = SportifyFamilyGenerator.GetOrCreateSymbol(doc, p);
            return Found(generated, ImportDiagnostics.HowGenerated);
        }

        private static FamilyResolution Found(FamilySymbol symbol, string how) => new()
        {
            SymbolId = symbol.Id,
            How = how,
            FamilyName = symbol.Family?.Name ?? "(family)",
            TypeName = symbol.Name,
        };

        private static TransactionGroup? StartGroup(Document doc)
        {
            try
            {
                var group = new TransactionGroup(doc, "Sportify: prepare families");
                group.Start();
                return group;
            }
            catch (Exception ex)
            {
                SportifyLog.Warn("families", "could not group the family transactions: " + ex.Message);
                return null;
            }
        }

        /// <summary>One undo step for everything the preparation loaded, when it loaded anything.</summary>
        private static void EndGroup(TransactionGroup? group)
        {
            if (group == null) return;
            try
            {
                if (group.GetStatus() != TransactionStatus.Started) return;
                if (group.Assimilate() != TransactionStatus.Committed) SportifyLog.Warn("families", "the family transaction group did not commit");
            }
            catch (Exception ex)
            {
                SportifyLog.Warn("families", "closing the family transaction group: " + ex.Message);
                try { if (group.GetStatus() == TransactionStatus.Started) group.RollBack(); } catch (Exception) { /* nothing more to do */ }
            }
        }
    }
}
