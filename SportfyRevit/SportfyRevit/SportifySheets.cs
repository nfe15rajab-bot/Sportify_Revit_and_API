using System.Text.RegularExpressions;
using Autodesk.Revit.DB;

namespace SportfyRevit
{
    /// <summary>
    /// The Sportify plan set (user, 2026-09-29): every Sportify sheet on A0, in the order roof existing, roof new, axonometric, diagram 1 zoning, diagram 2
    /// circulation, diagram 3 accessibility, then the lists (SportifyTemplateSpec, German or English). Apply Sportify Template arranges it (a new project, or one
    /// made with the earlier template: its sheets are re-numbered and re-named, not made twice), and so does every drawing of the diagrams (SportifyDiagramViews),
    /// so a diagram goes on its sheet as soon as it exists.
    ///
    /// What a person put on a sheet is kept. A sheet of the set is found by what it shows first (the roof new sheet showing "LAGEPLAN NEU", say), then by its
    /// name (this template's or the earlier one's), then, only when it is empty, by its number. A view is added only to a sheet that shows none of its kind; a
    /// view already on another sheet is never taken off it. A Sportify sheet that is not in the set keeps its content and moves out of the set's numbers
    /// (S2-07 and on). Runs inside the caller's transaction.
    /// </summary>
    internal static class SportifySheets
    {
        const double MmToFt = 1.0 / 304.8;
        const double TitleStripMm = 200;                                  // the Plankopf's column on the right of a German sheet: the views are centred left of it
        static readonly Regex SportifyNumber = new(@"^S[1-4]-\d\d$");

