using Autodesk.Revit.DB;

namespace SportfyRevit
{
    internal sealed record BimScheduleResult(List<string> Created, List<string> Notes)
    {
        public bool Any => Created.Count > 0;
    }

    /// <summary>
    /// Native Revit schedules of what Sportify put in the project (they update as the model changes, unlike the CSV export):
    ///   "Sportify - Equipment Takeoff"          courts, activities and furniture (Generic Models with a Sportify_Category): category, family and type, quality key, size,
    ///                                           reference material and provider, and how many of each;
    ///   "Sportify - Planting Takeoff"           the same for planting, only when the project has some;
    ///   "Sportify - Green Roof Build-up Takeoff" a material takeoff of the Sportify floor types (the build-ups of the zones and the roof finish): every layer's material with its
    ///                                           area and volume.
    /// A schedule that already exists under the name is deleted and made again, so running the command twice does not give two. Call inside a transaction.
    /// </summary>
    internal static class BimScheduleBuilder
    {
        public const string EquipmentName = "Sportify - Equipment Takeoff";
        public const string PlantingName = "Sportify - Planting Takeoff";
        public const string RoofName = "Sportify - Green Roof Build-up Takeoff";

        /// <param name="layout">Optional: the layout last pushed from the web app, only named in the notes. The schedules read the model, not the layout.</param>
        public static BimScheduleResult CreateSportifySchedules(Document doc, SportifyLayout? layout)
        {
            var created = new List<string>();
            var notes = new List<string>();

            var found = SportifyElementScan.Find(doc);
            if (found.IsEmpty)
            {
                notes.Add("This project holds nothing from Sportify yet: import a layout first (Import Configuration, or Auto Import).");
                return new BimScheduleResult(created, notes);
            }

            var generic = found.Elements.Where(e => e is FamilyInstance && !SportifyElementScan.IsPlanting(e)).ToList();
            var planting = found.Elements.Where(e => e is FamilyInstance && SportifyElementScan.IsPlanting(e)).ToList();
            var floors = found.Elements.OfType<Floor>().ToList();

            if (generic.Count > 0)
            {
                var s = Equipment(doc, BuiltInCategory.OST_GenericModel, EquipmentName, notes);
                if (s != null) created.Add(s.Name);
            }
            else notes.Add("No courts, activities or furniture: no equipment schedule.");

            if (planting.Count > 0)
            {
                var s = Equipment(doc, BuiltInCategory.OST_Planting, PlantingName, notes);
                if (s != null) created.Add(s.Name);
            }

            if (floors.Count > 0)
            {
                var s = RoofTakeoff(doc, notes);
                if (s != null) created.Add(s.Name);
            }
            else notes.Add("No Sportify floors (zones, roof finish): no build-up takeoff.");

            SportifyLog.Info("bim", "schedules: " + (created.Count == 0 ? "none created" : string.Join(", ", created)) + (layout?.Placements != null ? $" (layout of {layout.Placements.Count} placement(s) pushed last)" : ""));
            return new BimScheduleResult(created, notes);
        }

        // ---------------------------------------------------------------- the two kinds

        private static ViewSchedule? Equipment(Document doc, BuiltInCategory category, string name, List<string> notes)
        {
            if (!Remove(doc, name, notes)) return null;
            var schedule = ViewSchedule.CreateSchedule(doc, new ElementId(category));
            schedule.Name = name;
            var def = schedule.Definition;

            var kind = AddByName(doc, def, "Sportify_Category");
            if (kind == null)
                notes.Add($"\"{name}\": the Sportify parameters are not in this project (they are added by an import), so it lists every {(category == BuiltInCategory.OST_Planting ? "plant" : "generic model")} without their details.");
            var familyType = AddBuiltIn(def, BuiltInParameter.ELEM_FAMILY_AND_TYPE_PARAM);
            foreach (var p in new[] { "Sportify_QualityKey", "Sportify_LengthM", "Sportify_WidthM", "Sportify_ReferenceMaterial", "Sportify_ReferenceProvider" })
                AddByName(doc, def, p);
            var count = AddCount(def);

            if (kind != null)
            {
                def.AddFilter(new ScheduleFilter(kind.FieldId, ScheduleFilterType.HasValue));       // only what Sportify made, not every generic model of the project
                def.AddSortGroupField(new ScheduleSortGroupField(kind.FieldId, ScheduleSortOrder.Ascending));
            }
            if (familyType != null) def.AddSortGroupField(new ScheduleSortGroupField(familyType.FieldId, ScheduleSortOrder.Ascending));
            def.IsItemized = false;                 // one row per kind of piece, with how many
            def.ShowGrandTotal = true;
            def.ShowGrandTotalCount = true;
            if (count != null) count.DisplayType = ScheduleFieldDisplayType.Totals;
            return schedule;
        }

