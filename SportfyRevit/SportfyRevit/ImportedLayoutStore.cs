using System.Globalization;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;

namespace SportfyRevit
{
    /// <summary>
    /// The layout each import built, kept IN the project: one DataStorage per roof, replaced by that roof's next import, the JSON exactly as the web app
    /// sent it. What the analyses read when the web app has sent nothing in this Revit session (ProjectLayout): the add-in only holds what it was sent
    /// in memory, so after every Revit start the analyses had nothing, and fell back to Unity's demo layout (a 67.6 x 21 m roof with two badminton
    /// courts, found 2026-10-01 in a "Goldbeck — Low Roof, Sports" structural film) or to the newest export in the Layouts folder, whatever roof it was
    /// for (the High Roof garden's). Kept in the model, it travels with it to everyone who opens it.
    /// </summary>
    internal static class ImportedLayoutStore
    {
        static readonly Guid SchemaId = new("5b6f3c1e-8d2a-4c47-9e15-2f7a0c9d4b61");

        internal sealed record Stored(string RoofKey, DateTime ImportedAtUtc, string Json);

        static Schema GetSchema()
        {
            var existing = Schema.Lookup(SchemaId);
            if (existing != null) return existing;
            var builder = new SchemaBuilder(SchemaId);
            builder.SetSchemaName("SportifyImportedLayout");
            builder.SetDocumentation("The layout the last Sportify import of a roof built, as the web app sent it, for the analyses.");
            builder.SetReadAccessLevel(AccessLevel.Public);
            builder.SetWriteAccessLevel(AccessLevel.Public);
            builder.AddSimpleField("RoofId", typeof(string));
            builder.AddSimpleField("ImportedAtUtc", typeof(string));
            builder.AddSimpleField("LayoutJson", typeof(string));
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
                var when = DateTime.TryParse(entity.Get<string>("ImportedAtUtc"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t) ? t : DateTime.MinValue;
                var json = entity.Get<string>("LayoutJson");
                if (string.IsNullOrEmpty(json)) continue;
                yield return (storage, new Stored(entity.Get<string>("RoofId") ?? "", when, json));
            }
        }

        /// <summary>Keeps the layout of this roof's import (inside the caller's transaction), in place of the one its earlier import kept.</summary>
        internal static void Write(Document doc, string roofKey, string json)
        {
            var old = All(doc).Where(x => x.Item.RoofKey == roofKey).Select(x => x.Storage.Id).ToList();
            if (old.Count > 0) doc.Delete(old);
            var schema = GetSchema();
            var storage = DataStorage.Create(doc);
            var entity = new Entity(schema);
            entity.Set("RoofId", roofKey);
            entity.Set("ImportedAtUtc", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
            entity.Set("LayoutJson", json);
            storage.SetEntity(entity);
            ProjectMemoryFiles.Write(doc, "layout", roofKey, json);       // and on this computer, for a new local made from the central (ProjectMemoryFiles)
        }

        /// <summary>The layout last imported for this roof; for a roof that names none ("0", ""), or one never imported, the newest of any roof. Null when there is none.</summary>
        internal static Stored? Read(Document doc, string? roofKey)
        {
            var all = All(doc).Select(x => x.Item).OrderByDescending(x => x.ImportedAtUtc).ToList();
            if (all.Count == 0) return null;
            if (!RoofMatch.IsUnnamedRoofKey(roofKey) && all.FirstOrDefault(x => x.RoofKey == roofKey) is Stored mine) return mine;
            return all[0];
        }
    }

    /// <summary>
    /// The layout every analysis reads, from the place that knows this project's design: what the web app sent in this Revit session (for the active roof),
    /// else what was last imported into the open project (ImportedLayoutStore: the active roof's, else the newest). Never Unity's demo layout, and never a
    /// file picked for being the newest whatever roof it was for.
    /// </summary>
    internal static class ProjectLayout
    {
        internal const string NoLayoutMessage = "No layout of this project to analyse yet: in the Sportify web app, open the design and click Sync with Revit (or import a Combine export with Import Configuration), then run the analysis again.";

        internal static bool TryGet(Document? doc, out string? json, out string source)
        {
            doc ??= ProjectSession.Current;
            if (RoofBoundaryServer.TryGetLatestCombinedLayout(out json, out _) && json != null) { source = "the web app"; return true; }
            if (doc != null && !doc.IsFamilyDocument)
            {
                try
                {
                    if (ImportedLayoutStore.Read(doc, (RoofBoundaryServer.ActiveRoofId ?? 0).ToString()) is ImportedLayoutStore.Stored stored)
                    {
                        json = stored.Json;
                        source = $"the layout last imported into this project (roof {stored.RoofKey}, {stored.ImportedAtUtc.ToLocalTime():dd.MM.yyyy HH:mm})";
                        SportifyLog.Info("layout", "nothing from the web app in this Revit session: the analysis reads " + source);
                        return true;
                    }
                    // a new local made from the central carries nothing the old local had not synchronized: what this computer kept for the central
                    var files = ProjectMemoryFiles.Read(doc, "layout");
                    var active = (RoofBoundaryServer.ActiveRoofId ?? 0).ToString();
                    var mine = files.FirstOrDefault(f => f.RoofKey == active);
                    var pick = mine.Json != null ? mine : files.FirstOrDefault();
                    if (pick.Json != null)
                    {
                        json = pick.Json;
                        source = $"the layout last imported for this project on this computer (roof {pick.RoofKey}, {pick.WrittenUtc.ToLocalTime():dd.MM.yyyy HH:mm})";
                        SportifyLog.Info("layout", "nothing from the web app in this Revit session and none in the project: the analysis reads " + source);
                        return true;
                    }
                }
                catch (Exception ex) { SportifyLog.Warn("layout", "the layout kept in the project could not be read: " + ex.Message); }
            }
            json = null; source = "";
            return false;
        }
    }
}
