using System.Globalization;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.DB.ExtensibleStorage;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;

namespace SportfyRevit
{
    /// <summary>
    /// Every roof pushed to Sportify (its outline, structure, entries, openings ... as the push built them), kept IN the project, one DataStorage per roof,
    /// replaced by that roof's next push (user, 2026-10-01: "make the program remember these pushed elements even when the file is open 1 month later").
    /// The add-in held pushes in memory only, so after every Revit start the roof had to be pushed again, and until then the analyses ran on whatever
    /// they found instead. Given back to the add-in (RoofBoundaryServer) when the project opens (ProjectSession), the newest one active.
    /// </summary>
    internal static class PushedRoofStore
    {
        static readonly Guid SchemaId = new("9c41e2d7-3b5a-4f68-a0d2-6e8b1f5c7a93");

        internal sealed record Stored(long RoofId, DateTime PushedAtUtc, string Json);

        static Schema GetSchema()
        {
            var existing = Schema.Lookup(SchemaId);
            if (existing != null) return existing;
            var builder = new SchemaBuilder(SchemaId);
            builder.SetSchemaName("SportifyPushedRoof");
            builder.SetDocumentation("A roof pushed to Sportify (outline, structure, entries ...), as the push built it, so the add-in knows it when the project is opened again.");
            builder.SetReadAccessLevel(AccessLevel.Public);
            builder.SetWriteAccessLevel(AccessLevel.Public);
            builder.AddSimpleField("RoofId", typeof(string));
            builder.AddSimpleField("PushedAtUtc", typeof(string));
            builder.AddSimpleField("PayloadJson", typeof(string));
            return builder.Finish();
        }

        static IEnumerable<(DataStorage Storage, Stored Item)> All(Document doc)
        {
            var schema = Schema.Lookup(SchemaId);
            if (schema == null) yield break;
            foreach (var storage in new FilteredElementCollector(doc).OfClass(typeof(DataStorage)).Cast<DataStorage>())
            {
                Entity entity;
                try { entity = storage.GetEntity(schema); } catch (Exception) { continue; }
                if (entity == null || !entity.IsValid()) continue;
                if (!long.TryParse(entity.Get<string>("RoofId"), out var roofId)) continue;
                var when = DateTime.TryParse(entity.Get<string>("PushedAtUtc"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t) ? t : DateTime.MinValue;
                var json = entity.Get<string>("PayloadJson");
                if (string.IsNullOrEmpty(json)) continue;
                yield return (storage, new Stored(roofId, when, json));
            }
        }

        /// <summary>Keeps this roof's push (its own transaction: a push changes nothing else in the model) in place of the one kept before. Best-effort.</summary>
        internal static void Write(Document doc, long roofId, string json)
        {
            if (doc.IsReadOnly || doc.IsFamilyDocument) return;
            try
            {
                using var t = new Transaction(doc, "Sportify: remember the pushed roof");
                t.Start();
                var old = All(doc).Where(x => x.Item.RoofId == roofId).Select(x => x.Storage.Id).ToList();
                if (old.Count > 0) doc.Delete(old);
                var storage = DataStorage.Create(doc);
                var entity = new Entity(GetSchema());
                entity.Set("RoofId", roofId.ToString(CultureInfo.InvariantCulture));
                entity.Set("PushedAtUtc", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
                entity.Set("PayloadJson", json);
                storage.SetEntity(entity);
                t.Commit();
                SportifyLog.Info("push", $"the push of roof {roofId} is kept in the project: it is known again whenever the project is opened");
            }
            catch (Exception ex) { SportifyLog.Warn("push", "the push could not be kept in the project (it is known until Revit closes): " + ex.Message); }
        }

        /// <summary>Every push kept in the project, newest first.</summary>
        internal static List<Stored> Read(Document doc) => All(doc).Select(x => x.Item).OrderByDescending(x => x.PushedAtUtc).ToList();
    }

    /// <summary>
    /// What a project remembers of its Sportify session, given back when it opens: its pushed roofs (PushedRoofStore) to the add-in, so the web app and
    /// the analyses know them without a new push. Also which project is the active one, for the steps that are not handed a document (ProjectLayout).
    /// </summary>
    internal static class ProjectSession
    {
        static Document? _current;

        /// <summary>The project last opened or looked at in Revit, while it is still open; null otherwise.</summary>
        internal static Document? Current => _current != null && _current.IsValidObject ? _current : null;

        internal static void Register(UIControlledApplication app)
        {
            app.ControlledApplication.DocumentOpened += OnOpened;
            app.ViewActivated += (_, e) => { if (e.Document is Document d && !d.IsFamilyDocument) _current = d; };
            app.ControlledApplication.DocumentClosing += (_, e) => { if (_current != null && _current.IsValidObject && ReferenceEquals(_current, e.Document)) _current = null; };
        }

        static void OnOpened(object? sender, DocumentOpenedEventArgs e)
        {
            var doc = e.Document;
            if (doc == null || doc.IsFamilyDocument || doc.IsLinked) return;
            _current = doc;
            try
            {
                var pushes = PushedRoofStore.Read(doc);
                if (pushes.Count == 0) return;
                foreach (var p in pushes) RoofBoundaryServer.RestorePayload(p.RoofId, p.Json, p.PushedAtUtc, makeActiveIfNone: true);
                SportifyLog.Info("push", $"\"{doc.Title}\": {pushes.Count} roof push(es) kept in the project given back (newest: roof {pushes[0].RoofId}, {pushes[0].PushedAtUtc.ToLocalTime():dd.MM.yyyy HH:mm})");
            }
            catch (Exception ex) { SportifyLog.Warn("push", "the pushes kept in the project could not be read: " + ex.Message); }
        }
    }
}