        internal static void Arrange(Document doc, SportifyTemplateSet set, IReadOnlyDictionary<string, View>? views, IReadOnlyDictionary<string, ViewSchedule>? schedules,
            List<string> made, List<string> notes)
        {
            var allViews = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Where(v => !v.IsTemplate).ToList();
            var viewports = new FilteredElementCollector(doc).OfClass(typeof(Viewport)).Cast<Viewport>().ToList();
            var onSheet = viewports.GroupBy(vp => vp.SheetId).ToDictionary(g => g.Key, g => g.Select(vp => doc.GetElement(vp.ViewId) as View).OfType<View>().ToList());
            var listsOn = new FilteredElementCollector(doc).OfClass(typeof(ScheduleSheetInstance)).Cast<ScheduleSheetInstance>().Where(x => !x.IsTitleblockRevisionSchedule)
                .GroupBy(x => x.OwnerViewId).ToDictionary(g => g.Key, g => g.Select(x => x.ScheduleId).ToList());
            var sheets = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>().Where(s => !s.IsPlaceholder).ToList();
            var candidates = sheets.Where(s => SportifyNumber.IsMatch(s.SheetNumber)).ToList();

            List<View> Shows(ViewSheet s) => onSheet.TryGetValue(s.Id, out var l) ? l : new List<View>();
            List<ElementId> Lists(ViewSheet s) => listsOn.TryGetValue(s.Id, out var l) ? l : new List<ElementId>();
            View? SpecView(string key)
            {
                if (views != null && views.TryGetValue(key, out var v)) return v;
                var spec = set.Views.FirstOrDefault(x => x.Key == key);
                return spec == null ? null : allViews.FirstOrDefault(x => x.Name == spec.Name);
            }
            ElementId? ScheduleId(string key)
            {
                if (schedules != null && schedules.TryGetValue(key, out var s)) return s.Id;
                var spec = set.Schedules.FirstOrDefault(x => x.Key == key);
                return spec == null ? null : allViews.OfType<ViewSchedule>().FirstOrDefault(x => x.Name == spec.Name)?.Id;
            }
            bool OfKind(View v, string key)
            {
                if (SportifyTemplateSpec.DiagramTitle(key) is string title) return IsDiagram(v.Name, title);
                return SpecView(key) is View mine && mine.Id == v.Id;
            }
            bool ShowsKindOf(ViewSheet s, SheetSpec t) =>
                t.Views.Any(k => Shows(s).Any(v => OfKind(v, k))) || t.Schedules.Any(k => ScheduleId(k) is ElementId id && Lists(s).Contains(id));
            bool ShowsAnotherTargets(ViewSheet s, SheetSpec t) => set.Sheets.Where(o => o != t).Any(o => ShowsKindOf(s, o));
            bool Empty(ViewSheet s) => Shows(s).Count == 0 && Lists(s).Count == 0;

            // ---- which sheet is which: by what it shows, by its name, by its number when it is empty
            var matched = new Dictionary<SheetSpec, ViewSheet>();
            bool Free(ViewSheet s) => !matched.ContainsValue(s);
            foreach (var t in set.Sheets)
            {
                var hit = candidates.Where(s => Free(s) && ShowsKindOf(s, t)).OrderBy(s => s.SheetNumber == t.Number ? 0 : 1).ThenBy(s => s.SheetNumber, StringComparer.Ordinal).FirstOrDefault();
                if (hit != null) matched[t] = hit;
            }
            foreach (var t in set.Sheets.Where(t => !matched.ContainsKey(t)))
            {
                var names = new HashSet<string>(NamesOf(t, set));
                var hit = candidates.FirstOrDefault(s => Free(s) && names.Contains(s.Name) && !ShowsAnotherTargets(s, t));
                if (hit != null) matched[t] = hit;
            }
            foreach (var t in set.Sheets.Where(t => !matched.ContainsKey(t)))
            {
                var hit = candidates.FirstOrDefault(s => Free(s) && s.SheetNumber == t.Number && Empty(s));
                if (hit != null) matched[t] = hit;
            }

            // ---- the roof as it is: the roof new sheet's own plan, duplicated, in the Existing phase, Sportify's pieces hidden
            var existingSpec = set.Views.FirstOrDefault(v => v.Kind == "existing");
            var planSheet = set.Sheets.FirstOrDefault(t => t.Views.Contains("plan"));
            View? roofNew = planSheet != null && matched.TryGetValue(planSheet, out var ps) ? Shows(ps).OfType<ViewPlan>().FirstOrDefault() : null;
            roofNew ??= SpecView("plan");
            var existing = existingSpec != null ? EnsureExistingView(doc, existingSpec, SpecView("existing") ?? ExistingCounterpart(allViews, roofNew), roofNew as ViewPlan, made, notes) : null;
            View? ToPlace(string key)
            {
                if (key == existingSpec?.Key) return existing;
                if (SportifyTemplateSpec.DiagramTitle(key) is string title) return PreferredDiagram(allViews, viewports, title);
                return SpecView(key);
            }

            // ---- numbers: the set's own, in its order; a Sportify sheet outside the set that holds one of them moves to the next free number
            var reserved = new HashSet<string>(set.Sheets.Select(t => t.Number));
            var final = matched.ToDictionary(kv => kv.Value, kv => kv.Key.Number);
            var taken = new HashSet<string>(sheets.Select(s => s.SheetNumber).Where(n => !reserved.Contains(n)));
            foreach (var extra in candidates.Where(s => Free(s) && reserved.Contains(s.SheetNumber)).OrderBy(s => s.SheetNumber, StringComparer.Ordinal))
            {
                var next = NextFree(taken, reserved);
                final[extra] = next; taken.Add(next);
                made.Add($"Sheet {extra.SheetNumber} {extra.Name} moved to {next} (not one of the plan set; kept as it is)");
            }
            var before = final.Keys.ToDictionary(s => s, s => s.SheetNumber);
            foreach (var (sheet, number) in final)
                if (sheet.SheetNumber != number) sheet.SheetNumber = "tmp-" + sheet.Id.Value;          // every number freed first, so no two ever collide ("~" is not allowed in a sheet number: found in the dry run)
            foreach (var (sheet, number) in final)
                if (before[sheet] != number)
                {
                    sheet.SheetNumber = number;
                    if (matched.ContainsValue(sheet)) made.Add($"Sheet {before[sheet]} \"{sheet.Name}\" is now {number}");
                }

            // ---- each sheet of the set: made when it has something to show, named, A0, its view or lists on it
            foreach (var t in set.Sheets)
            {
                try
                {
                    matched.TryGetValue(t, out var sheet);
                    var missingViews = t.Views.Where(k => sheet == null || !Shows(sheet).Any(v => OfKind(v, k))).Select(ToPlace).OfType<View>()
                        .Where(v => !viewports.Any(vp => vp.ViewId == v.Id)).ToList();
                    var missingLists = t.Schedules.Select(ScheduleId).OfType<ElementId>().Where(id => sheet == null || !Lists(sheet).Contains(id)).ToList();
                    if (sheet == null)
                    {
                        if (missingViews.Count == 0 && (t.Schedules.Count == 0 || missingLists.Count < t.Schedules.Count)) continue;   // a plan sheet waits for its view
                        sheet = ViewSheet.Create(doc, ElementId.InvalidElementId);
                        sheet.SheetNumber = t.Number;
                        sheet.Name = t.Name;
                        made.Add($"Sheet {t.Number} {t.Name} (A0)");
                    }
                    else if (sheet.Name != t.Name)
                    {
                        made.Add($"Sheet {t.Number}: \"{sheet.Name}\" renamed \"{t.Name}\"");
                        sheet.Name = t.Name;
                    }
                    var resized = EnsureA0(doc, sheet, set, t, notes);
                    if (resized) made.Add($"Sheet {t.Number} {t.Name} on A0");
                    var frame = Frame(doc, sheet);

                    int i = 0;
                    // a diagram sheet showing another roof's (or an older) drawing of its diagram gets the active roof's, in the same place
                    foreach (var key in t.Views.Where(SportifyTemplateSpec.IsDiagramKey))
                    {
                        var title = SportifyTemplateSpec.DiagramTitle(key)!;
                        var current = ToPlace(key);
                        if (current == null || current.Name != GenerateFunctionalDiagramsCommand.RoofScopedName(title) || viewports.Any(vp => vp.ViewId == current.Id)) continue;
                        var old = viewports.FirstOrDefault(vp => vp.SheetId == sheet.Id && doc.GetElement(vp.ViewId) is View v && IsDiagram(v.Name, title));
                        if (old == null) continue;
                        var at = old.GetBoxCenter();
                        var oldName = (doc.GetElement(old.ViewId) as View)?.Name;
                        viewports.Remove(old);
                        doc.Delete(old.Id);
                        if (!Viewport.CanAddViewToSheet(doc, sheet.Id, current.Id)) continue;
                        viewports.Add(Viewport.Create(doc, sheet.Id, current.Id, at));
                        made.Add($"Sheet {t.Number}: \"{oldName}\" replaced by \"{current.Name}\"");
                    }
                    foreach (var v in missingViews)
                    {
                        if (!Viewport.CanAddViewToSheet(doc, sheet.Id, v.Id)) continue;
                        var vp = Viewport.Create(doc, sheet.Id, v.Id, frame.Centre);
                        viewports.Add(vp);
                        made.Add($"Sheet {t.Number}: \"{v.Name}\" placed");
                        i++;
                    }
                    int row = Lists(sheet).Count;
                    foreach (var id in missingLists)
                    {
                        ScheduleSheetInstance.Create(doc, sheet.Id, id, new XYZ(frame.Min.X + 20 * MmToFt, frame.Max.Y - (30 + row * 110) * MmToFt, 0));
                        row++;
                    }
                    if (resized || i > 0) Centre(doc, sheet, frame);
                    SportifyTemplateBuilder.SetPhase(set, sheet, t.PhaseKey);
                    SportifyTemplateBuilder.SetBrowserGrouping(sheet);
                    var size = sheet.LookupParameter("Plangröße");
                    if (size != null && !size.IsReadOnly && size.StorageType == StorageType.String && size.AsString() != t.Size) size.Set(t.Size);
                }
                catch (Exception ex)
                {
                    notes.Add($"Sheet {t.Number} {t.Name} could not be arranged: {ex.Message.Split('\n')[0]}");
                    SportifyLog.Warn("sheets", "sheet " + t.Number + ": " + ex);
                }
            }
            // "must be all A0" (user, 2026-09-29): the Sportify sheets outside the set too (a person's extra sheet, the analysis report), content untouched
            foreach (var extra in sheets.Where(x => !matched.ContainsValue(x) && (SportifyNumber.IsMatch(x.SheetNumber) || x.SheetNumber.StartsWith("SPORT", StringComparison.Ordinal))))
            {
                try { if (EnsureA0(doc, extra, set, set.Sheets[0], notes)) made.Add($"Sheet {extra.SheetNumber} {extra.Name} on A0"); }
                catch (Exception ex) { notes.Add($"Sheet {extra.SheetNumber} could not be put on A0: {ex.Message.Split('\n')[0]}"); }
            }
            SportifyLog.Info("sheets", $"plan set arranged ({set.Language}): " + string.Join("; ", made.Where(m => m.StartsWith("Sheet")).DefaultIfEmpty("nothing to change")));
        }

