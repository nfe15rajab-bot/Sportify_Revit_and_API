using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;

namespace SportfyRevit
{
    /// <summary>
    /// The SubmissionFix step "bim" (user, 2026-10-01: "check the 3d model itself, make sure it matches the criteria of a bim model"): what an audit of the Goldbeck model
    /// (Sportify.ModelInspect, BimAudit) found wrong and a program can set right without touching what a person made:
    ///   - the project location: Revit's default (Boston) replaced by the project's own (SPORTIFY_FIX_SITE="lat,lon,utc offset,name"), only while it still is the default;
    ///   - every Sportify piece given its host level and its DIN 276 / DIN 277 classes (BimFinish);
    ///   - the Garden tab's Park Bench and Table: DirectShape boxes replaced by a real family at the same spot, workset and phase.
    /// Each part in its own transaction, so one that fails leaves the others done.
    /// </summary>
    internal static class BimFix
    {
        internal static void Run(Document doc, List<string> lines)
        {
            try { Site(doc, lines); } catch (Exception ex) { lines.Add("BIM site FAILED: " + ex.Message); SportifyLog.Error("bim", "site failed", ex); }
            try { Benches(doc, lines); } catch (Exception ex) { lines.Add("BIM benches FAILED: " + ex.Message); SportifyLog.Error("bim", "benches failed", ex); }
            try { LevelsAndNorms(doc, lines); } catch (Exception ex) { lines.Add("BIM levels and norms FAILED: " + ex.Message); SportifyLog.Error("bim", "levels and norms failed", ex); }
        }

        static void Site(Document doc, List<string> lines)
        {
            var site = doc.SiteLocation;
            var lat = site.Latitude * 180 / Math.PI; var lon = site.Longitude * 180 / Math.PI;
            var isDefault = Math.Abs(lat - 42.387) < 0.05 && Math.Abs(lon + 71.242) < 0.05;
            if (!isDefault) { lines.Add($"BIM site: left as it is ({lat:0.0000}, {lon:0.0000}, \"{site.PlaceName}\")"); return; }
            var spec = Environment.GetEnvironmentVariable("SPORTIFY_FIX_SITE");
            var parts = (spec ?? "").Split(',');
            if (parts.Length < 4 || !double.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var la)
                || !double.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var lo)
                || !double.TryParse(parts[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var tz))
            { lines.Add("BIM site: still the Revit default (Boston) and no SPORTIFY_FIX_SITE (\"lat,lon,utc,name\") given"); return; }
            using var t = new Transaction(doc, "Sportify: project location");
            t.Start();
            site.Latitude = la * Math.PI / 180; site.Longitude = lo * Math.PI / 180; site.TimeZone = tz; site.PlaceName = string.Join(",", parts.Skip(3)).Trim();
            t.Commit();
            lines.Add($"BIM site: {lat:0.0000}, {lon:0.0000} (the Revit default, Boston) -> {la:0.0000}, {lo:0.0000}, UTC{tz:+0;-0}, \"{site.PlaceName}\"");
        }

        static void LevelsAndNorms(Document doc, List<string> lines)
        {
            var unhosted = new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>()
                .Where(f => !string.IsNullOrWhiteSpace(SportifyElementScan.CategoryOf(f)) && f.LevelId == ElementId.InvalidElementId).ToList();
            int withScheduleLevel = 0, noParam = 0, readOnly = 0;
            foreach (var f in unhosted)
            {
                var sl = f.get_Parameter(BuiltInParameter.INSTANCE_SCHEDULE_ONLY_LEVEL_PARAM);
                if (sl == null) noParam++; else if (sl.IsReadOnly) readOnly++; else if (sl.AsElementId() is { } id && id != ElementId.InvalidElementId) withScheduleLevel++;
            }
            lines.Add($"BIM levels: {unhosted.Count} Sportify instance(s) without a host level: {withScheduleLevel} already have a Schedule Level, {noParam} have no such parameter, {readOnly} read-only");
            using var t = new Transaction(doc, "Sportify: host levels and norm classes");
            t.Start();
            var r = BimFinish.ApplyAll(doc);
            t.Commit();
            lines.Add($"BIM levels and norms: {r.Levelled} piece(s) given their host level (Schedule Level); {r.Classed} element(s) classed by DIN 276 / DIN 277 ({r.Floors} floor(s), {r.Pieces} piece(s)); " +
                      (BimFinish.LanguageOf(doc) is TemplateLanguage l ? "template language " + l : "no Sportify template in this project: norm classes not written") + (r.Notes.Length > 0 ? "; " + r.Notes : ""));
        }

