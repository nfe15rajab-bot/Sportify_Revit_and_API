using Autodesk.Revit.DB;

namespace SportfyRevit
{
    /// <summary>What "IFC Worksets by Class" would do to a project: every element the IFC import made, the workset its class puts it on, and what is already there.</summary>
    internal sealed class IfcWorksetPlan
    {
        public sealed record Item(Element Element, string? IfcClass, string Workset, bool Roof, bool AlreadyThere);

        public IfcWorksetTable Table { get; init; } = IfcWorksetRules.Defaults;
        public List<Item> Items { get; } = new();
        /// <summary>Where the elements go (a workset name and how many), whether or not they are there already.</summary>
        public Dictionary<string, int> ByWorkset { get; } = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>What the elements are (an IFC class, or "(no class)").</summary>
        public Dictionary<string, int> ByClass { get; } = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>The phase the elements were created in (the IFC import puts them all in the project's first phase).</summary>
        public Dictionary<string, int> Phases { get; } = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>Elements the IFC import made that say no IFC class (not even by their category): they stay where they are. The Revit category and how many.</summary>
        public Dictionary<string, int> Unclassified { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int WithoutClass => Unclassified.Values.Sum();
        public int AlreadyThere, SkippedSportify, RoofCount;
        public int ToMove => Items.Count - AlreadyThere;
    }

    internal sealed record IfcWorksetResult(Dictionary<string, int> Moved, int Already, int OwnedByOthers, int Refused, List<string> Made);

    /// <summary>
    /// Works out (Plan) and does (Apply) the worksets by IFC class for a workshared project. An element counts as "from the IFC" when it is a DirectShape (what an IFC import makes of what Revit has no
    /// family for) or carries the IfcSpatialContainer parameter; anything Sportify made (the import ledger, the Sportify worksets) is left alone. The class is read by IfcWorksetRules, the workset from
    /// the person's table. The roof is not guessed: the elements the person selected are the roof, and go on the roof workset whatever their class.
    /// </summary>
    internal static class IfcWorksetService
    {
        static string? Read(Element? el, string name)
        {
            if (el == null) return null;
            var p = el.LookupParameter(name);
            if (p == null || !p.HasValue) return null;
            return p.StorageType == StorageType.String ? p.AsString() : p.AsValueString();
        }

        /// <summary>The IFC import's marks: a DirectShape, or an element that says which IFC spatial container it was in.</summary>
        internal static bool IsFromIfc(Element el) => el is DirectShape || el.LookupParameter("IfcSpatialContainer") != null;

        internal static bool IsSportifyWorkset(string name, IfcWorksetTable table) =>
            !name.Equals(table.Roof, StringComparison.OrdinalIgnoreCase)
            && (BimRules.WorksetNames.Contains(name, StringComparer.OrdinalIgnoreCase) || name.StartsWith("Sportify", StringComparison.OrdinalIgnoreCase));

        public static IfcWorksetPlan Plan(Document doc, IfcWorksetTable table, ISet<ElementId>? roofIds = null)
        {
            var plan = new IfcWorksetPlan { Table = table };
            var sportify = new HashSet<long>(SportifyElementScan.Find(doc).Elements.Select(e => e.Id.Value));
            var worksets = doc.IsWorkshared
                ? new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset).ToDictionary(w => w.Id.IntegerValue, w => w.Name)
                : new Dictionary<int, string>();
            var phases = new Dictionary<long, string>();
            foreach (Phase ph in doc.Phases) phases[ph.Id.Value] = ph.Name;

            foreach (var el in new FilteredElementCollector(doc).WhereElementIsNotElementType())
            {
                if (el.WorksetId == WorksetId.InvalidWorksetId) continue;               // not something a workset can hold
                if (!IsFromIfc(el)) continue;
                var current = worksets.TryGetValue(el.WorksetId.IntegerValue, out var cn) ? cn : "";
                if (sportify.Contains(el.Id.Value) || IsSportifyWorkset(current, table)) { plan.SkippedSportify++; continue; }

                var type = doc.GetElement(el.GetTypeId());
                var category = el.Category != null ? ((BuiltInCategory)el.Category.Id.Value).ToString() : null;
                var ifcClass = IfcWorksetRules.ClassOf(n => Read(el, n), n => Read(type, n), category);
                var predefined = IfcWorksetRules.PredefinedOf(n => Read(el, n), n => Read(type, n));
                var isRoof = roofIds != null && roofIds.Contains(el.Id);
                var workset = IfcWorksetRules.WorksetFor(table, ifcClass, predefined, isRoof);
                if (workset == null)
                {
                    var kind = el.Category?.Name ?? "no category";
                    plan.Unclassified[kind] = plan.Unclassified.GetValueOrDefault(kind) + 1;
                    continue;
                }
                var there = current.Equals(workset, StringComparison.OrdinalIgnoreCase);

                plan.Items.Add(new IfcWorksetPlan.Item(el, ifcClass, workset, isRoof, there));
                plan.ByWorkset[workset] = plan.ByWorkset.GetValueOrDefault(workset) + 1;
                var cls = ifcClass ?? "(the roof you named)";
                plan.ByClass[cls] = plan.ByClass.GetValueOrDefault(cls) + 1;
                var phase = el.get_Parameter(BuiltInParameter.PHASE_CREATED)?.AsElementId();
                var phaseName = phase != null && phases.TryGetValue(phase.Value, out var pn) ? pn : "(no phase)";
                plan.Phases[phaseName] = plan.Phases.GetValueOrDefault(phaseName) + 1;
                if (there) plan.AlreadyThere++;
                if (isRoof) plan.RoofCount++;
            }
            return plan;
        }

        /// <summary>Puts every element of the plan on its workset (making the worksets that are missing) in one transaction. Needs a workshared project.</summary>
        public static IfcWorksetResult Apply(Document doc, IfcWorksetPlan plan)
        {
            var moved = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var made = new List<string>();
            int already = 0, others = 0, refused = 0;
            var central = doc.IsWorkshared && doc.GetWorksharingCentralModelPath() != null;      // a detached copy has no central model to ask about ownership
            using var t = new Transaction(doc, "Sportify: IFC worksets by class");
            t.Start();
            try
            {
                var ids = new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset).ToDictionary(w => w.Name, w => w.Id, StringComparer.OrdinalIgnoreCase);
                foreach (var item in plan.Items)
                {
                    if (item.AlreadyThere) { already++; continue; }
                    var p = item.Element.get_Parameter(BuiltInParameter.ELEM_PARTITION_PARAM);
                    if (p == null || p.IsReadOnly) { refused++; continue; }
                    if (!ids.TryGetValue(item.Workset, out var id))
                    {
                        id = Workset.Create(doc, item.Workset).Id;
                        ids[item.Workset] = id;
                        made.Add(item.Workset);
                    }
                    try
                    {
                        if (central && WorksharingUtils.GetCheckoutStatus(doc, item.Element.Id) == CheckoutStatus.OwnedByOtherUser) { others++; continue; }
                        if (p.Set(id.IntegerValue)) moved[item.Workset] = moved.GetValueOrDefault(item.Workset) + 1; else refused++;
                    }
                    catch (Exception) { refused++; }
                }
                t.Commit();
            }
            catch (Exception)
            {
                if (t.HasStarted() && !t.HasEnded()) t.RollBack();
                throw;
            }
            return new IfcWorksetResult(moved, already, others, refused, made);
        }

        /// <summary>The name of the workset an element is on now.</summary>
        internal static string WorksetNameOf(Document doc, Element el) => doc.GetWorksetTable().GetWorkset(el.WorksetId).Name;
    }
}