        /// <summary>A view drawn by SportifyDiagramViews under that title: the title alone, or for one roof (" — Roof N").</summary>
        internal static bool IsDiagram(string viewName, string title) => viewName == title || viewName.StartsWith(title + " — Roof ", StringComparison.Ordinal);

        /// <summary>The diagram to put on a sheet: the active roof's, else one on no sheet yet, else the newest.</summary>
        static View? PreferredDiagram(List<View> all, List<Viewport> viewports, string title)
        {
            var mine = all.Where(v => v is ViewPlan && IsDiagram(v.Name, title)).OrderByDescending(v => v.Id.Value).ToList();
            var active = GenerateFunctionalDiagramsCommand.RoofScopedName(title);
            return mine.FirstOrDefault(v => v.Name == active) ?? mine.FirstOrDefault(v => !viewports.Any(vp => vp.ViewId == v.Id)) ?? mine.FirstOrDefault();
        }

        static IEnumerable<string> NamesOf(SheetSpec t, SportifyTemplateSet set)
        {
            yield return t.Name;
            foreach (var other in new[] { SportifyTemplateSpec.De, SportifyTemplateSpec.En })
                if (other.Sheets.FirstOrDefault(o => o.Number == t.Number) is SheetSpec same) yield return same.Name;
            foreach (var key in t.Views)
                if (set.FormerSheetNames.TryGetValue(key, out var former)) foreach (var n in former) yield return n;
        }