        static void Benches(Document doc, List<string> lines)
        {
            var boxes = new FilteredElementCollector(doc).OfClass(typeof(DirectShape)).Cast<DirectShape>()
                .Where(d => d.Name.StartsWith("Park_Bench_and_Table", StringComparison.OrdinalIgnoreCase)).ToList();
            if (boxes.Count == 0) { lines.Add("BIM benches: no Park Bench and Table placeholder boxes"); return; }
            // the family template the builder works from is found before the transaction opens (it opens template files)
            if (GenericFamilyTemplateLocator.Resolve() == null) GenericFamilyTemplateLocator.Prepare(doc.Application, allowDialog: false);
            var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().ToList();
            var texts = new FilteredElementCollector(doc).OfClass(typeof(TextNote)).Cast<TextNote>().ToList();
            int replaced = 0; var failures = new List<string>();
            using var t = new Transaction(doc, "Sportify: park benches as families");
            t.Start();
            foreach (var ds in boxes)
            {
                try
                {
                    var bb = ds.get_BoundingBox(null);
                    if (bb == null) { failures.Add($"#{ds.Id.Value} has no box"); continue; }
                    double w = (bb.Max.X - bb.Min.X) * 0.3048, h = (bb.Max.Y - bb.Min.Y) * 0.3048;
                    var spec = new FurnitureDto { Key = "park_bench_table", Label = "Park Bench and Table", Category = "picnic", LengthM = Math.Round(w, 2), WidthM = Math.Round(h, 2), HeightM = 0.8, Seats = 4 };
                    var symbol = SportifyFurnitureFamilyBuilder.GetOrCreateSymbol(doc, spec, out _);
                    if (symbol == null) { failures.Add($"#{ds.Id.Value}: no family"); continue; }
                    if (!symbol.IsActive) { symbol.Activate(); doc.Regenerate(); }
                    var centre = new XYZ((bb.Min.X + bb.Max.X) / 2, (bb.Min.Y + bb.Max.Y) / 2, bb.Min.Z);
                    var instance = FamilyPlacementBuilder.NewInstance(doc, centre, symbol);
                    SportifyLayoutBuilder.SetWorkset(instance, ds.WorksetId);
                    FamilyPlacementBuilder.MoveToElevation(doc, instance, centre.Z);
                    var phase = ds.get_Parameter(BuiltInParameter.PHASE_CREATED)?.AsElementId();
                    if (phase != null && phase != ElementId.InvalidElementId) instance.get_Parameter(BuiltInParameter.PHASE_CREATED)?.Set(phase);
                    foreach (Parameter p in ds.Parameters)
                    {
                        var name = p.Definition?.Name;
                        if (name == null || !name.StartsWith("Sportify_", StringComparison.Ordinal) || p.StorageType != StorageType.String || !p.HasValue) continue;
                        var target = instance.LookupParameter(name);
                        if (target != null && !target.IsReadOnly && string.IsNullOrEmpty(target.AsString())) target.Set(p.AsString());
                    }
                    BimFinish.EnsureLevel(instance, levels);
                    // the label text the placeholder came with is the family's own name now: the orphan is removed with it
                    foreach (var tx in texts.Where(x => x.IsValidObject && string.Equals(x.Text?.Trim(), ds.Name, StringComparison.OrdinalIgnoreCase)
                                                        && Math.Abs(x.Coord.X - centre.X) < 8 && Math.Abs(x.Coord.Y - centre.Y) < 8).ToList())
                        doc.Delete(tx.Id);
                    doc.Delete(ds.Id);
                    replaced++;
                }
                catch (Exception ex) { failures.Add($"#{ds.Id.Value}: {ex.Message}"); }
            }
            t.Commit();
            lines.Add($"BIM benches: {replaced} of {boxes.Count} placeholder box(es) replaced by a family" + (failures.Count == 0 ? "" : "; " + string.Join("; ", failures)));
        }
    }
}
