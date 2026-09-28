using Autodesk.Revit.DB;

namespace SportfyRevit
{
    /// <summary>
    /// The green roof tray a zone is built from, and the placeholder that comes out of it.
    ///
    /// ── What the family is ──
    /// Sportify_GreenRoofModule is the design team's parametric tray: a pedestal,
    /// a rim, a drainage outlet, and a stack of layers inside it. It is the same
    /// family as the planters, value for value — a planter whose rim happens to
    /// be 170 mm rather than 450 — which is why it is configured through
    /// SportifyPlanterFamilyBuilder.ApplyParameters rather than through anything
    /// of its own.
    ///
    /// ── The placeholder, and why it has to go ──
    /// Between Fleece Top and Rim Level the family carries a generic-model
    /// extrusion: a solid block standing in for a build-up nobody had chosen when
    /// the family was authored.
    ///
    /// Once a build-up IS chosen, that block is wrong. It says "something 115 mm
    /// thick" where the answer is a named system with named layers, real weight
    /// and real cost. Worse, it would sit in the same space as the floor we build
    /// from that system, so a section would show both and neither would be
    /// trustworthy.
    ///
    /// So the block is deleted and the floor takes its place. The tray, rim,
    /// pedestal and outlet stay — those are the product the design team modelled.
    /// Only the stand-in goes.
    ///
    /// ── Why this edits the family rather than hiding the block ──
    /// Geometry inside a loaded family cannot be switched off per instance from
    /// the project; that would need a visibility parameter the family does not
    /// have. So the family document is opened, the extrusion deleted, and the
    /// family reloaded. The .rfa on disk is never written: this changes the copy
    /// inside the project only, which is why the design team's file stays theirs.
    ///
    /// It is done once per family per document — after that every instance is
    /// already clean, and re-editing per zone would be slow and pointless.
    /// </summary>
    internal static class SportifyGreenRoofModuleBuilder
    {
        /// <summary>
        /// Families already stripped in this document, so a roof with forty zones
        /// edits the family once rather than forty times.
        /// </summary>
        private static readonly HashSet<string> StrippedInDoc = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Called when a fresh import starts, so a second import re-checks rather than trusting a stale note.</summary>
        public static void BeginImport() => StrippedInDoc.Clear();

        /// <summary>Millimetres of slack when matching the block's top and bottom to the family's own parameters.</summary>
        private const double ToleranceMm = 1.0;

        /// <summary>
        /// Deletes the placeholder block from the family, if it is still there.
        ///
        /// Returns what happened in <paramref name="note"/> so the import report
        /// can say it plainly — a silent no-op here would leave a designer looking
        /// at a section with two solids in it and no idea why.
        /// </summary>
        public static bool StripPlaceholder(Document doc, FamilySymbol symbol, out string note)
        {
            note = "";
            var family = symbol.Family;
            var key = family.Name;

            if (StrippedInDoc.Contains(key)) { note = "already stripped in this import"; return true; }

            Document? famDoc = null;
            try
            {
                famDoc = doc.EditFamily(family);
                if (famDoc == null) { note = "the family could not be opened for editing"; return false; }

                // The block's extent is not a fixed number: it is whatever Fleece
                // Top and Rim Level currently work out to, and both move when the
                // build-up changes. So they are read from the family rather than
                // written down here, where they would go stale the first time
                // someone chose a deeper system.
                if (!TryReadMm(famDoc, "Fleece Top", out double bottomMm) ||
                    !TryReadMm(famDoc, "Rim Level", out double topMm))
                {
                    note = "the family has no Fleece Top / Rim Level to locate the block by";
                    return false;
                }

                var target = FindPlaceholder(famDoc, bottomMm, topMm);
                if (target == null)
                {
                    // Not a failure. A family the team has already cleaned up, or
                    // one stripped in an earlier session, simply has no block.
                    StrippedInDoc.Add(key);
                    note = $"no placeholder between {bottomMm:0} and {topMm:0} mm — nothing to remove";
                    return true;
                }

                using (var t = new Transaction(famDoc, "Remove build-up placeholder"))
                {
                    t.Start();
                    famDoc.Delete(target.Id);
                    t.Commit();
                }

                // Back into the project. The .rfa on disk is deliberately not
                // written: the design team's file stays theirs.
                famDoc.LoadFamily(doc, new OverwriteFamilyLoadOptions());
                StrippedInDoc.Add(key);
                note = $"placeholder removed ({bottomMm:0}–{topMm:0} mm), floor takes its place";
                return true;
            }
            catch (Exception ex)
            {
                note = $"the placeholder could not be removed ({ex.Message})";
                return false;
            }
            finally
            {
                // Closed without saving, so nothing reaches the library file.
                try { famDoc?.Close(false); } catch (Exception) { /* already gone */ }
            }
        }

        /// <summary>
        /// The solid whose bottom and top are the fleece and the rim.
        ///
        /// Matched on extent rather than on name, because a name is something a
        /// person types and will differ between the families the team sends;
        /// the extent is the thing that actually defines what the block IS.
        /// </summary>
        private static Element? FindPlaceholder(Document famDoc, double bottomMm, double topMm)
        {
            double bottomFt = UnitUtils.ConvertToInternalUnits(bottomMm, UnitTypeId.Millimeters);
            double topFt = UnitUtils.ConvertToInternalUnits(topMm, UnitTypeId.Millimeters);
            double tolFt = UnitUtils.ConvertToInternalUnits(ToleranceMm, UnitTypeId.Millimeters);

            foreach (var e in new FilteredElementCollector(famDoc).OfClass(typeof(Extrusion)))
            {
                if (e is not Extrusion x) continue;
                // A void would be cutting the tray, not standing in for the build-up.
                if (!x.IsSolid) continue;
                if (Math.Abs(x.StartOffset - bottomFt) <= tolFt && Math.Abs(x.EndOffset - topFt) <= tolFt)
                    return x;
            }
            return null;
        }

        /// <summary>A family parameter's current value in millimetres.</summary>
        private static bool TryReadMm(Document famDoc, string name, out double mm)
        {
            mm = 0;
            var mgr = famDoc.FamilyManager;
            var p = mgr?.get_Parameter(name);
            if (p == null || mgr?.CurrentType == null) return false;

            var v = mgr.CurrentType.AsDouble(p);
            if (v == null) return false;

            mm = UnitUtils.ConvertFromInternalUnits(v.Value, UnitTypeId.Millimeters);
            return true;
        }

        /// <summary>
        /// Answers Revit's "this family is already loaded" prompt in code, so the
        /// reload never stops to ask.
        ///
        /// Parameter values are deliberately NOT overwritten here, unlike the
        /// library loaders: this is the project's own family coming back with one
        /// solid removed, so the types already in the project are the ones to keep.
        /// </summary>
        private sealed class OverwriteFamilyLoadOptions : IFamilyLoadOptions
        {
            public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues)
            {
                overwriteParameterValues = false;
                return true;
            }

            public bool OnSharedFamilyFound(Family sharedFamily, bool familyInUse,
                out FamilySource source, out bool overwriteParameterValues)
            {
                source = FamilySource.Family;
                overwriteParameterValues = false;
                return true;
            }
        }
    }
}