        static string NextFree(HashSet<string> taken, HashSet<string> reserved)
        {
            for (int n = 7; n < 100; n++)
            {
                var number = "S2-" + n.ToString("00");
                if (!taken.Contains(number) && !reserved.Contains(number)) return number;
            }
            return "S2-X" + Guid.NewGuid().ToString("N").Substring(0, 4);
        }

        /// <summary>A person's own "existing" plan beside the roof new one ("LAGEPLAN NEU/SITE PLAN Existing" next to "... NEW"): a plan named Existing or Bestand, the one
        /// whose name shares most with the roof new plan's. Null when there is none.</summary>
        internal static View? ExistingCounterpart(List<View> all, View? roofNew)
        {
            if (roofNew == null) return null;
            return all.OfType<ViewPlan>().Where(v => v.Id != roofNew.Id && IsExistingName(v.Name) && !SportifyTemplateSpec.Diagrams.Any(d => IsDiagram(v.Name, d.Title)))
                .OrderByDescending(v => SharedPrefix(v.Name, roofNew.Name)).ThenBy(v => v.Id.Value).FirstOrDefault(v => SharedPrefix(v.Name, roofNew.Name) >= 4);
        }

        internal static bool IsExistingName(string name) =>
            name.IndexOf("existing", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("bestand", StringComparison.OrdinalIgnoreCase) >= 0;

        internal static int SharedPrefix(string a, string b)
        {
            int n = 0;
            while (n < a.Length && n < b.Length && char.ToLowerInvariant(a[n]) == char.ToLowerInvariant(b[n])) n++;
            return n;
        }

        /// <summary>The roof as it stands: a view of that name, else the roof new sheet's plan duplicated. Its phase is the project's first (Existing), and what Sportify
        /// put there (an import from before the Sportify phases, say) is hidden, so it shows the roof before the design.</summary>
        static View? EnsureExistingView(Document doc, ViewSpec spec, View? found, ViewPlan? roofNew, List<string> made, List<string> notes)
        {
            var view = found;
            try
            {
                if (view == null)
                {
                    if (roofNew == null || !roofNew.CanViewBeDuplicated(ViewDuplicateOption.Duplicate)) return null;
                    view = (View)doc.GetElement(roofNew.Duplicate(ViewDuplicateOption.Duplicate));
                    view.Name = spec.Name;
                    made.Add($"View: {spec.Name} (the roof new sheet's \"{roofNew.Name}\" in the Existing phase)");
                }
                var existingPhase = SportifyPhases.Read(doc).Existing;
                var phase = view.get_Parameter(BuiltInParameter.VIEW_PHASE);
                if (existingPhase != null && phase != null && !phase.IsReadOnly && phase.AsElementId() != existingPhase.Id) phase.Set(existingPhase.Id);
                SportifyTemplateBuilder.SetBrowserGrouping(view);
                doc.Regenerate();
                var sportify = new FilteredElementCollector(doc, view.Id).WhereElementIsNotElementType()
                    .Where(e => RoofIdentity.IsSportifys(doc, e) && e.CanBeHidden(view)).Select(e => e.Id).ToList();
                if (sportify.Count > 0) view.HideElements(sportify);
            }
            catch (Exception ex)
            {
                notes.Add($"The view \"{spec.Name}\" could not be made: {ex.Message.Split('\n')[0]}");
                SportifyLog.Warn("sheets", "existing roof view: " + ex);
            }
            return view;
        }

        static bool IsA0(string typeName) => Regex.IsMatch(typeName, @"(^|[^A-Za-z0-9])A0([^0-9]|$)");

        /// <summary>The sheet's title block in its family's A0 type (else the Sportify one, else any A0); a sheet without one gets one. True when it changed.</summary>
        static bool EnsureA0(Document doc, ViewSheet sheet, SportifyTemplateSet set, SheetSpec spec, List<string> notes)
        {
            var symbols = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).OfCategory(BuiltInCategory.OST_TitleBlocks).Cast<FamilySymbol>().ToList();
            var tb = new FilteredElementCollector(doc, sheet.Id).OfCategory(BuiltInCategory.OST_TitleBlocks).WhereElementIsNotElementType().OfType<FamilyInstance>().FirstOrDefault();
            // the project's own Sportify title block first (the team's "sportify_Plankopf Ausführung", user 2026-09-30), then the sheet's own family, then the template's
            var own = "sportify_" + set.TitleBlockFamily(spec.PhaseKey);
            var want = symbols.FirstOrDefault(s => string.Equals(s.FamilyName, own, StringComparison.OrdinalIgnoreCase) && IsA0(s.Name))
                       ?? (tb != null ? symbols.FirstOrDefault(s => s.FamilyName == tb.Symbol.FamilyName && IsA0(s.Name)) : null)
                       ?? symbols.FirstOrDefault(s => s.FamilyName + " : " + s.Name == set.TitleBlockType(spec))
                       ?? symbols.FirstOrDefault(s => IsA0(s.Name));
            if (want == null) { notes.Add($"Sheet {spec.Number}: the project has no A0 title block"); return false; }
            if (tb != null && tb.Symbol.Id == want.Id) return false;
            if (!want.IsActive) want.Activate();
            var before = tb?.get_BoundingBox(sheet);
            if (tb == null) doc.Create.NewFamilyInstance(XYZ.Zero, want, sheet);
            else tb.ChangeTypeId(want.Id);
            doc.Regenerate();
            if (before != null)
            {
                // the lists stay at the top left of the larger sheet
                var after = Frame(doc, sheet);
                var shift = new XYZ(after.Min.X - before.Min.X, after.Max.Y - before.Max.Y, 0);
                foreach (var list in new FilteredElementCollector(doc, sheet.Id).OfClass(typeof(ScheduleSheetInstance)).Cast<ScheduleSheetInstance>().Where(x => !x.IsTitleblockRevisionSchedule))
                    list.Point = list.Point + shift;
            }
            return true;
        }

