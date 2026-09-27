using Autodesk.Revit.DB;

namespace SportfyRevit
{
    /// <summary>
    /// The seven schedules of the Sportify templates (SportifyTemplateSet.Schedules), named like the German template's: 00_ for the lists of the project itself (sheets, views), "AVA -" for
    /// the lists of quantities (equipment, planting, roof build-ups), "FLÄCHEN - DIN 277" and "KOSTENGRUPPEN - DIN 276" for the norms. In a project without Sportify elements they are empty
    /// definitions, ready to fill as an import arrives. A schedule of the same name that already exists is replaced (so running twice never gives two). Call inside a transaction.
    /// </summary>
    internal static class SportifyScheduleFactory
    {
        public static Dictionary<string, ViewSchedule> EnsureAll(Document doc, SportifyTemplateSet set, Dictionary<string, View> templates, List<string> made, List<string> notes)
        {
            var result = new Dictionary<string, ViewSchedule>();
            templates.TryGetValue(set.ScheduleTemplateName, out var scheduleTemplate);
            foreach (var spec in set.Schedules)
            {
                try
                {
                    var schedule = Make(doc, spec, notes);
                    if (schedule != null)
                    {
                        result[spec.Key] = schedule;
                        made.Add("Schedule: " + spec.Name);
                        if (scheduleTemplate != null) AssignTemplate(doc, schedule, scheduleTemplate, notes);
                    }
                }
                catch (Exception ex) { notes.Add($"\"{spec.Name}\" could not be made: {ex.Message.Split('\n')[0]}"); SportifyLog.Warn("templates", "schedule " + spec.Name + ": " + ex); }
            }
            return result;
        }

        /// <summary>
        /// The schedule view template controls none of a schedule's own content: whichever of its parameters Revit lists are made non-controlled, so a schedule keeps its fields, filter and grouping.
        /// </summary>
        internal static void ControlNothing(View template)
        {
            try { template.SetNonControlledTemplateParameterIds(template.GetTemplateParameterIds()); }
            catch (Exception ex) { SportifyLog.Warn("templates", "schedule view template " + template.Name + ": its parameters could not be set to non-controlled: " + ex.Message); }
        }

        /// <summary>
        /// Assigns the Sportify schedule view template to a schedule, in a sub-transaction that is rolled back (the schedule stays without the template) if the assignment changed what the schedule
        /// lists: a schedule's fields, filter and grouping are the point of it and a template must never take them away.
        /// </summary>
        private static void AssignTemplate(Document doc, ViewSchedule schedule, View template, List<string> notes)
        {
            using var sub = new SubTransaction(doc);
            sub.Start();
            try
            {
                var fields = schedule.Definition.GetFieldCount();
                var filters = schedule.Definition.GetFilterCount();
                var groups = schedule.Definition.GetSortGroupFieldCount();
                schedule.ViewTemplateId = template.Id;
                if (schedule.Definition.GetFieldCount() != fields || schedule.Definition.GetFilterCount() != filters || schedule.Definition.GetSortGroupFieldCount() != groups)
                {
                    sub.RollBack();
                    notes.Add(schedule.Name + ": the schedule view template would have changed its fields, so it was left without.");
                    return;
                }
                sub.Commit();
            }
            catch (Exception ex)
            {
                if (sub.GetStatus() == TransactionStatus.Started) sub.RollBack();
                notes.Add(schedule.Name + " could not be assigned to the schedule view template: " + ex.Message.Split((char)10)[0]);
            }
        }

        private static ViewSchedule? Make(Document doc, ScheduleSpec spec, List<string> notes)
        {
            if (!BimScheduleBuilder.Remove(doc, spec.Name, notes)) return null;
            switch (spec.Kind)
            {
                case "sheets": return Sheets(doc, spec.Name);
                case "views": return Views(doc, spec.Name);
                case "equipment": return Pieces(doc, spec.Name, BuiltInCategory.OST_GenericModel);
                case "planting": return Pieces(doc, spec.Name, BuiltInCategory.OST_Planting);
                case "buildups": return BuildUps(doc, spec.Name);
                case "areas": return Floors(doc, spec.Name, NormParameters.Din277);
                case "costs": return Floors(doc, spec.Name, NormParameters.Kg);
                default: return null;
            }
        }

        private static ViewSchedule Sheets(Document doc, string name)
        {
            var schedule = ViewSchedule.CreateSheetList(doc);      // Revit's own way to make a sheet list: the category cannot be given to CreateSchedule
            schedule.Name = name;
            var def = schedule.Definition;
            var number = BimScheduleBuilder.AddBuiltIn(def, BuiltInParameter.SHEET_NUMBER);
            BimScheduleBuilder.AddBuiltIn(def, BuiltInParameter.SHEET_NAME);
            if (number != null) def.AddSortGroupField(new ScheduleSortGroupField(number.FieldId, ScheduleSortOrder.Ascending));
            def.IsItemized = true;
            return schedule;
        }

        private static ViewSchedule Views(Document doc, string name)
        {
            var schedule = ViewSchedule.CreateViewList(doc);
            schedule.Name = name;
            var def = schedule.Definition;
            var viewName = BimScheduleBuilder.AddBuiltIn(def, BuiltInParameter.VIEW_NAME);
            BimScheduleBuilder.AddBuiltIn(def, BuiltInParameter.VIEW_SCALE);
            BimScheduleBuilder.AddBuiltIn(def, BuiltInParameter.VIEWER_SHEET_NUMBER);
            if (viewName != null) def.AddSortGroupField(new ScheduleSortGroupField(viewName.FieldId, ScheduleSortOrder.Ascending));
            def.IsItemized = true;
            return schedule;
        }

