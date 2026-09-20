using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;

namespace SportfyRevit
{
    /// <summary>
    /// Everything about turning one placement into a Revit element: matching
    /// it to a loaded FamilySymbol (or falling back to a placeholder box),
    /// placing it, and labeling it. Split out of SportifyLayoutBuilder so the
    /// family-implementation work (this file) and the sync/orchestration work
    /// (SportifyLayoutBuilder, AutoImportSync, RoofBoundaryServer) live in
    /// separate files — this is the one to extend when adding real parametric
    /// family support; the roof boundary/setback/circulation/entry-marker
    /// geometry and the sync pipeline that calls into this class stay in
    /// SportifyLayoutBuilder.cs and are not part of this file's job.
    /// </summary>
    internal static class FamilyPlacementBuilder
    {
        private const double ThicknessM = 0.1;

        /// <summary>
        /// Single entry point SportifyLayoutBuilder calls per placement: try a
        /// real family first, fall back to the placeholder box if nothing
        /// loaded matches well enough.
        /// </summary>
        public static void PlaceComponent(
            Document doc, PlacementDto p, BoundingBoxDto bb, bool isGarden,
            double originXFt, double originYFt, WorksetId worksetId, ElementId textTypeId, List<ElementId> createdIds,
            double? fireSafetyDistanceM)
        {
            // Priority: an already-loaded family a human curated/named well
            // enough to match, wins outright (FindMatchingFamilySymbol's own
            // exact-quality_key-then-keyword-scoring order, unchanged) —
            // then a real SportifyFamilyGenerator-built family — and only if
            // that itself fails does this fall back to the placeholder box.
            // An explicit revit_family reference outranks everything: the user
            // picked that family in the Families tab and configured it there,
            // so there is nothing left to infer. Only a reference that can't be
            // honored (family not loaded in this document) falls through to
            // keyword matching and generation.
            // A plant resolves to its species family before anything else: the
            // species IS the answer, so keyword matching and the generic
            // extrusion generator have nothing to contribute.
            // A specified sport goes first: a padel court is not an extrusion of
            // its footprint, and the generic path would happily make one.
            var symbol = TryResolvePadelCourt(doc, p)
                         ?? TryResolvePlantFamily(doc, p)
                         ?? TryResolveExplicitFamily(doc, p);
            if (symbol != null) { /* diagnostics recorded inside the resolver */ }
            else
            {
                symbol = FindMatchingFamilySymbol(doc, p, GetPlacementKeywords(p));
                if (symbol != null) ImportDiagnostics.KeywordMatched(p.Label ?? p.Id ?? "(piece)");
                else
                {
                    symbol = TryGenerateSymbol(doc, p);
                    if (symbol != null) ImportDiagnostics.Generated(p.Label ?? p.Id ?? "(piece)");
                    else ImportDiagnostics.Placeholder(p.Label ?? p.Id ?? "(piece)");
                }
            }
            Element placed = symbol != null
                ? PlaceFamilyInstance(doc, p, bb, symbol, originXFt, originYFt, worksetId, textTypeId, createdIds)
                : PlacePlaceholderBox(doc, p, bb, isGarden, originXFt, originYFt, worksetId, textTypeId, createdIds);

            // Stamps the same unified parameter set on whatever got placed —
            // real family instance or placeholder box alike — so Revit's own
            // Properties panel/Schedules can already query category/quality/
            // material data today, without waiting on real "sportified"
            // families to exist. Defensive: EnsureBound returns false (and
            // SetValues is then a no-op) if the shared-parameter file/binding
            // fails for any reason — geometry placement above must never be
            // broken by this.
            if (SportifySharedParameters.EnsureBound(doc))
            {
                var (typeId, variant, norm, lengthM, widthM) = PlacementDataHelpers.GetUnifiedFields(p);
                SportifySharedParameters.SetValues(placed, p.Category, typeId, variant,
                    PlacementDataHelpers.GetQualityLevel(p), norm, lengthM, widthM,
                    PlacementDataHelpers.GetReferenceMaterialName(p), PlacementDataHelpers.GetReferenceProviderName(p),
                    p.Parameters?.QualityKey);
            }

            // The richer generalities/materials(+LCA+provider)/analysis set —
            // safe no-op wherever these Sportify_* parameters don't exist (a
            // matched family that isn't one of ours, or the placeholder box);
            // only actually populates on a SportifyFamilyGenerator instance.
            SportifyFamilyParameters.SetInstanceValues(placed, p, fireSafetyDistanceM);
        }