        internal readonly record struct SheetFrame(XYZ Min, XYZ Max)
        {
            /// <summary>The middle of the drawing area: left of the Plankopf's column.</summary>
            public XYZ Centre => new((Min.X + Max.X - TitleStripMm * MmToFt) / 2, (Min.Y + Max.Y) / 2, 0);
        }

        /// <summary>The sheet's title block outline (A0 landscape from the origin when it has none).</summary>
        internal static SheetFrame Frame(Document doc, ViewSheet sheet)
        {
            var tb = new FilteredElementCollector(doc, sheet.Id).OfCategory(BuiltInCategory.OST_TitleBlocks).WhereElementIsNotElementType().FirstOrDefault();
            var bb = tb?.get_BoundingBox(sheet);
            return bb != null ? new SheetFrame(bb.Min, bb.Max) : new SheetFrame(XYZ.Zero, new XYZ(1189 * MmToFt, 841 * MmToFt, 0));
        }

        /// <summary>The sheet's views side by side across the drawing area, each in the middle of its share.</summary>
        static void Centre(Document doc, ViewSheet sheet, SheetFrame frame)
        {
            var vps = new FilteredElementCollector(doc, sheet.Id).OfClass(typeof(Viewport)).Cast<Viewport>().OrderBy(v => v.Id.Value).ToList();
            if (vps.Count == 0) return;
            double left = frame.Min.X, width = frame.Max.X - frame.Min.X - TitleStripMm * MmToFt, y = (frame.Min.Y + frame.Max.Y) / 2;
            for (int i = 0; i < vps.Count; i++)
            {
                try { vps[i].SetBoxCenter(new XYZ(left + width * (i + 0.5) / vps.Count, y, 0)); }
                catch (Exception ex) { SportifyLog.Warn("sheets", $"a view could not be centred on {sheet.SheetNumber}: " + ex.Message); }
            }
        }
    }
}
