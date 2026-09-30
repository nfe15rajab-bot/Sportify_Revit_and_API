using Autodesk.Revit.DB;

namespace SportfyRevit
{
    /// <summary>
    /// "Sportify - Tags" (user, 2026-09-29: "add a view where the sportify_tag is auto assigned"): a plan of the roof, under the same "Diagrams" view template
    /// as the diagrams, with every Sportify piece in it tagged with the design team's own tag family, Tag_Sportify (Library\Families, loaded when the project
    /// does not have it). It is drawn again with the diagrams (SportifyDiagramViews), so a new import is tagged without anyone placing a tag.
    ///
    /// Tag_Sportify is a Generic Model tag (its category, read from the family): it tags the pieces built as Generic Models (courts, rigs, the library's
    /// pieces), not the plants (Planting) or the build-up floors. Its type: the one the project already uses for a Tag_Sportify tag (a person's choice is
    /// kept), else the family's first.
    /// </summary>
    internal static class SportifyTagView
    {
        internal const string Title = "Sportify - Tags";
        internal const string FamilyName = "Tag_Sportify";

        /// <summary>Draws (or re-draws) the tag view; inside the caller's transaction. Null when the tag family is neither in the project nor beside the add-in.</summary>
        internal static ViewPlan? Draw(Document doc, View? template)
        {
            var symbol = TagSymbol(doc);
            if (symbol == null) { SportifyLog.Warn("tags", FamilyName + ".rfa is neither in the project nor in the add-in's Library\\Families: no tag view"); return null; }
            if (!symbol.IsActive) symbol.Activate();

            var view = GenerateFunctionalDiagramsCommand.CreateOrReuseCirculationView(doc, GenerateFunctionalDiagramsCommand.RoofScopedName(Title), configure: false);
            var found = SportifyElementScan.Find(doc, (RoofBoundaryServer.ActiveRoofId ?? 0).ToString());
            if (!found.IsEmpty) GenerateFunctionalDiagramsCommand.FixViewRangeForRoof(view, found);
            if (template != null) { try { if (view.ViewTemplateId != template.Id) view.ViewTemplateId = template.Id; } catch (Exception) { /* left without */ } }

            // Only what is missing is tagged, and only the design's own pieces: a Kinetics unit is hundreds of blades, rails and rods, and Import Iterations
            // builds every piece once per iteration. Found 2026-09-30: the view was cleared and every Sportify generic model tagged again after EVERY
            // analysis (they all redraw the diagrams), one regeneration of the view per tag, ~0.85 s each over ~2,500 elements: Revit hung on "Structural
            // (bay by bay)". A tag on something no longer tagged here (a Kinetics part, an iteration's copy) goes; a deleted piece takes its tag with it.
            var iterations = IterationLedger.ReadUniqueIds(doc);
            bool Taggable(Element el) =>
                el is not FamilyInstance { SuperComponent: not null }                                   // a nested part: its piece is tagged
                && el.DesignOption == null && !iterations.Contains(el.UniqueId)
                && RoofIdentity.IsSportifys(doc, el)
                && el.LookupParameter("Sportify_Category")?.AsString() != SportifyKineticFamilyBuilder.KineticsCategoryValue;
            var tagged = new HashSet<ElementId>();
            var drop = new List<ElementId>();
            foreach (var t in new FilteredElementCollector(doc, view.Id).OfClass(typeof(IndependentTag)).Cast<IndependentTag>())
            {
                if (doc.GetElement(t.GetTypeId()) is not FamilySymbol s || s.FamilyName != FamilyName) continue;
                var hosts = t.GetTaggedLocalElementIds();
                if (hosts.Any(id => doc.GetElement(id) is Element e && Taggable(e))) tagged.UnionWith(hosts);
                else drop.Add(t.Id);
            }
            if (drop.Count > 0) doc.Delete(drop);

            var workset = SportifyWorksetSet.Ensure(doc, new[] { SportifyWorksetSet.AnnotationsAndTags })[SportifyWorksetSet.AnnotationsAndTags];
            int added = 0, failed = 0;
            var todo = new FilteredElementCollector(doc, view.Id).OfCategory(BuiltInCategory.OST_GenericModel).WhereElementIsNotElementType()
                .Where(el => !tagged.Contains(el.Id) && Taggable(el)).ToList();
            foreach (var el in todo)
            {
                var bb = el.get_BoundingBox(view);
                if (bb == null) continue;
                try
                {
                    var tag = IndependentTag.Create(doc, symbol.Id, view.Id, new Reference(el), false, TagOrientation.Horizontal, (bb.Min + bb.Max) / 2);
                    SportifyLayoutBuilder.SetWorkset(tag, workset);
                    added++;
                }
                catch (Exception) { failed++; }
            }
            SportifyLog.Info("tags", $"\"{view.Name}\": {tagged.Count} piece(s) already tagged, {added} tagged now with {FamilyName} : {symbol.Name}, {drop.Count} old tag(s) removed" + (failed > 0 ? $", {failed} could not be" : ""));
            return view;
        }

        /// <summary>The tag type: the project's Tag_Sportify (loaded from the add-in's library when it has none), in the type already in use, else its first.</summary>
        static FamilySymbol? TagSymbol(Document doc)
        {
            var family = new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>().FirstOrDefault(f => f.Name == FamilyName);
            if (family == null && SportifyPlanterFamilyBuilder.LibraryPath(FamilyName) is string path)
            {
                try { if (!doc.LoadFamily(path, new KeepLoaded(), out family)) family = null; }
                catch (Exception ex) { SportifyLog.Warn("tags", FamilyName + " could not be loaded: " + ex.Message); family = null; }
                if (family != null) SportifyLog.Info("tags", "loaded " + FamilyName + " from " + path);
            }
            if (family == null) return null;
            var inUse = new FilteredElementCollector(doc).OfClass(typeof(IndependentTag)).Cast<IndependentTag>()
                .Select(t => doc.GetElement(t.GetTypeId()) as FamilySymbol).FirstOrDefault(s => s?.FamilyName == FamilyName);
            return inUse ?? family.GetFamilySymbolIds().Select(doc.GetElement).OfType<FamilySymbol>().OrderBy(s => s.Id.Value).FirstOrDefault();
        }

        /// <summary>A project that has the family keeps its own version of it.</summary>
        sealed class KeepLoaded : IFamilyLoadOptions
        {
            public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues) { overwriteParameterValues = false; return false; }
            public bool OnSharedFamilyFound(Family sharedFamily, bool familyInUse, out FamilySource source, out bool overwriteParameterValues)
            { source = FamilySource.Project; overwriteParameterValues = false; return false; }
        }
    }
}
