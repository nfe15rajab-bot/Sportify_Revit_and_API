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
        ///
        /// <paramref name="freshSymbolId"/> is the symbol to use AFTERWARDS, and
        /// callers must take it rather than keeping the one they passed in.
        /// Reloading a family replaces it and its types with new elements, so the
        /// handle that came in is left pointing at something Revit has deleted —
        /// "the referenced object is not valid". That is exactly how the tray went
        /// missing the first time this ran: the strip succeeded, the ElementId
        /// went stale, and the placement quietly resolved it to nothing.
        /// </summary>
        public static bool StripPlaceholder(Document doc, FamilySymbol symbol, out string note, out ElementId? freshSymbolId)
        {
            note = "";
            // Read across the reload, so they are strings rather than handles into
            // a family that is about to be replaced.
            var family = symbol.Family;
            var key = family.Name;
            var typeName = symbol.Name;
            freshSymbolId = symbol.Id;

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
                var reloaded = famDoc.LoadFamily(doc, new OverwriteFamilyLoadOptions());
                StrippedInDoc.Add(key);

                // The family that comes back is a NEW element. Take its type by
                // name rather than trusting the id we walked in with.
                var fresh = FindSymbol(doc, reloaded, key, typeName);
                if (fresh == null)
                {
                    note = $"placeholder removed ({bottomMm:0}–{topMm:0} mm), but the reloaded family has no type \"{typeName}\"";
                    freshSymbolId = null;
                    return false;
                }

                freshSymbolId = fresh.Id;
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
        /// The type to place for a zone of this size.
        ///
        /// ── Why this exists ──
        /// Two zones came into Revit as two trays of the SAME size, neither
        /// matching its zone. The family holds Length and Width as TYPE
        /// parameters, so every instance shares one value and the last zone
        /// written wins. Setting them per instance cannot work; there is only one
        /// number to set.
        ///
        /// So each distinct size gets its own type. That is also how a Revit user
        /// would do it by hand, and it makes the sizes visible in the project
        /// browser and schedulable, instead of hidden in instance overrides.
        ///
        /// The type is named for what makes it different — the size and the
        /// build-up it was made for — so two zones that really are the same tray
        /// share one type rather than multiplying.
        ///
        /// When Length turns out to be an INSTANCE parameter after all (a later
        /// family, or one the design team changes), this steps aside and returns
        /// the symbol untouched: setting it per instance then does the right
        /// thing on its own.
        /// </summary>
        public static FamilySymbol SymbolForSize(Document doc, FamilySymbol baseSymbol,
                                                 double lengthMm, double widthMm,
                                                 string? variantKey, out string note)
        {
            note = "";
            // An instance parameter needs no type of its own.
            if (baseSymbol.LookupParameter("Length") == null)
            {
                note = "Length is an instance parameter — one type is enough";
                return baseSymbol;
            }

            var wanted = $"{baseSymbol.Family?.Name ?? "Green Roof"} {lengthMm:0}x{widthMm:0}"
                       + (string.IsNullOrWhiteSpace(variantKey) ? "" : $" {variantKey}");

            var familyName = baseSymbol.Family?.Name;
            foreach (var e in new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)))
                if (e is FamilySymbol s
                    && string.Equals(s.Family?.Name, familyName, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(s.Name, wanted, StringComparison.Ordinal))
                {
                    note = $"reused type \"{wanted}\"";
                    return s;
                }

            try
            {
                if (baseSymbol.Duplicate(wanted) is FamilySymbol made)
                {
                    note = $"new type \"{wanted}\"";
                    return made;
                }
            }
            catch (Exception ex)
            {
                // Not fatal: the tray is still placed, at whatever size the base
                // type carries, and the report says it is the wrong size rather
                // than leaving it to be measured.
                note = $"could not make a type for {lengthMm:0}x{widthMm:0} mm ({ex.Message}) — placed at the base type's size";
            }
            return baseSymbol;
        }

        /// <summary>
        /// The reloaded family's type, by name.
        ///
        /// LoadFamily's return value is preferred, but it can come back null when
        /// Revit decides the family was merged rather than added — so the project
        /// is searched by family name as well, and only then do we give up. Going
        /// by name is the point: every handle from before the reload is stale.
        /// </summary>
        private static FamilySymbol? FindSymbol(Document doc, Family? reloaded, string familyName, string typeName)
        {
            var candidates = new List<FamilySymbol>();

            if (reloaded != null)
            {
                foreach (var id in reloaded.GetFamilySymbolIds())
                    if (doc.GetElement(id) is FamilySymbol s) candidates.Add(s);
            }

            if (candidates.Count == 0)
            {
                foreach (var e in new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)))
                    if (e is FamilySymbol s && string.Equals(s.Family?.Name, familyName, StringComparison.OrdinalIgnoreCase))
                        candidates.Add(s);
            }

            return candidates.FirstOrDefault(s => string.Equals(s.Name, typeName, StringComparison.OrdinalIgnoreCase))
                ?? candidates.FirstOrDefault();
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