        /// <summary>
        /// Best-effort: family generation is a real Revit document mutation
        /// (new family document, geometry, ~28 parameters, save, load) with
        /// no dry-run and genuine failure modes (template truly
        /// unresolvable, disk/permissions) — any failure here must fall
        /// through to the placeholder box rather than abort the whole
        /// placement, same defensive posture as SportifySharedParameters.
        /// </summary>
        /// <summary>
        /// Best-effort, same defensive posture as TryGenerateSymbol: resolving
        /// a reference can duplicate a family type and write parameters, both
        /// real document mutations with genuine failure modes. A failure here
        /// must degrade to the normal matching path rather than abort the
        /// placement — the piece still belongs on the roof either way.
        /// </summary>
        /// <summary>
        /// Best-effort, same posture as the other resolvers: generating a family
        /// document is real work with real failure modes, and a plant that
        /// cannot be built should still reach the roof as something rather than
        /// stopping the import.
        /// </summary>
        /// <summary>
        /// Same best-effort posture as the plant resolver: a court that cannot
        /// be built should still reach the roof as something rather than
        /// stopping the import — but it is reported, because a padel court
        /// arriving as a slab is a quieter failure than none at all.
        /// </summary>
        private static FamilySymbol? TryResolvePadelCourt(Document doc, PlacementDto p)
        {
            var padel = p.Parameters?.Padel;
            if (padel == null) return null;

            try
            {
                var symbol = SportifyPadelCourtBuilder.GetOrCreateSymbol(doc, padel);
                if (symbol != null)
                    ImportDiagnostics.PadelCourtBuilt(padel.CourtType ?? "double",
                        padel.WallSystem ?? "panoramic", padel.Surface ?? "", padel.WeightKg);
                return symbol;
            }
            catch (Exception ex)
            {
                ImportDiagnostics.ExplicitFailed("Padel court", $"{ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        private static FamilySymbol? TryResolvePlantFamily(Document doc, PlacementDto p)
        {
            var plant = p.Parameters?.Vegetation;
            if (plant == null) return null;

            try
            {
                var symbol = SportifyPlantFamilyBuilder.GetOrCreateSymbol(doc, plant);
                if (symbol != null)
                    ImportDiagnostics.PlantPlaced(plant.BotanicalName ?? "(plant)", plant.CrownM, plant.HeightM);
                return symbol;
            }
            catch (Exception ex)
            {
                ImportDiagnostics.ExplicitFailed(plant.BotanicalName ?? "(plant)",
                    $"{ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        private static FamilySymbol? TryResolveExplicitFamily(Document doc, PlacementDto p)
        {
            var reference = p.Parameters?.RevitFamily;
            if (reference == null) return null;

            try
            {
                return RevitFamilyResolver.Resolve(doc, reference);
            }
            catch (Exception ex)
            {
                // Still non-fatal — but recorded, not swallowed. A silently
                // discarded exception here is indistinguishable from "the user
                // didn't ask for a family", which cost an evening of guessing.
                ImportDiagnostics.ExplicitFailed(reference.FamilyName ?? "(unnamed)",
                    $"{ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        private static FamilySymbol? TryGenerateSymbol(Document doc, PlacementDto p)
        {
            try
            {
                return SportifyFamilyGenerator.GetOrCreateSymbol(doc, p);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// Words split out of a placement's category/label/quality_key,
        /// used to score every loaded FamilySymbol's family name and pick
        /// the closest match. quality_key (e.g. "BASKETBALL_STANDARD_HIGH",
        /// "GARDEN_ROOF_TREES_JAPANESE_HIGH") is included because it's
        /// already the frontend's own designed-for-lookup identifier (see
        /// ParametersDto) — label alone is free text a human wrote for the
        /// sidebar, not a stable key.
        /// </summary>
        private static HashSet<string> GetPlacementKeywords(PlacementDto p)
        {
            var words = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            words.UnionWith(ExtractWords(p.Category));
            words.UnionWith(ExtractWords(p.Label));
            words.UnionWith(ExtractWords(p.Parameters?.QualityKey));
            return words;
        }

        private static readonly Regex WordSplitter = new(@"[^A-Za-z0-9]+", RegexOptions.Compiled);

        private static IEnumerable<string> ExtractWords(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) yield break;
            foreach (var word in WordSplitter.Split(text))
            {
                if (word.Length >= 2)
                    yield return word;
            }
        }

        /// <summary>
        /// Checks FamilyMatchRules.Lookup for an exact quality_key match
        /// first (empty today, so this is a no-op until Moamen fills it in —
        /// see that file's own TODO), then falls back to best-effort keyword
        /// scoring: scores every FamilySymbol currently loaded in the
        /// document by how many keywords its family name shares with this
        /// placement, and returns the top scorer (null if nothing shares
        /// even one word). Deliberately generic rather than a hardcoded
        /// sport-name table, since this project has no bundled family
        /// content — it has to work with whatever family names the user
        /// loads in, whatever they're called.
        /// </summary>
        private static FamilySymbol? FindMatchingFamilySymbol(Document doc, PlacementDto p, HashSet<string> keywords)
        {
            var qualityKey = p.Parameters?.QualityKey;
            if (!string.IsNullOrWhiteSpace(qualityKey) && FamilyMatchRules.Lookup.TryGetValue(qualityKey, out var exactFamilyName))
            {
                var exact = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
                    .FirstOrDefault(s => string.Equals(s.Family?.Name, exactFamilyName, StringComparison.OrdinalIgnoreCase));
                if (exact != null) return exact;
            }

            if (keywords.Count == 0) return null;

            FamilySymbol? best = null;
            int bestScore = 0;
            var symbols = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>();
            foreach (var symbol in symbols)
            {
                var familyName = symbol.Family?.Name;
                if (string.IsNullOrWhiteSpace(familyName)) continue;

                int score = ExtractWords(familyName).Count(w => keywords.Contains(w));
                if (score > bestScore)
                {
                    bestScore = score;
                    best = symbol;
                }
            }
            return best;
        }

        /// <summary>
        /// The real-family path: places an instance of a FamilySymbol the
        /// user already loaded, at the frontend's purpose-built center
        /// point (falling back to the bounding box's center for exports
        /// from before insertion_point existed), rotates it to match the
        /// Combine layout, and labels it with the family's own name — the
        /// "assign a family + name annotation" behavior this replaces the
        /// placeholder box with.
        /// </summary>
        private static Element PlaceFamilyInstance(
            Document doc, PlacementDto p, BoundingBoxDto bb, FamilySymbol symbol,
            double originXFt, double originYFt, WorksetId worksetId, ElementId textTypeId, List<ElementId> createdIds)
        {
            if (!symbol.IsActive)
            {
                symbol.Activate();
                doc.Regenerate();
            }

            double centerXFt, centerYFt;
            if (p.InsertionPoint != null)
            {
                centerXFt = originXFt + SportifyLayoutBuilder.FeetFromMeters(p.InsertionPoint.CenterXM);
                centerYFt = SportifyLayoutBuilder.WorldYFt(originYFt, p.InsertionPoint.CenterYM);
            }
            else
            {
                centerXFt = originXFt + SportifyLayoutBuilder.FeetFromMeters(bb.TopLeftXM + bb.WidthM / 2.0);
                centerYFt = SportifyLayoutBuilder.WorldYFt(originYFt, bb.TopLeftYM + bb.HeightM / 2.0);
            }
            var center = new XYZ(centerXFt, centerYFt, SportifyLayoutBuilder.CurrentOriginZFt);

            var instance = doc.Create.NewFamilyInstance(center, symbol, StructuralType.NonStructural);
            SportifyLayoutBuilder.SetWorkset(instance, worksetId);
            createdIds.Add(instance.Id);

            // NewFamilyInstance does NOT honor the Z of the point it is given:
            // a family instance is anchored to a level, and its height comes from
            // that level plus an offset parameter, so Revit quietly drops the
            // instance onto the active level. Explicit geometry (the roof outline,
            // setback, circulation lines) landed at the right elevation while every
            // court sat on the ground — the difference is exactly this.
            //
            // Rather than guess which parameter governs height for an arbitrary
            // customer family (Offset from Level, Elevation from Level, Height
            // Offset, or none at all), place it, measure where Revit actually put
            // it, and move it the remaining distance. Works for any family.
            MoveToElevation(doc, instance, center.Z);

            double rotationDeg = p.Transform?.RotationDeg ?? 0;
            if (Math.Abs(rotationDeg) > 1e-6)
            {
                // Negated because the Y axis is flipped on the way in (see
                // SportifyLayoutBuilder.WorldYFt). Mirroring a plan reverses the
                // sense of rotation, so a clockwise turn on the web canvas is a
                // counter-clockwise turn in Revit — applying the angle unchanged
                // would leave every rotated piece turned the wrong way.
                double rotationRad = -rotationDeg * Math.PI / 180.0;
                var axis = Line.CreateBound(center, center + XYZ.BasisZ);
                ElementTransformUtils.RotateElement(doc, instance.Id, axis, rotationRad);
            }

            // Prefer the placement's own human label (matches PlacePlaceholderBox's
            // BuildLabel, which already reads p.Label first) — a generated family's
            // Family Name is the raw quality_key (e.g. "BASKETBALL_STANDARD_HIGH"),
            // already available as the Sportify_QualityKey parameter, so the on-model
            // annotation is more useful showing what the web app itself calls this piece.
            string labelText = !string.IsNullOrWhiteSpace(p.Label) ? p.Label! : (symbol.Family?.Name ?? symbol.Name);
            var textOrigin = new XYZ(centerXFt, centerYFt, SportifyLayoutBuilder.CurrentOriginZFt + SportifyLayoutBuilder.FeetFromMeters(ThicknessM));
            CreateLabelText(doc, labelText, textOrigin, textTypeId, worksetId, createdIds);

            return instance;
        }

        /// <summary>
        /// Nudges a just-placed instance so its insertion point sits at
        /// targetZFt, whatever level Revit anchored it to.
        ///
        /// Regenerate first: a freshly created element's Location is not
        /// resolved until the document catches up, so reading it before that
        /// gives the point it was requested at rather than where it ended up —
        /// and the correction would compute a delta of zero.
        /// </summary>
        private static void MoveToElevation(Document doc, Element instance, double targetZFt)
        {
            try
            {
                doc.Regenerate();
                if (instance.Location is not LocationPoint locationPoint) return;

                double deltaZ = targetZFt - locationPoint.Point.Z;
                if (Math.Abs(deltaZ) < 1e-9) return;

                ElementTransformUtils.MoveElement(doc, instance.Id, new XYZ(0, 0, deltaZ));
            }
            catch (Exception)
            {
                // A pinned or otherwise immovable instance still belongs on the
                // roof plan — leave it where Revit put it rather than failing the
                // whole import over one piece.
            }
        }

        /// <summary>
        /// bounding_box.width_m/height_m from the frontend already reflect
        /// the item's final on-roof orientation (Combine's getFootprint()
        /// swaps width/height itself for a 90°-rotated item before export)
        /// — so a plain axis-aligned rectangle built straight from those
        /// dimensions is already correctly oriented. Applying transform.
        /// rotation_deg on top would rotate it a second time, so — unlike
        /// PlaceFamilyInstance, which needs it — that field stays unused
        /// here. This is the fallback for whenever PlaceComponent has no
        /// loaded family to match: still just a labeled box standing in
        /// for a real, non-square family with its own "front".
        /// </summary>
        private static Element PlacePlaceholderBox(
            Document doc, PlacementDto p, BoundingBoxDto bb, bool isGarden,
            double originXFt, double originYFt, WorksetId worksetId, ElementId textTypeId, List<ElementId> createdIds)
        {
            double xFt = originXFt + SportifyLayoutBuilder.FeetFromMeters(bb.TopLeftXM);
            // The canvas box's TOP edge is its LOWEST Y once flipped, so the
            // corner this rectangle is built from is the bottom-left in Revit.
            double yFt = SportifyLayoutBuilder.WorldYFt(originYFt, bb.TopLeftYM + bb.HeightM);
            double wFt = SportifyLayoutBuilder.FeetFromMeters(bb.WidthM);
            double hFt = SportifyLayoutBuilder.FeetFromMeters(bb.HeightM);
            double thickFt = SportifyLayoutBuilder.FeetFromMeters(ThicknessM);

            var loop = new CurveLoop();
            var c0 = new XYZ(xFt, yFt, SportifyLayoutBuilder.CurrentOriginZFt);
            var c1 = new XYZ(xFt + wFt, yFt, SportifyLayoutBuilder.CurrentOriginZFt);
            var c2 = new XYZ(xFt + wFt, yFt + hFt, SportifyLayoutBuilder.CurrentOriginZFt);
            var c3 = new XYZ(xFt, yFt + hFt, SportifyLayoutBuilder.CurrentOriginZFt);
            loop.Append(Line.CreateBound(c0, c1));
            loop.Append(Line.CreateBound(c1, c2));
            loop.Append(Line.CreateBound(c2, c3));
            loop.Append(Line.CreateBound(c3, c0));

            var solid = GeometryCreationUtilities.CreateExtrusionGeometry(new List<CurveLoop> { loop }, XYZ.BasisZ, thickFt);

            var categoryId = new ElementId(isGarden ? BuiltInCategory.OST_Planting : BuiltInCategory.OST_GenericModel);

            var ds = DirectShape.CreateElement(doc, categoryId);
            ds.SetShape(new GeometryObject[] { solid });

            string label = BuildLabel(p.Label ?? p.Category ?? "item", bb.WidthM, bb.HeightM);
            ds.Name = label;

            SportifyLayoutBuilder.SetWorkset(ds, worksetId);
            createdIds.Add(ds.Id);

            var textOrigin = new XYZ(xFt + wFt / 2.0, yFt + hFt / 2.0, SportifyLayoutBuilder.CurrentOriginZFt + thickFt);
            CreateLabelText(doc, label, textOrigin, textTypeId, worksetId, createdIds);

            return ds;
        }

        private static string BuildLabel(string name, double widthM, double heightM)
        {
            var safe = name.Trim().Replace(' ', '_');
            return $"{safe}_{widthM:0.#}x{heightM:0.#}";
        }

        private static void CreateLabelText(Document doc, string text, XYZ origin, ElementId textTypeId, WorksetId worksetId, List<ElementId> createdIds)
        {
            var textNote = TextNote.Create(doc, doc.ActiveView.Id, origin, text, textTypeId);
            SportifyLayoutBuilder.SetWorkset(textNote, worksetId);
            createdIds.Add(textNote.Id);
        }
    }
}