        /// <summary>Equipment (generic models) or planting: by Kostengruppe and kind, with the size, how many of each and the total.</summary>
        private static ViewSchedule Pieces(Document doc, string name, BuiltInCategory category)
        {
            var schedule = ViewSchedule.CreateSchedule(doc, new ElementId(category));
            schedule.Name = name;
            var def = schedule.Definition;
            var kg = BimScheduleBuilder.AddByName(doc, def, NormParameters.Kg);
            var kind = BimScheduleBuilder.AddByName(doc, def, "Sportify_Category");
            var familyType = BimScheduleBuilder.AddBuiltIn(def, BuiltInParameter.ELEM_FAMILY_AND_TYPE_PARAM);
            BimScheduleBuilder.AddByName(doc, def, "Sportify_LengthM");
            BimScheduleBuilder.AddByName(doc, def, "Sportify_WidthM");
            BimScheduleBuilder.AddByName(doc, def, "Sportify_ReferenceMaterial");
            var count = BimScheduleBuilder.AddCount(def);
            if (kind != null) def.AddFilter(new ScheduleFilter(kind.FieldId, ScheduleFilterType.HasValue));       // what Sportify made, not every generic model of the project
            if (kg != null) def.AddSortGroupField(new ScheduleSortGroupField(kg.FieldId, ScheduleSortOrder.Ascending));
            if (kind != null) def.AddSortGroupField(new ScheduleSortGroupField(kind.FieldId, ScheduleSortOrder.Ascending));
            if (familyType != null) def.AddSortGroupField(new ScheduleSortGroupField(familyType.FieldId, ScheduleSortOrder.Ascending));
            def.IsItemized = false;
            def.ShowGrandTotal = true;
            def.ShowGrandTotalCount = true;
            if (count != null) count.DisplayType = ScheduleFieldDisplayType.Totals;
            return schedule;
        }

        /// <summary>A material takeoff of the Sportify floor types: every layer's material with its area and volume.</summary>
        private static ViewSchedule BuildUps(Document doc, string name)
        {
            var schedule = ViewSchedule.CreateMaterialTakeoff(doc, new ElementId(BuiltInCategory.OST_Floors));
            schedule.Name = name;
            var def = schedule.Definition;
            var familyType = BimScheduleBuilder.AddBuiltIn(def, BuiltInParameter.ELEM_FAMILY_AND_TYPE_PARAM);
            var material = BimScheduleBuilder.AddBuiltIn(def, BuiltInParameter.MATERIAL_NAME);
            var area = BimScheduleBuilder.AddBuiltIn(def, BuiltInParameter.MATERIAL_AREA);
            var volume = BimScheduleBuilder.AddBuiltIn(def, BuiltInParameter.MATERIAL_VOLUME);
            if (familyType != null)
            {
                def.AddFilter(new ScheduleFilter(familyType.FieldId, ScheduleFilterType.Contains, BimRules.SportifyTypePrefix));
                def.AddSortGroupField(new ScheduleSortGroupField(familyType.FieldId, ScheduleSortOrder.Ascending));
            }
            if (material != null) def.AddSortGroupField(new ScheduleSortGroupField(material.FieldId, ScheduleSortOrder.Ascending));
            def.IsItemized = false;
            def.ShowGrandTotal = true;
            if (area != null) area.DisplayType = ScheduleFieldDisplayType.Totals;
            if (volume != null) volume.DisplayType = ScheduleFieldDisplayType.Totals;
            return schedule;
        }

        /// <summary>The Sportify floors (zones, roof finish) grouped by a norm parameter (Sportify_DIN277 for the areas, Sportify_KG for the costs), with their area and the total.</summary>
        private static ViewSchedule Floors(Document doc, string name, string groupBy)
        {
            var schedule = ViewSchedule.CreateSchedule(doc, new ElementId(BuiltInCategory.OST_Floors));
            schedule.Name = name;
            var def = schedule.Definition;
            var group = BimScheduleBuilder.AddByName(doc, def, groupBy);
            var familyType = BimScheduleBuilder.AddBuiltIn(def, BuiltInParameter.ELEM_FAMILY_AND_TYPE_PARAM);
            var area = BimScheduleBuilder.AddBuiltIn(def, BuiltInParameter.HOST_AREA_COMPUTED);
            var count = BimScheduleBuilder.AddCount(def);
            if (familyType != null) def.AddFilter(new ScheduleFilter(familyType.FieldId, ScheduleFilterType.Contains, BimRules.SportifyTypePrefix));
            if (group != null) def.AddSortGroupField(new ScheduleSortGroupField(group.FieldId, ScheduleSortOrder.Ascending) { ShowFooter = true, ShowBlankLine = false });
            if (familyType != null) def.AddSortGroupField(new ScheduleSortGroupField(familyType.FieldId, ScheduleSortOrder.Ascending));
            def.IsItemized = false;
            def.ShowGrandTotal = true;
            if (area != null) area.DisplayType = ScheduleFieldDisplayType.Totals;
            if (count != null) count.DisplayType = ScheduleFieldDisplayType.Totals;
            return schedule;
        }
    }
}
