using System.Text.Json;
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
        private readonly Dictionary<ZoneDto, FamilyResolution> _byZone = new(ReferenceEqualityComparer.Instance);

        internal void Set(PlacementDto p, FamilyResolution r) => _byPlacement[p] = r;
        internal void SetZone(ZoneDto z, FamilyResolution r) => _byZone[z] = r;

        public FamilyResolution For(PlacementDto p) =>
            _byPlacement.TryGetValue(p, out var r) ? r : FamilyResolution.None("no family was prepared for this piece (the import ran without the preparation step)");

        /// <summary>
        /// The tray a zone is built from. A zone with no tray is the normal case
        /// for a roof laid directly, so "none" here is an answer and not a fault.
        /// </summary>
        public FamilyResolution ForZone(ZoneDto z) =>
            _byZone.TryGetValue(z, out var r) ? r : FamilyResolution.None("no tray was chosen for this zone");

        public int Count => _byPlacement.Count;
        public int ZoneCount => _byZone.Count;
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
            bool needsBuilder = placements.Any(p => !IsFloor(p, assemblyKeys) && (p.Parameters?.Padel != null || p.Parameters?.Basketball != null || p.Parameters?.Volleyball != null || p.Parameters?.Football != null || p.Parameters?.PingPong != null || p.Parameters?.Calisthenics != null || p.Parameters?.Crossfit != null || p.Parameters?.Trx != null
                                                                                  || p.Parameters?.Vegetation != null || p.Parameters?.Furniture != null || p.Parameters?.DesignFamily != null
                                                                                  || SportifyFamilyGenerator.FamilyNameFor(p) != null));
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

            PrepareZoneTrays(doc, layout, prepared);

            SportifyLog.Info("families", $"{placements.Count} placement(s), {byKey.Count} distinct family key(s), " +
                                          $"{prepared.Count} resolved; failures: {placements.Count(p => prepared.For(p).SymbolId == null && !IsFloor(p, assemblyKeys))}");
            return prepared;
        }

        /// <summary>
        /// The green roof trays the zones are built from.
        ///
        /// Here rather than in the import for one hard reason: removing the
        /// build-up placeholder means editing the family document, and
        /// Document.EditFamily throws inside an open transaction — the same
        /// constraint that already forces worksharing to be settled before the
        /// import. Loading the family needs a transaction; stripping it must not
        /// be in one. So both happen here, in that order, before the import
        /// transaction opens.
        ///
        /// One resolution per distinct family, not per zone: forty zones of the
        /// same tray edit the family once.
        /// </summary>
        private static void PrepareZoneTrays(Document doc, SportifyLayout layout, PreparedFamilies prepared)
        {
            var zones = (layout.Zones ?? new List<ZoneDto>()).Where(z => z.Family?.Key != null).ToList();
            if (zones.Count == 0) return;

            SportifyGreenRoofModuleBuilder.BeginImport();
            var byKey = new Dictionary<string, FamilyResolution>(StringComparer.OrdinalIgnoreCase);

            foreach (var zone in zones)
            {
                var fam = zone.Family!;
                var label = zone.Label ?? zone.Kind ?? "(zone)";

                if (byKey.TryGetValue(fam.Key!, out var shared)) { prepared.SetZone(zone, shared); continue; }

                FamilySymbol? symbol = null;
                using (var t = new Transaction(doc, "Sportify: tray for " + label))
                {
                    try
                    {
                        t.Start();
                        // The tray is one of the design team's families, so it is
                        // found and loaded by exactly the same path as a planter.
                        symbol = SportifyPlanterFamilyBuilder.GetOrLoadSymbol(doc, new DesignFamilyDto
                        {
                            Type = fam.Key,
                            Family = fam.Family,
                            Label = fam.Type,
                            Units = fam.Units,
                        });
                        if (symbol != null) t.Commit(); else t.RollBack();
                    }
                    catch (Exception) { try { t.RollBack(); } catch (Exception) { } }
                }

                if (symbol == null)
                {
                    var none = FamilyResolution.None($"the green roof family \"{fam.Family}\" is neither in the project nor in the add-in's library");
                    byKey[fam.Key!] = none;
                    prepared.SetZone(zone, none);
                    ImportDiagnostics.FloorFailed(label, none.Reason!);
                    continue;
                }

                // Outside any transaction, as EditFamily requires. A failure here
                // is reported but does not cost us the tray: a tray with its
                // placeholder still in is worse than one without, but far better
                // than no tray at all, and the report says which it is.
                // Read before the strip: after a reload the symbol handle is a
                // dead element, and even asking it for its own name throws.
                var familyName = symbol.Family?.Name;
                var typeName = symbol.Name;
                var symbolId = symbol.Id;

                if (fam.StripGenericModel)
                {
                    bool ok = SportifyGreenRoofModuleBuilder.StripPlaceholder(doc, symbol, out string note, out var freshId);
                    SportifyLog.Info("greenroof", $"{fam.Family}: {note}");
                    if (!ok) ImportDiagnostics.FloorFailed(label, $"the build-up was drawn but {note}");

                    // Take the id the strip hands back. Reloading the family
                    // replaces it and its types with NEW elements, so the id we
                    // walked in with now points at something Revit has deleted.
                    // Trusting it is what made the tray vanish silently the first
                    // time this ran.
                    if (freshId != null) symbolId = freshId;
                }

                var resolved = new FamilyResolution
                {
                    SymbolId = symbolId,
                    How = "the design team's green roof family",
                    FamilyName = familyName,
                    TypeName = typeName,
                };
                byKey[fam.Key!] = resolved;
                prepared.SetZone(zone, resolved);
            }

            SportifyLog.Info("greenroof", $"{zones.Count} zone(s) with a tray, {byKey.Count} distinct family/families");
        }

        /// <summary>
        /// What distinguishes one configuration of a design family from another.
        ///
        /// Short and readable rather than a hash, because it becomes part of a
        /// Revit type name and someone has to recognise it in the project
        /// browser: "Sprint Lane · Lane_Length 63000" says what it is.
        ///
        /// Only the numbers are taken. A material or a yes/no changes what the
        /// family looks like, not what size it is, and folding those in would
        /// multiply types for no gain.
        /// </summary>
        private static string DesignFamilySignature(DesignFamilyDto df)
        {
            if (df.Params == null || df.Params.Count == 0) return "default";
            var parts = df.Params
                .Where(kv => kv.Value.ValueKind == JsonValueKind.Number)
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => $"{kv.Key} {kv.Value.GetRawText()}");
            var s = string.Join(", ", parts);
            return string.IsNullOrEmpty(s) ? "default" : s;
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
            // One product placed many times is one family — but only when it is
            // configured the SAME way. The key used to be the product alone, and
            // the comment here claimed it carried the size when it did not: two
            // sprint lanes of different lengths shared one resolution, hence one
            // type, hence one length. The parameters are part of the identity.
            if (p.Parameters?.DesignFamily is { } df && !string.IsNullOrWhiteSpace(df.Type))
                return "designfamily::" + df.Type + "::" + DesignFamilySignature(df);
            if (p.Parameters?.Furniture is { } furniture && !string.IsNullOrWhiteSpace(furniture.Key))
                return "furniture::" + FurnitureShape.FamilyName(furniture.RevitFamilyName, furniture.Label ?? furniture.Product, furniture.Key, furniture.LengthM, furniture.WidthM, furniture.HeightM);
            if (p.Parameters?.Padel != null || p.Parameters?.Basketball != null || p.Parameters?.Volleyball != null || p.Parameters?.Football != null || p.Parameters?.PingPong != null || p.Parameters?.Calisthenics != null || p.Parameters?.Crossfit != null || p.Parameters?.Trx != null || p.Parameters?.Vegetation != null || p.Parameters?.RevitFamily != null || p.Parameters?.Furniture != null)
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
            if (p.Parameters?.Crossfit is { } cf)
            {
                var s = SportifyCrossfitRigBuilder.GetOrCreateSymbol(doc, cf);
                if (s == null) return FamilyResolution.None("the CrossFit rig builder returned no family");
                ImportDiagnostics.CrossfitBuilt(cf.Bays, cf.DoubleSided, cf.Stations,
                    cf.RigLengthM, cf.RigWidthM, cf.WeightKg);
                return Found(s, ImportDiagnostics.HowGenerated);
            }
            if (p.Parameters?.Trx is { } trx)
            {
                var s = SportifyTrxFrameBuilder.GetOrCreateSymbol(doc, trx);
                if (s == null) return FamilyResolution.None("the suspension frame builder returned no family");
                ImportDiagnostics.TrxBuilt(trx.AnchorCount, trx.AFrame, trx.FrameLengthM,
                    trx.FrameHeightM, trx.WeightKg);
                return Found(s, ImportDiagnostics.HowGenerated);
            }
            if (p.Parameters?.Calisthenics is { } rig)
            {
                var s = SportifyCalisthenicsRigBuilder.GetOrCreateSymbol(doc, rig);
                if (s == null) return FamilyResolution.None("the calisthenics builder returned no family");
                ImportDiagnostics.CalisthenicsBuilt(rig.Bays, rig.RigLengthM, rig.RigWidthM,
                    rig.FrameHeightM, rig.RungCount, rig.WeightKg);
                return Found(s, ImportDiagnostics.HowGenerated);
            }
            if (p.Parameters?.PingPong is { } pingPong)
            {
                var s = SportifyPingPongBuilder.GetOrCreateSymbol(doc, pingPong);
                if (s == null) return FamilyResolution.None("the table tennis builder returned no family");
                ImportDiagnostics.PingPongBuilt(pingPong.PlayingSpace ?? "recreational", pingPong.Table ?? "",
                    pingPong.LengthM, pingPong.WidthM, pingPong.WeightKg);
                return Found(s, ImportDiagnostics.HowGenerated);
            }

            if (p.Parameters?.Football is { } football)
            {
                var s = SportifyFootballCourtBuilder.GetOrCreateSymbol(doc, football);
                if (s == null) return FamilyResolution.None("the football court builder returned no family");
                ImportDiagnostics.FootballCourtBuilt(football.CourtType ?? "futsal", football.Surface ?? "",
                    football.Boards ?? "none", football.PlayLengthM, football.PlayWidthM, football.WeightKg);
                return Found(s, ImportDiagnostics.HowGenerated);
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

            // 4a-ii. A design team family is not built, it is found: theirs, loaded
            //        from the library that ships with the add-in. The instance is
            //        configured afterwards (SportifyFamilyParameters.SetInstanceValues).
            if (p.Parameters?.DesignFamily is { } block)
            {
                var s = SportifyPlanterFamilyBuilder.GetOrLoadSymbol(doc, block);
                if (s == null)
                    return FamilyResolution.None($"the family for \"{block.Label ?? block.Type}\" is not loaded and is not in the add-in's library");

                // These families hold their dimensions as TYPE parameters, so
                // every instance of one type shares one set of numbers. Two of
                // the same product configured differently therefore need two
                // types, or the second silently rewrites the first.
                s = SportifyGreenRoofModuleBuilder.SymbolForConfiguration(
                        doc, s, block, DesignFamilySignature(block), out string typeNote);
                if (!string.IsNullOrEmpty(typeNote)) SportifyLog.Info("families", $"{block.Label ?? block.Type}: {typeNote}");

                return Found(s, ImportDiagnostics.HowReference);
            }

            // 4b. Furniture is its product's family: the firm's own of that product when the project has one, else one built from the catalogue size (FurnitureShape).
            if (p.Parameters?.Furniture is { } furniture)
            {
                var s = SportifyFurnitureFamilyBuilder.GetOrCreateSymbol(doc, furniture, out var firmsOwn);
                return s == null ? FamilyResolution.None("the furniture family builder returned no family") : Found(s, firmsOwn ? ImportDiagnostics.HowReference : ImportDiagnostics.HowFurniture);
            }

            // 5. A family the designer referenced in the Revit families tab.
            if (p.Parameters?.RevitFamily is { } reference)
            {
                var s = RevitFamilyResolver.Resolve(doc, reference);
                if (s != null) return Found(s, ImportDiagnostics.HowReference);
                return FamilyResolution.None("the referenced family \"" + (reference.FamilyName ?? "(unnamed)") + "\" is not loaded in this project");
            }

            var qualityKey = p.Parameters?.QualityKey;
            if (string.IsNullOrWhiteSpace(qualityKey))
                return FamilyResolution.None("it has no quality_key, and nothing builds a family for this kind of piece yet");

            // 6. A family a person named for this quality_key (FamilyMatchRules): already in the project, or loaded from the library
            //    that ships with the add-in — the same "project wins, else the library" rule the design-team kit items use.
            if (FamilyMatchRules.Lookup.TryGetValue(qualityKey, out var curated))
            {
                var matched = SportifyPlanterFamilyBuilder.FindOrLoad(doc, curated, curated);
                if (matched != null) return Found(matched, ImportDiagnostics.HowMatched);
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
