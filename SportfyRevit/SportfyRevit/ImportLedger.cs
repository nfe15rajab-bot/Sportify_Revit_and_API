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
    /// by the next import of the SAME ROOF of the same project (RoofId) — a project can hold several roofs (a Revit level's roof and a lower
    /// annex's, say), each pushed and imported on its own, and importing one must never delete what an earlier import of a DIFFERENT roof
    /// built. Manual and Auto Import both go through it (LayoutImporter).
    /// </summary>
    internal static class ImportLedger
    {
        // Bumped (2026-09-28, adding RoofId) rather than reusing the old GUID: an Extensible Storage schema is locked to whatever fields it had
        // when a document (or this Revit session) first registered that GUID. Adding a field under the SAME GUID made Schema.Lookup hand back
        // the old, RoofId-less definition, and Entity.Set("RoofId", ...) then failed for real with "the name matches no field in this Entity's
        // Schema" -- a hard, unignorable import error, not a warning. A ledger entry written under the old GUID (6b1f6c58-2a0e-4c3f-9a0d-51b0a1f0c2d7)
        // is simply invisible to this code now; its elements are not deleted automatically, but nothing here can read or use that old schema
        // safely either, so leaving them and starting clean under the new GUID is the right trade-off. Whenever a field is added again, bump this again.
        private static readonly Guid SchemaId = new("77983038-c829-4b79-b63a-9a64358c10ab");

        /// <summary>A ledger entry from before roofs each got their own (no RoofId field at all) belongs to this bucket — treated as one particular roof's entry, not "every roof's", so it is only ever replaced by another import that also has no roof identity (the ContractCheck tool, an old manually re-imported export).</summary>
        internal const string UnknownRoofId = "";

        private static Schema GetSchema()
        {
            var existing = Schema.Lookup(SchemaId);
            if (existing != null) return existing;

            var builder = new SchemaBuilder(SchemaId);
            builder.SetSchemaName("SportifyImportLedger");
            builder.SetDocumentation("What the last Sportify import created in this project, so that the next import of the same roof can replace it.");
            builder.SetReadAccessLevel(AccessLevel.Public);
            builder.SetWriteAccessLevel(AccessLevel.Public);
            builder.AddSimpleField("ImportId", typeof(string));
            builder.AddSimpleField("ImportedAtUtc", typeof(string));
            builder.AddSimpleField("Source", typeof(string));
            builder.AddSimpleField("RoofId", typeof(string));
            builder.AddArrayField("ElementUniqueIds", typeof(string));
            return builder.Finish();
        }

        private static List<DataStorage> Find(Document doc, Schema schema) =>
            new FilteredElementCollector(doc).OfClass(typeof(DataStorage)).Cast<DataStorage>()
                .Where(ds => ds.GetEntity(schema).IsValid())
                .ToList();

        /// <summary>The RoofId an entry was written with — "" (UnknownRoofId) for one written before this field existed, read as a plain missing-field rather than an error so old projects keep working.</summary>
        private static string RoofIdOf(DataStorage storage, Schema schema)
        {
            try { return storage.GetEntity(schema).Get<string>("RoofId") ?? UnknownRoofId; }
            catch (Exception) { return UnknownRoofId; }
        }

        /// <summary>
        /// Deletes what the previous import of THIS roof of this project created (and its old ledger entry) — entries belonging to a
        /// different roof are left alone. Inside the import transaction, so that a failed import puts it all back. Returns how many
        /// elements were removed; elements the user already deleted are simply not there any more.
        /// </summary>
        public static int RemovePrevious(Document doc, string roofId) =>
            RemoveWhere(doc, storage => RoofIdOf(storage, GetSchema()) == roofId);

        /// <summary>The distinct roofs (other than <paramref name="excludeRoofId"/>) that already have a tracked import in this project — what
        /// "switching to the second roof" (ImportSportifyLayoutCommand) asks the user about before importing: keep them alongside this one, or
        /// remove them first. Read-only.</summary>
        public static List<string> OtherRoofIds(Document doc, string excludeRoofId)
        {
            var schema = GetSchema();
            return Find(doc, schema).Select(s => RoofIdOf(s, schema)).Where(id => id != excludeRoofId).Distinct().ToList();
        }

        /// <summary>
        /// Deletes every roof's previous import in this project, not just one — what "Update" (UpdateSportifyCommand) uses for an explicit,
        /// deliberate full reset. A normal import (LayoutImporter.Run, always scoped to one roof via RemovePrevious above) never calls this:
        /// several roofs pushed and imported independently must keep coexisting on their own.
        /// </summary>
        public static int RemoveAll(Document doc) => RemoveWhere(doc, _ => true);

        private static int RemoveWhere(Document doc, Func<DataStorage, bool> matches)
        {
            var schema = GetSchema();
            int removed = 0;
            foreach (var storage in Find(doc, schema))
            {
                if (!matches(storage)) continue;

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

        /// <summary>
        /// The elements the last import of this project created and that still exist (read only: nothing is changed). The BIM & Documentation commands (schedules, view filters,
        /// phasing, worksets) act on exactly these, so they never touch anything of the user's own. `roofId`: only that roof's own import, when several roofs are pushed and a
        /// caller (the functional diagrams, the analysis report's revision cloud) must not mix one roof's pieces into another's view or bounding box; null (the default) is
        /// every roof, which is what the BIM & Documentation commands still want — they act on the whole project, not just whichever roof happens to be active.
        /// </summary>
        public static List<Element> ReadElements(Document doc, string? roofId = null)
        {
            var schema = GetSchema();
            var found = new List<Element>();
            var seen = new HashSet<ElementId>();
            foreach (var storage in Find(doc, schema))
            {
                if (roofId != null && RoofIdOf(storage, schema) != roofId) continue;
                IList<string> uniqueIds;
                try { uniqueIds = storage.GetEntity(schema).Get<IList<string>>("ElementUniqueIds"); }
                catch (Exception ex) { SportifyLog.Warn("ledger", "a ledger could not be read: " + ex.Message); continue; }
                foreach (var uid in uniqueIds)
                {
                    var el = string.IsNullOrEmpty(uid) ? null : doc.GetElement(uid);
                    if (el != null && el.Id != storage.Id && seen.Add(el.Id)) found.Add(el);
                }
            }
            return found;
        }

        /// <summary>Records what this import created, tagged with the roof it belongs to. Inside the import transaction.</summary>
        public static void Write(Document doc, IEnumerable<ElementId> created, string source, string roofId)
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
            entity.Set("RoofId", roofId);
            entity.Set<IList<string>>("ElementUniqueIds", uniqueIds);
            storage.SetEntity(entity);
            SportifyLog.Info("ledger", $"recorded {uniqueIds.Count} element(s) created by this import (roof {(string.IsNullOrEmpty(roofId) ? "unknown" : roofId)})");
        }
    }
}
