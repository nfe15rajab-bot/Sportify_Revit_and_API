using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;

namespace SportfyRevit
{
    /// <summary>
    /// What the last Sportify import created in THIS project, so the next import replaces it instead of adding a second copy.
    ///
    /// Before this a manual Import Configuration added everything again on every run (two imports of one layout gave two copies of each piece, one
    /// on top of the other), and Auto Import replaced the previous import by a static, in-memory list of ElementIds that knew nothing about the
    /// document: with two projects open, the ids of the first were looked up in the second and whatever element had the same number was deleted,
    /// and after restarting Revit nothing was replaced at all.
    ///
    /// Now the record lives IN the document, in Extensible Storage on a DataStorage element (it is saved with the project, follows it to another
    /// machine, and belongs to exactly one document), and it lists the UniqueIds (unique across documents, unlike ElementIds) of what the
    /// import created: floors, family instances, placeholder boxes, text notes, model lines and their sketch planes. Types, loaded families,
    /// materials and worksets are NOT in it: they are reused by the next import, not rebuilt. Only elements listed here are ever deleted, and only
    /// by the next import of the same project. Manual and Auto Import both go through it (LayoutImporter).
    /// </summary>
    internal static class ImportLedger
    {
        private static readonly Guid SchemaId = new("6b1f6c58-2a0e-4c3f-9a0d-51b0a1f0c2d7");

        private static Schema GetSchema()
        {
            var existing = Schema.Lookup(SchemaId);
            if (existing != null) return existing;

            var builder = new SchemaBuilder(SchemaId);
            builder.SetSchemaName("SportifyImportLedger");
            builder.SetDocumentation("What the last Sportify import created in this project, so that the next import can replace it.");
            builder.SetReadAccessLevel(AccessLevel.Public);
            builder.SetWriteAccessLevel(AccessLevel.Public);
            builder.AddSimpleField("ImportId", typeof(string));
            builder.AddSimpleField("ImportedAtUtc", typeof(string));
            builder.AddSimpleField("Source", typeof(string));
            builder.AddArrayField("ElementUniqueIds", typeof(string));
            return builder.Finish();
        }

        private static List<DataStorage> Find(Document doc, Schema schema) =>
            new FilteredElementCollector(doc).OfClass(typeof(DataStorage)).Cast<DataStorage>()
                .Where(ds => ds.GetEntity(schema).IsValid())
                .ToList();

        /// <summary>
        /// Deletes what the previous import of this project created (and the old ledger). Inside the import transaction, so that a failed import
        /// puts it all back. Returns how many elements were removed; elements the user already deleted are simply not there any more.
        /// </summary>
        public static int RemovePrevious(Document doc)
        {
            var schema = GetSchema();
            int removed = 0;
            foreach (var storage in Find(doc, schema))
            {
                IList<string> uniqueIds;
                try { uniqueIds = storage.GetEntity(schema).Get<IList<string>>("ElementUniqueIds"); }
                catch (Exception ex) { SportifyLog.Warn("ledger", "an old ledger could not be read: " + ex.Message); uniqueIds = new List<string>(); }

                var ids = new List<ElementId>();
                foreach (var uid in uniqueIds)
                {
                    var el = string.IsNullOrEmpty(uid) ? null : doc.GetElement(uid);
                    if (el != null && el.Id != storage.Id) ids.Add(el.Id);
                }

                if (ids.Count > 0) removed += Delete(doc, ids);
                try { doc.Delete(storage.Id); } catch (Exception ex) { SportifyLog.Warn("ledger", "the old ledger could not be deleted: " + ex.Message); }
            }
            return removed;
        }

        /// <summary>One call when possible; when Revit refuses the lot (an element owned by someone else in a central model), one by one, and the failures are logged.</summary>
        private static int Delete(Document doc, List<ElementId> ids)
        {
            try { return doc.Delete(ids).Count; }
            catch (Exception ex)
            {
                SportifyLog.Warn("ledger", $"deleting {ids.Count} element(s) at once failed ({ex.Message}); trying one by one");
                int done = 0;
                foreach (var id in ids)
                {
                    try { if (doc.GetElement(id) != null) { doc.Delete(id); done++; } }
                    catch (Exception one) { SportifyLog.Warn("ledger", $"element {id.IntegerValue} could not be deleted: {one.Message}"); }
                }
                return done;
            }
        }

        /// <summary>Records what this import created. Inside the import transaction.</summary>
        public static void Write(Document doc, IEnumerable<ElementId> created, string source)
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
            entity.Set("ImportId", Guid.NewGuid().ToString("N"));
            entity.Set("ImportedAtUtc", DateTime.UtcNow.ToString("o"));
            entity.Set("Source", source);
            entity.Set<IList<string>>("ElementUniqueIds", uniqueIds);
            storage.SetEntity(entity);
            SportifyLog.Info("ledger", $"recorded {uniqueIds.Count} element(s) created by this import");
        }
    }
}
