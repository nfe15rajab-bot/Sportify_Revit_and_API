using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;

namespace SportfyRevit
{
    /// <summary>
    /// What the last "Import Iterations as Design Options" run created in THIS project — the same idea as ImportLedger, kept as its own,
    /// separate Extensible Storage schema on purpose: ImportLedger.RemovePrevious wipes everything under ITS schema with no filter by source,
    /// so sharing it would mean importing iterations deletes the normal current-layout import (and the next normal import would delete the
    /// iterations). Re-running the iterations import replaces only what an earlier iterations import made.
    /// </summary>
    internal static class IterationLedger
    {
        private static readonly Guid SchemaId = new("9d3e2a71-6f84-4e1b-8c2a-7b1f0d4c6a92");

        private static Schema GetSchema()
        {
            var existing = Schema.Lookup(SchemaId);
            if (existing != null) return existing;

            var builder = new SchemaBuilder(SchemaId);
            builder.SetSchemaName("SportifyIterationLedger");
            builder.SetDocumentation("What the last Sportify \"Import Iterations as Design Options\" run created in this project, so the next run can replace it.");
            builder.SetReadAccessLevel(AccessLevel.Public);
            builder.SetWriteAccessLevel(AccessLevel.Public);
            builder.AddArrayField("ElementUniqueIds", typeof(string));
            return builder.Finish();
        }

        private static List<DataStorage> Find(Document doc, Schema schema) =>
            new FilteredElementCollector(doc).OfClass(typeof(DataStorage)).Cast<DataStorage>()
                .Where(ds => ds.GetEntity(schema).IsValid())
                .ToList();

        /// <summary>Whether an earlier "Import Iterations as Design Options" run left anything in this project — checked before offering to clear it from a normal import.</summary>
        public static bool HasAny(Document doc) => Find(doc, GetSchema()).Count > 0;

        /// <summary>Deletes what the previous iterations import created (and the old ledger). Inside the import transaction, so a failed import puts it all back.</summary>
        public static int RemovePrevious(Document doc)
        {
            var schema = GetSchema();
            int removed = 0;
            foreach (var storage in Find(doc, schema))
            {
                IList<string> uniqueIds;
                try { uniqueIds = storage.GetEntity(schema).Get<IList<string>>("ElementUniqueIds"); }
                catch (Exception ex) { SportifyLog.Warn("iterations", "an old ledger could not be read: " + ex.Message); uniqueIds = new List<string>(); }

                var ids = new List<ElementId>();
                foreach (var uid in uniqueIds)
                {
                    var el = string.IsNullOrEmpty(uid) ? null : doc.GetElement(uid);
                    if (el != null && el.Id != storage.Id) ids.Add(el.Id);
                }

                if (ids.Count > 0)
                {
                    try { removed += doc.Delete(ids).Count; }
                    catch (Exception ex) { SportifyLog.Warn("iterations", $"deleting {ids.Count} previous iteration element(s) failed: {ex.Message}"); }
                }
                try { doc.Delete(storage.Id); } catch (Exception ex) { SportifyLog.Warn("iterations", "the old ledger could not be deleted: " + ex.Message); }
            }
            return removed;
        }

        /// <summary>Records what this iterations import created, across all the iterations it built. Inside the import transaction.</summary>
        public static void Write(Document doc, IEnumerable<ElementId> created)
        {
            var uniqueIds = new List<string>();
            foreach (var id in created.Distinct())
            {
                var el = doc.GetElement(id);
                if (el != null) uniqueIds.Add(el.UniqueId);
            }

            var schema = GetSchema();
            var storage = DataStorage.Create(doc);
            var entity = new Entity(schema);
            entity.Set<IList<string>>("ElementUniqueIds", uniqueIds);
            storage.SetEntity(entity);
            SportifyLog.Info("iterations", $"recorded {uniqueIds.Count} element(s) created by the iterations import");
        }
    }
}
