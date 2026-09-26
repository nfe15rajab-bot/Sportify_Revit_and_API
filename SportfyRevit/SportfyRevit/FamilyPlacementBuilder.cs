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
        /// Single entry point SportifyLayoutBuilder calls per placement. The family was settled BEFORE the import transaction opened
        /// (FamilyPreparation: a specified-sport builder, a plant, a referenced family, a family already in the project that is exactly this
        /// piece's, or a generated one); this places it, or, when there is none, a placeholder box, and says in the report which family the
        /// piece got and how, or why it became a box. There is no keyword matching any more: sharing one word with some loaded family used to
        /// be enough to place a piece as that family.
        /// </summary>
        public static void PlaceComponent(
            Document doc, PlacementDto p, BoundingBoxDto bb, bool isGarden,
            double originXFt, double originYFt, WorksetId worksetId, ElementId? textTypeId, List<ElementId> createdIds,
            double? fireSafetyDistanceM, FamilyResolution resolution)
        {
            var label = p.Label ?? p.Id ?? "(piece)";
            FamilySymbol? symbol = resolution.SymbolId != null ? doc.GetElement(resolution.SymbolId) as FamilySymbol : null;
            if (symbol != null)
                ImportDiagnostics.Placed(label, resolution.FamilyName ?? symbol.Family?.Name ?? "(family)", resolution.TypeName ?? symbol.Name, resolution.How);
            else
                ImportDiagnostics.PlaceholderBecause(label, resolution.SymbolId != null
                    ? "the family it was given is no longer in the project"
                    : resolution.Reason ?? "no family could be resolved");

            Element placed = symbol != null
                ? PlaceFamilyInstance(doc, p, bb, symbol, originXFt, originYFt, worksetId, textTypeId, createdIds)
                : PlacePlaceholderBox(doc, p, bb, isGarden, originXFt, originYFt, worksetId, textTypeId, createdIds);

            // Stamps the same unified parameter set on whatever got placed — real family instance or placeholder box alike — so Revit's own
            // Properties panel/Schedules can already query category/quality/material data. Defensive: EnsureBound returns false (and SetValues
            // is then a no-op) if the shared-parameter file/binding fails for any reason — geometry placement above must never be broken by this.
            if (SportifySharedParameters.EnsureBound(doc))
            {
                var (typeId, variant, norm, lengthM, widthM) = PlacementDataHelpers.GetUnifiedFields(p);
                SportifySharedParameters.SetValues(placed, p.Category, typeId, variant,
                    PlacementDataHelpers.GetQualityLevel(p), norm, lengthM, widthM,
                    PlacementDataHelpers.GetReferenceMaterialName(p), PlacementDataHelpers.GetReferenceProviderName(p),
                    p.Parameters?.QualityKey);
            }

            // The richer generalities/materials(+LCA+provider)/analysis set — safe no-op wherever these Sportify_* parameters don't exist
            // (a matched family that isn't one of ours, or the placeholder box); only actually populates on a SportifyFamilyGenerator instance.
            SportifyFamilyParameters.SetInstanceValues(placed, p, fireSafetyDistanceM);
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
            double originXFt, double originYFt, WorksetId worksetId, ElementId? textTypeId, List<ElementId> createdIds)
        {
            if (!symbol.IsActive)
            {
                symbol.Activate();
                doc.Regenerate();
            }

            double centerXFt, centerYFt;
            if (p.InsertionPoint != null)
                (centerXFt, centerYFt) = SportifyLayoutBuilder.PlanToWorldFt(p.InsertionPoint.CenterXM, p.InsertionPoint.CenterYM);
            else
                (centerXFt, centerYFt) = SportifyLayoutBuilder.PlanToWorldFt(bb.TopLeftXM + bb.WidthM / 2.0, bb.TopLeftYM + bb.HeightM / 2.0);
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
            // Negated because the Y axis is flipped on the way in (see
            // SportifyLayoutBuilder.WorldYFt). Mirroring a plan reverses the
            // sense of rotation, so a clockwise turn on the web canvas is a
            // counter-clockwise turn in Revit — applying the angle unchanged
            // would leave every rotated piece turned the wrong way.
            // Plus the turn of the plan itself (RoofFrame): a roof turned against the
            // model's axes has a plan turned by that much, so every piece is turned with it.
            double rotationRad = -rotationDeg * Math.PI / 180.0 + SportifyLayoutBuilder.CurrentAngleRad;
            if (Math.Abs(rotationRad) > 1e-9)
            {
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
            double originXFt, double originYFt, WorksetId worksetId, ElementId? textTypeId, List<ElementId> createdIds)
        {
            // The canvas box's TOP edge is its LOWEST Y once flipped, so the first
            // corner is the box's bottom-left on the canvas, and the loop runs counter-clockwise
            // in Revit. Each corner goes through the plan-to-model transform, so a roof turned
            // against the model's axes gets a turned box.
            double thickFt = SportifyLayoutBuilder.FeetFromMeters(ThicknessM);
            double z = SportifyLayoutBuilder.CurrentOriginZFt;
            var loop = new CurveLoop();
            var c0 = SportifyLayoutBuilder.PlanPointFt(bb.TopLeftXM, bb.TopLeftYM + bb.HeightM, z);
            var c1 = SportifyLayoutBuilder.PlanPointFt(bb.TopLeftXM + bb.WidthM, bb.TopLeftYM + bb.HeightM, z);
            var c2 = SportifyLayoutBuilder.PlanPointFt(bb.TopLeftXM + bb.WidthM, bb.TopLeftYM, z);
            var c3 = SportifyLayoutBuilder.PlanPointFt(bb.TopLeftXM, bb.TopLeftYM, z);
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

            var textOrigin = new XYZ((c0.X + c2.X) / 2.0, (c0.Y + c2.Y) / 2.0, SportifyLayoutBuilder.CurrentOriginZFt + thickFt);
            CreateLabelText(doc, label, textOrigin, textTypeId, worksetId, createdIds);

            return ds;
        }

        private static string BuildLabel(string name, double widthM, double heightM)
        {
            var safe = name.Trim().Replace(' ', '_');
            return $"{safe}_{widthM:0.#}x{heightM:0.#}";
        }

        private static void CreateLabelText(Document doc, string text, XYZ origin, ElementId? textTypeId, WorksetId worksetId, List<ElementId> createdIds)
        {
            // No text-note type, or an active view that cannot hold text notes (a 3D view): SportifyLayoutBuilder said so once in the report.
            if (textTypeId == null) return;
            try
            {
                var textNote = TextNote.Create(doc, doc.ActiveView.Id, origin, text, textTypeId);
                SportifyLayoutBuilder.SetWorkset(textNote, worksetId);
                createdIds.Add(textNote.Id);
            }
            catch (Exception ex)
            {
                // A label must never take the import with it (it used to: one label in a view that could not hold it rolled everything back).
                ImportDiagnostics.Note($"the label \"{text}\" was not created: {ex.Message}");
            }
        }
    }
}