        private static ViewSchedule? RoofTakeoff(Document doc, List<string> notes)
        {
            if (!Remove(doc, RoofName, notes)) return null;
            var schedule = ViewSchedule.CreateMaterialTakeoff(doc, new ElementId(BuiltInCategory.OST_Floors));
            schedule.Name = RoofName;
            var def = schedule.Definition;

            var familyType = AddBuiltIn(def, BuiltInParameter.ELEM_FAMILY_AND_TYPE_PARAM);
            var material = AddBuiltIn(def, BuiltInParameter.MATERIAL_NAME);
            var area = AddBuiltIn(def, BuiltInParameter.MATERIAL_AREA);
            var volume = AddBuiltIn(def, BuiltInParameter.MATERIAL_VOLUME);
            if (familyType == null || material == null)
            {
                notes.Add($"\"{RoofName}\": Revit did not offer the type or material fields for floors; the takeoff was left without them.");
                return schedule;
            }

            def.AddFilter(new ScheduleFilter(familyType.FieldId, ScheduleFilterType.Contains, BimRules.SportifyTypePrefix));   // the Sportify build-ups only, not the project's own floors
            def.AddSortGroupField(new ScheduleSortGroupField(familyType.FieldId, ScheduleSortOrder.Ascending));
            def.AddSortGroupField(new ScheduleSortGroupField(material.FieldId, ScheduleSortOrder.Ascending));
            def.IsItemized = false;
            def.ShowGrandTotal = true;
            if (area != null) area.DisplayType = ScheduleFieldDisplayType.Totals;
            if (volume != null) volume.DisplayType = ScheduleFieldDisplayType.Totals;
            return schedule;
        }

        /// <summary>What a generated schedule holds, for the message to the user and the log: "name: 5 fields, 7 rows". Call after the transaction that made it.</summary>
        public static string Describe(Document doc, string name)
        {
            var schedule = new FilteredElementCollector(doc).OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>().FirstOrDefault(v => v.Name == name);
            if (schedule == null) return name;
            try
            {
                var rows = schedule.GetTableData().GetSectionData(SectionType.Body).NumberOfRows;
                return $"{name}: {schedule.Definition.GetFieldCount()} fields, {rows} row(s)";
            }
            catch (Exception) { return name; }
        }

        // ---------------------------------------------------------------- helpers

        /// <summary>Deletes a schedule of ours that already exists. False when Revit will not (it is open in the active view): then it is left as it is and said so.</summary>
        private static bool Remove(Document doc, string name, List<string> notes)
        {
            var existing = new FilteredElementCollector(doc).OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>().FirstOrDefault(v => v.Name == name);
            if (existing == null) return true;
            try { doc.Delete(existing.Id); return true; }
            catch (Exception ex)
            {
                notes.Add($"\"{name}\" exists and could not be replaced ({ex.Message.Split('\n')[0]}): close it (switch to another view) and run the command again.");
                return false;
            }
        }

        private static ScheduleField? AddByName(Document doc, ScheduleDefinition def, string parameterName)
        {
            foreach (var f in def.GetSchedulableFields())
                if (f.GetName(doc) == parameterName) return def.AddField(f);
            return null;
        }

        private static ScheduleField? AddBuiltIn(ScheduleDefinition def, BuiltInParameter parameter)
        {
            var id = new ElementId(parameter);
            foreach (var f in def.GetSchedulableFields())
                if (f.ParameterId == id) return def.AddField(f);
            return null;
        }

        private static ScheduleField? AddCount(ScheduleDefinition def)
        {
            foreach (var f in def.GetSchedulableFields())
                if (f.FieldType == ScheduleFieldType.Count) return def.AddField(f);
            return null;
        }
    }
}
