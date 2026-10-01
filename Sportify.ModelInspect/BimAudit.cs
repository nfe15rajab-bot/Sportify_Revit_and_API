using System.Globalization;
using System.Text;
using System.Text.Json;
using Autodesk.Revit.DB;

namespace Sportify.ModelInspect
{
    /// <summary>
    /// A read-only audit of a Revit project against what makes it a BIM model and not a drawing (user, 2026-10-01: "check the 3d model itself, make sure it matches the criteria of
    /// a bim model"): native elements instead of imports and DirectShapes, the right categories, data on every element (Sportify_Category, DIN 276 / DIN 277 classes, IFC class),
    /// levels, worksets, phases and design options in order, no duplicates or floating pieces, parity with the layout the web app last imported (the planters included), and a
    /// documentation set (sheets, schedules, tags) that is complete. Writes bim-audit.txt and bim-audit.json next to the inspection; the project is only read, on the copy the
    /// inspector opened (SPORTIFY_INSPECT_LAYOUT names the layout JSON to compare with).
    /// </summary>
    internal static class BimAudit
    {
        const double FeetToM = 0.3048;

        sealed record Check(string Group, string Name, string Status, string Detail, List<string> Samples);

        sealed class Piece
        {
            public Element E = null!;
            public string Kind = "";            // "instance" | "floor" | "line" | "other"
            public string Category = "", Family = "", Type = "", Level = "", Workset = "", Phase = "", Option = "";
            public string SportifyCategory = "", Kg = "", Din277 = "", IfcAs = "", Mark = "", RoofId = "";
            public bool InPlace, HasSolid, HasMaterial, DirectShape; public string PlacementType = "", LevelParam = "", ScheduleLevel = "";
            public double[]? Min, Max;           // metres, model coordinates
            public double OffsetM;               // offset from the host level, metres
            public double Volume;
            public string Label => $"#{E.Id.Value} {Family}{(Type.Length > 0 && Type != Family ? " : " + Type : "")}";
        }

        static readonly List<Check> Checks = new();
        static void Add(string group, string name, string status, string detail, IEnumerable<string>? samples = null) =>
            Checks.Add(new Check(group, name, status, detail, samples?.Take(12).ToList() ?? new List<string>()));

        internal static void Run(Document doc, string outDir, Dictionary<string, object?> report, List<string> failures)
        {
            Checks.Clear();
            try { Audit(doc, outDir, failures); }
            catch (Exception ex) { failures.Add("the BIM audit failed: " + ex); }
            try
            {
                File.WriteAllText(Path.Combine(outDir, "bim-audit.txt"), Text());
                File.WriteAllText(Path.Combine(outDir, "bim-audit.json"), JsonSerializer.Serialize(Checks, new JsonSerializerOptions { WriteIndented = true }));
                report["bim_audit"] = Checks.GroupBy(c => c.Status).ToDictionary(g => g.Key, g => g.Count());
            }
            catch (Exception ex) { failures.Add("the BIM audit could not be written: " + ex.Message); }
        }

        // ------------------------------------------------------------------------------------------------------------------------------------------------ the audit

        static void Audit(Document doc, string outDir, List<string> failures)
        {
            var phases = new Dictionary<long, string>();
            foreach (Phase p in doc.Phases) phases[p.Id.Value] = p.Name;
            var worksetNames = new Dictionary<int, string>();
            if (doc.IsWorkshared) foreach (var w in new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset).ToWorksets()) worksetNames[w.Id.IntegerValue] = w.Name;
            var layout = ReadLayout();

            // ---- every Sportify element
            var pieces = new List<Piece>();
            var otherSportifyLines = 0; var linesByStyle = new Dictionary<string, int>();
            foreach (var e in new FilteredElementCollector(doc).WhereElementIsNotElementType())
            {
                if (e.Category == null) continue;
                var ws = doc.IsWorkshared && worksetNames.TryGetValue(e.WorksetId.IntegerValue, out var wn) ? wn : "";
                if (e is CurveElement ce && ce.LineStyle is GraphicsStyle gs && gs.Name.StartsWith("Sportify", StringComparison.Ordinal))
                {
                    linesByStyle[gs.Name] = linesByStyle.GetValueOrDefault(gs.Name) + 1; otherSportifyLines++; continue;
                }
                string sc = Str(e, "Sportify_Category");
                var isFloor = e is Floor && doc.GetElement(e.GetTypeId())?.Name.StartsWith("Sportify - ", StringComparison.Ordinal) == true;
                if (sc.Length == 0 && !isFloor) continue;
                pieces.Add(Describe(doc, e, sc, ws, phases, isFloor));
            }
            try
            {
                var csv = new StringBuilder("id;kind;category;family;type;sportify_category;workset;level;phase;option;minx;miny;minz;maxx;maxy;maxz;offset_m;ifc;kg;din277;roof;placement;level_param;schedule_level\n");
                foreach (var p in pieces.OrderBy(p => p.Workset).ThenBy(p => p.Family))
                    csv.AppendLine(string.Join(";", new[] { p.E.Id.Value.ToString(), p.Kind, p.Category, p.Family, p.Type.Length > 40 ? p.Type.Substring(0, 40) : p.Type, p.SportifyCategory, p.Workset, p.Level, p.Phase, p.Option,
                        N(p.Min, 0), N(p.Min, 1), N(p.Min, 2), N(p.Max, 0), N(p.Max, 1), N(p.Max, 2), p.OffsetM.ToString("0.000", CultureInfo.InvariantCulture), p.IfcAs, p.Kg, p.Din277, p.RoofId, p.PlacementType, p.LevelParam, p.ScheduleLevel }));
                File.WriteAllText(Path.Combine(outDir, "bim-pieces.csv"), csv.ToString());
                var slabs = new StringBuilder("id;name;level;workset;minx;miny;minz;maxx;maxy;maxz\n");
                foreach (var e in new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Floors).WhereElementIsNotElementType())
                {
                    var bb = e.get_BoundingBox(null); if (bb == null || e is not DirectShape) continue;
                    slabs.AppendLine(string.Join(";", new[] { e.Id.Value.ToString(), e.Name, e.LevelId != ElementId.InvalidElementId ? doc.GetElement(e.LevelId)?.Name ?? "" : "", doc.IsWorkshared && worksetNames.TryGetValue(e.WorksetId.IntegerValue, out var w) ? w : "",
                        (bb.Min.X * FeetToM).ToString("0.000", CultureInfo.InvariantCulture), (bb.Min.Y * FeetToM).ToString("0.000", CultureInfo.InvariantCulture), (bb.Min.Z * FeetToM).ToString("0.000", CultureInfo.InvariantCulture),
                        (bb.Max.X * FeetToM).ToString("0.000", CultureInfo.InvariantCulture), (bb.Max.Y * FeetToM).ToString("0.000", CultureInfo.InvariantCulture), (bb.Max.Z * FeetToM).ToString("0.000", CultureInfo.InvariantCulture) }));
                }
                File.WriteAllText(Path.Combine(outDir, "bim-slabs.csv"), slabs.ToString());
            }
            catch (Exception ex) { failures.Add("the piece dump failed: " + ex.Message); }
            var instances = pieces.Where(p => p.Kind == "instance").ToList();
            var floors = pieces.Where(p => p.Kind == "floor").ToList();

            Add("Inventory", "Sportify elements", "INFO",
                $"{pieces.Count} elements: {instances.Count} family instances, {floors.Count} floors (build-ups); {otherSportifyLines} model lines in Sportify line styles ({string.Join(", ", linesByStyle.Select(kv => kv.Key + " " + kv.Value))})");
            Add("Inventory", "By category", "INFO", string.Join("; ", pieces.GroupBy(p => p.Category).OrderByDescending(g => g.Count()).Select(g => g.Key + " " + g.Count())));
            Add("Inventory", "By Sportify_Category", "INFO", string.Join("; ", pieces.GroupBy(p => p.SportifyCategory.Length == 0 ? "(floor)" : p.SportifyCategory).OrderByDescending(g => g.Count()).Select(g => g.Key + " " + g.Count())));

            // ---- native elements, not drawings
            var imports = new FilteredElementCollector(doc).OfClass(typeof(ImportInstance)).GetElementCount();
            Add("Native elements", "No CAD imports or links in the model", imports == 0 ? "PASS" : "FAIL", imports + " import instance(s)");
            var ds = pieces.Where(p => p.DirectShape).ToList();
            Add("Native elements", "Sportify elements are parametric family instances / floors, not DirectShapes", ds.Count == 0 ? "PASS" : "FAIL", ds.Count + " DirectShape(s)", ds.Select(p => p.Label));
            var inplace = pieces.Where(p => p.InPlace).ToList();
            Add("Native elements", "No in-place families among Sportify elements", inplace.Count == 0 ? "PASS" : "WARN", inplace.Count + " in-place", inplace.Select(p => p.Label));
            var noGeom = instances.Where(p => !p.HasSolid).ToList();
            Add("Native elements", "Every piece has real 3D geometry", noGeom.Count == 0 ? "PASS" : "FAIL", noGeom.Count + " of " + instances.Count + " family instances without a solid", noGeom.Select(p => p.Label + " (box " + Box(p) + ")"));
            var flat = instances.Where(p => p.HasSolid && p.Min != null && p.Max != null && (p.Max[2] - p.Min[2]) < 0.01).ToList();
            Add("Native elements", "No piece is flat (zero height)", flat.Count == 0 ? "PASS" : "WARN", flat.Count + " instance(s) 1 cm or lower", flat.Select(p => p.Label));
            var noMat = instances.Where(p => p.HasSolid && !p.HasMaterial).ToList();
            Add("Native elements", "Pieces carry a material (for colour, take-off and IFC)", noMat.Count == 0 ? "PASS" : "WARN", noMat.Count + " of " + instances.Count + " instances show no material on any face", noMat.Select(p => p.Label));
            var inGen = pieces.GroupBy(p => p.Category).ToDictionary(g => g.Key, g => g.Count());
            Add("Native elements", "Categories used", "INFO", string.Join("; ", inGen.OrderByDescending(kv => kv.Value).Select(kv => kv.Key + " " + kv.Value)));

            // ---- data on elements
            var noSc = instances.Where(p => p.SportifyCategory.Length == 0).ToList();
            Add("Data", "Sportify_Category on every piece", noSc.Count == 0 ? "PASS" : "FAIL", noSc.Count + " without", noSc.Select(p => p.Label));
            var noKg = pieces.Where(p => p.Kg.Length == 0).ToList();
            Add("Data", "DIN 276 cost group (Sportify_KG) on every element", noKg.Count == 0 ? "PASS" : "WARN", noKg.Count + " of " + pieces.Count + " without", noKg.Select(p => p.Label));
            var noDin = floors.Where(p => p.Din277.Length == 0).ToList();
            Add("Data", "DIN 277 area class (Sportify_DIN277) on every build-up floor", noDin.Count == 0 ? "PASS" : "WARN", noDin.Count + " of " + floors.Count + " floors without", noDin.Select(p => p.Label));
            var ifc = pieces.GroupBy(p => p.IfcAs.Length == 0 ? "(not set: Revit maps the category)" : p.IfcAs).ToDictionary(g => g.Key, g => g.Count());
            Add("Data", "IFC export class", pieces.All(p => p.IfcAs.Length > 0) ? "PASS" : "INFO", string.Join("; ", ifc.Select(kv => kv.Key + " " + kv.Value)));
            var noMark = instances.Count(p => p.Mark.Length == 0);
            Add("Data", "Mark (identifier) on pieces", noMark == 0 ? "PASS" : "INFO", noMark + " of " + instances.Count + " without a Mark");
            var badNames = instances.Where(p => LooksGeneric(p.Family) || LooksGeneric(p.Type)).ToList();
            Add("Data", "Family and type names say what the piece is", badNames.Count == 0 ? "PASS" : "WARN", badNames.Count + " generic or default name(s)", badNames.Select(p => p.Label));
            var famNames = instances.GroupBy(p => p.Family).OrderByDescending(g => g.Count()).Select(g => g.Key + " (" + g.Count() + ")");
            Add("Data", "Families in use", "INFO", string.Join("; ", famNames));

            // ---- levels, phases, worksets, options
            var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().ToList();
            var dupLevel = levels.GroupBy(l => l.Name).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            Add("Structure", "Levels: unique names", dupLevel.Count == 0 ? "PASS" : "FAIL", levels.Count + " levels", dupLevel);
            var noLevel = pieces.Where(p => p.Level.Length == 0 && p.Kind != "line").ToList();
            Add("Structure", "Every Sportify element is hosted on a level", noLevel.Count == 0 ? "PASS" : "FAIL", noLevel.Count + " without a level", noLevel.Select(p => p.Label));
            Add("Structure", "Host levels of Sportify elements", "INFO", string.Join("; ", pieces.GroupBy(p => p.Level).Select(g => (g.Key.Length == 0 ? "(none)" : g.Key) + " " + g.Count())));
            Add("Structure", "Phases", phases.Count > 1 ? "PASS" : "WARN",
                string.Join("; ", phases.Select(kv => kv.Value + " (created: " + pieces.Count(p => p.Phase == kv.Value) + ")")) + (phases.Count > 1 ? "" : " : only one phase, existing and new cannot be told apart"));
            var phaseless = pieces.Where(p => p.Phase.Length == 0).ToList();
            Add("Structure", "Every element has a creation phase", phaseless.Count == 0 ? "PASS" : "FAIL", phaseless.Count + " without", phaseless.Select(p => p.Label));
            if (doc.IsWorkshared)
            {
                var wk = pieces.GroupBy(p => p.Workset.Length == 0 ? "(none)" : p.Workset).ToDictionary(g => g.Key, g => g.Count());
                Add("Structure", "Sportify elements by workset", "INFO", string.Join("; ", wk.OrderByDescending(kv => kv.Value).Select(kv => kv.Key + " " + kv.Value)));
                var wrong = pieces.Where(p => !p.Workset.StartsWith("Sportify", StringComparison.OrdinalIgnoreCase) && !p.Workset.StartsWith("Combine", StringComparison.OrdinalIgnoreCase) && !p.Workset.StartsWith("Gardens", StringComparison.OrdinalIgnoreCase) && !p.Workset.StartsWith("Sports", StringComparison.OrdinalIgnoreCase)).ToList();
                Add("Structure", "Sportify elements are on Sportify worksets", wrong.Count == 0 ? "PASS" : "WARN", wrong.Count + " on another workset", wrong.Select(p => p.Label + " on '" + p.Workset + "'"));
                var all = new FilteredElementCollector(doc).WhereElementIsNotElementType().ToElements();
                var counts = all.Where(e => e.Category != null && worksetNames.ContainsKey(e.WorksetId.IntegerValue)).GroupBy(e => worksetNames[e.WorksetId.IntegerValue]).ToDictionary(g => g.Key, g => g.Count());
                var empty = worksetNames.Values.Where(n => !counts.ContainsKey(n)).ToList();
                Add("Structure", "Worksets", "INFO", worksetNames.Count + " user worksets; empty: " + (empty.Count == 0 ? "none" : string.Join(", ", empty)) + ". Largest: " + string.Join("; ", counts.OrderByDescending(kv => kv.Value).Take(8).Select(kv => kv.Key + " " + kv.Value)));
            }
            var options = new FilteredElementCollector(doc).OfClass(typeof(DesignOption)).Cast<DesignOption>().ToList();
            Add("Structure", "Design options", options.Count == 0 ? "INFO" : "PASS",
                options.Count == 0 ? "none in the model" : string.Join("; ", options.Select(o => o.Name + (o.IsPrimary ? " (primary)" : "") + ": " + pieces.Count(p => p.Option == o.Name) + " Sportify elements")));

            // ---- project setup
            var pi = doc.ProjectInformation;
            var missing = new List<string>();
            if (string.IsNullOrWhiteSpace(pi.Name)) missing.Add("name");
            if (string.IsNullOrWhiteSpace(pi.Number)) missing.Add("number");
            if (string.IsNullOrWhiteSpace(pi.Address)) missing.Add("address");
            if (string.IsNullOrWhiteSpace(pi.ClientName)) missing.Add("client");
            if (string.IsNullOrWhiteSpace(pi.Author)) missing.Add("author");
            Add("Setup", "Project information", missing.Count == 0 ? "PASS" : "WARN", $"name '{pi.Name}', number '{pi.Number}', client '{pi.ClientName}', author '{pi.Author}'" + (missing.Count == 0 ? "" : "; empty: " + string.Join(", ", missing)));
            try
            {
                var site = doc.SiteLocation;
                var lat = site.Latitude * 180 / Math.PI; var lon = site.Longitude * 180 / Math.PI;
                var boston = Math.Abs(lat - 42.387) < 0.05 && Math.Abs(lon + 71.242) < 0.05;
                var inEurope = lat > 35 && lat < 72 && lon > -12 && lon < 40;
                string layoutSite = layout?.SiteText ?? "";
                Add("Setup", "Project location is the real site (sun, wind and energy analysis depend on it)", boston || !inEurope ? "FAIL" : "PASS",
                    $"lat {lat:0.0000}, lon {lon:0.0000}, '{site.PlaceName}', UTC{site.TimeZone:+0;-0}" + (boston ? " : the Revit default (Boston)" : "") + (layoutSite.Length > 0 ? "; the layout says " + layoutSite : ""));
                var pos = doc.ActiveProjectLocation.GetProjectPosition(XYZ.Zero);
                Add("Setup", "True north", "INFO", $"angle to true north {pos.Angle * 180 / Math.PI:0.##} deg; project position E {pos.EastWest * FeetToM:0.###} N {pos.NorthSouth * FeetToM:0.###} elevation {pos.Elevation * FeetToM:0.###} m");
            }
            catch (Exception ex) { Add("Setup", "Project location", "WARN", "could not be read: " + ex.Message); }
            try { Add("Setup", "Length unit", "INFO", doc.GetUnits().GetFormatOptions(SpecTypeId.Length).GetUnitTypeId().TypeId); } catch (Exception) { /* not reported */ }

            // ---- warnings
            try
            {
                var warnings = doc.GetWarnings();
                var sportifyIds = new HashSet<long>(pieces.Select(p => p.E.Id.Value));
                var mine = warnings.Where(w => w.GetFailingElements().Concat(w.GetAdditionalElements()).Any(id => sportifyIds.Contains(id.Value))).ToList();
                var groups = warnings.GroupBy(w => w.GetDescriptionText()).OrderByDescending(g => g.Count()).ToList();
                try
                {
                    var wcsv = new StringBuilder("description;id;family;type;workset;level;minz\n");
                    foreach (var w in warnings)
                        foreach (var id in w.GetFailingElements().Concat(w.GetAdditionalElements()))
                        {
                            var e = doc.GetElement(id); if (e == null) continue;
                            var t = doc.GetElement(e.GetTypeId());
                            var bb = e.get_BoundingBox(null);
                            wcsv.AppendLine(string.Join(";", new[] { Trim(w.GetDescriptionText(), 60), id.Value.ToString(), (e as FamilyInstance)?.Symbol?.FamilyName ?? e.GetType().Name, t?.Name ?? "", doc.IsWorkshared && worksetNames.TryGetValue(e.WorksetId.IntegerValue, out var wn) ? wn : "", e.LevelId != ElementId.InvalidElementId ? doc.GetElement(e.LevelId)?.Name ?? "" : "", bb == null ? "" : (bb.Min.Z * FeetToM).ToString("0.00", CultureInfo.InvariantCulture) }));
                        }
                    File.WriteAllText(Path.Combine(outDir, "bim-warnings.csv"), wcsv.ToString());
                }
                catch (Exception) { /* the dump is a convenience */ }
                Add("Model health", "Revit warnings", warnings.Count == 0 ? "PASS" : warnings.Count < 25 ? "WARN" : "FAIL",
                    warnings.Count + " warnings, " + mine.Count + " involve Sportify elements", groups.Take(10).Select(g => g.Count() + " x " + Trim(g.Key, 150)));
            }
            catch (Exception ex) { Add("Model health", "Revit warnings", "WARN", "could not be read: " + ex.Message); }

            // ---- placement
            var roof = layout == null ? null : layout.RoofWorld();
            if (roof != null)
            {
                var outside = new List<string>(); var floating = new List<string>();
                var roofId = Environment.GetEnvironmentVariable("SPORTIFY_INSPECT_ROOFID") ?? "";
                var otherRoofs = instances.Where(p => p.RoofId.Length > 0 && roofId.Length > 0 && p.RoofId != roofId).GroupBy(p => p.RoofId).Select(g => "roof " + g.Key + ": " + g.Count() + " pieces").ToList();
                Add("Placement", "Roofs the pieces belong to (Sportify_RoofId)", "INFO", string.Join("; ", instances.GroupBy(p => p.RoofId.Length == 0 ? "(none)" : p.RoofId).Select(g => g.Key + " " + g.Count())) + "; the checks below are for roof " + roofId);
                foreach (var p in instances.Where(p => p.Min != null && p.Max != null && (roofId.Length == 0 || p.RoofId == roofId)))
                {
                    var cx = (p.Min![0] + p.Max![0]) / 2; var cy = (p.Min[1] + p.Max[1]) / 2;
                    if (!roof.Contains(cx, cy, 0.75)) outside.Add(p.Label + $" centre ({cx:0.0}, {cy:0.0})");
                    if (p.SportifyCategory != "kinetics" && Math.Abs(p.Min[2] - roof.Z) > 0.35) floating.Add(p.Label + $" base z {p.Min[2]:0.00} (roof {roof.Z:0.00})");
                }
                Add("Placement", "Every piece stands inside the roof outline", outside.Count == 0 ? "PASS" : "FAIL", outside.Count + " outside (roof outline from the layout, 0.75 m tolerance)", outside);
                Add("Placement", "Every piece stands on the roof (base within 35 cm of the roof top)", floating.Count == 0 ? "PASS" : "WARN", floating.Count + " do not (roof top z " + roof.Z.ToString("0.000", CultureInfo.InvariantCulture) + ")", floating);
            }
            var dup = new List<string>(); var overlap = new List<string>();
            var movable = instances.Where(p => p.Min != null && p.Max != null).ToList();
            for (var i = 0; i < movable.Count; i++)
                for (var j = i + 1; j < movable.Count; j++)
                {
                    var a = movable[i]; var b = movable[j];
                    var ox = Math.Min(a.Max![0], b.Max![0]) - Math.Max(a.Min![0], b.Min![0]);
                    var oy = Math.Min(a.Max[1], b.Max[1]) - Math.Max(a.Min[1], b.Min[1]);
                    if (ox <= 0.02 || oy <= 0.02) continue;
                    var oz = Math.Min(a.Max[2], b.Max[2]) - Math.Max(a.Min[2], b.Min[2]);
                    if (oz <= 0.02) continue;
                    var sameSpot = Math.Abs((a.Min[0] + a.Max[0]) / 2 - (b.Min[0] + b.Max[0]) / 2) < 0.05 && Math.Abs((a.Min[1] + a.Max[1]) / 2 - (b.Min[1] + b.Max[1]) / 2) < 0.05;
                    if (sameSpot && a.Family == b.Family && a.Type == b.Type) dup.Add(a.Label + " twice (" + b.Label + ")");
                    else if (ox * oy > 0.25) overlap.Add(a.Label + " overlaps " + b.Label + $" by {ox * oy:0.0} m2");
                }
            Add("Placement", "No duplicate elements (same type, same spot)", dup.Count == 0 ? "PASS" : "FAIL", dup.Count + " duplicate(s)", dup);
            Add("Placement", "No pieces overlapping each other", overlap.Count == 0 ? "PASS" : "WARN", overlap.Count + " overlapping pair(s) of 3D boxes (more than 0.25 m2 in plan)", overlap);

            // ---- the layout the web app last imported: every piece and zone there is in the model
            if (layout != null && roof != null)
            {
                var missingPieces = new List<string>(); var seen = new List<string>();
                foreach (var pl in layout.Placements)
                {
                    var wx = roof.Ox + pl.Cx; var wy = roof.Oy + roof.Width - pl.Cy;
                    var near = movable.Where(p => wx >= p.Min![0] - 1 && wx <= p.Max![0] + 1 && wy >= p.Min[1] - 1 && wy <= p.Max[1] + 1).ToList();
                    seen.Add($"'{pl.Label}' ({pl.Category}) at ({pl.Cx:0.0}, {pl.Cy:0.0}) -> " + (near.Count == 0 ? "NOTHING" : string.Join(" | ", near.Take(3).Select(n => n.Label))));
                    if (near.Count == 0) missingPieces.Add(pl.Label + " at (" + pl.Cx.ToString("0.0", CultureInfo.InvariantCulture) + ", " + pl.Cy.ToString("0.0", CultureInfo.InvariantCulture) + ")");
                }
                Add("Layout parity", $"Every piece of the last imported layout ({layout.Placements.Count}) is in the model", missingPieces.Count == 0 ? "PASS" : "FAIL", missingPieces.Count + " missing", missingPieces);
                Add("Layout parity", "Where each layout piece landed", "INFO", string.Join(" // ", seen.Count + " pieces"), seen);
                var zoneFloors = floors.Count;
                Add("Layout parity", $"Garden zones ({layout.ZoneCount}) have a build-up floor each", zoneFloors >= layout.ZoneCount ? "PASS" : "FAIL", zoneFloors + " Sportify floors in the model for " + layout.ZoneCount + " zones");
            }

            // ---- planters
            var planters = pieces.Where(p => IsPlanter(p)).ToList();
            var anyPlanting = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Planting).WhereElementIsNotElementType().GetElementCount();
            Add("Planters", "Planter-like elements (family or type says planter / Pflanz, or Planting category)", planters.Count > 0 ? "PASS" : "FAIL",
                planters.Count + " found, Planting-category elements in the whole model: " + anyPlanting,
                planters.Select(p => p.Label + $" | {p.Category} | level {p.Level} | workset {p.Workset} | option '{p.Option}' | box {Box(p)} | base z {(p.Min == null ? "?" : p.Min[2].ToString("0.00", CultureInfo.InvariantCulture))}"));
            if (layout != null)
            {
                var expected = layout.Placements.Where(p => p.Category == "gardenBlock" || p.Label.Contains("Planter", StringComparison.OrdinalIgnoreCase)).ToList();
                var missingPl = expected.Where(pl =>
                {
                    var roofW = layout.RoofWorld(); if (roofW == null) return false;
                    var wx = roofW.Ox + pl.Cx; var wy = roofW.Oy + roofW.Width - pl.Cy;
                    return !planters.Any(p => p.Min != null && wx >= p.Min[0] - 1 && wx <= p.Max![0] + 1 && wy >= p.Min[1] - 1 && wy <= p.Max[1] + 1);
                }).Select(pl => pl.Label + $" at ({pl.Cx:0.0}, {pl.Cy:0.0})").ToList();
                Add("Planters", $"The layout's planters ({expected.Count}) are in the model", missingPl.Count == 0 ? "PASS" : "FAIL", missingPl.Count + " missing", missingPl);
            }

            // ---- documentation
            var views = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Where(v => !v.IsTemplate).ToList();
            var sheets = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>().ToList();
            var placed = new HashSet<long>();
            foreach (var s in sheets) foreach (var id in s.GetAllPlacedViews()) placed.Add(id.Value);
            var unplaced = views.Where(v => v is not ViewSheet && v is not ViewSchedule && v.ViewType is ViewType.FloorPlan or ViewType.CeilingPlan or ViewType.ThreeD or ViewType.Section or ViewType.Elevation or ViewType.AreaPlan or ViewType.EngineeringPlan or ViewType.DraftingView)
                .Where(v => !placed.Contains(v.Id.Value)).ToList();
            Add("Documentation", "Sheets", sheets.Count > 0 ? "PASS" : "FAIL", sheets.Count + " sheets", sheets.OrderBy(s => s.SheetNumber).Select(s => s.SheetNumber + "  " + s.Name + "  (" + s.GetAllPlacedViews().Count + " views)"));
            var emptySheets = sheets.Where(s => s.GetAllPlacedViews().Count == 0 && new FilteredElementCollector(doc, s.Id).OfClass(typeof(ScheduleSheetInstance)).GetElementCount() == 0).ToList();
            Add("Documentation", "No empty sheets", emptySheets.Count == 0 ? "PASS" : "WARN", emptySheets.Count + " empty", emptySheets.Select(s => s.SheetNumber + " " + s.Name));
            Add("Documentation", "Views not on any sheet", unplaced.Count == 0 ? "PASS" : "INFO", unplaced.Count + " of " + views.Count + " views", unplaced.Select(v => v.ViewType + " '" + v.Name + "'"));
            var oddViews = views.Where(v => v.Name.Contains("Copy", StringComparison.OrdinalIgnoreCase) || v.Name.Contains("Kopie", StringComparison.OrdinalIgnoreCase) || v.Name.StartsWith("{", StringComparison.Ordinal) || v.Name.Contains("Sportify inspect", StringComparison.Ordinal)).ToList();
            Add("Documentation", "View names are tidy (no 'Copy', no default {3D})", oddViews.Count == 0 ? "PASS" : "WARN", oddViews.Count + " untidy", oddViews.Select(v => v.ViewType + " '" + v.Name + "'"));
            var noTemplate = views.Where(v => v is not ViewSheet && v is not ViewSchedule && v.ViewType is ViewType.FloorPlan or ViewType.ThreeD or ViewType.Section or ViewType.Elevation && v.ViewTemplateId == ElementId.InvalidElementId && placed.Contains(v.Id.Value)).ToList();
            Add("Documentation", "Placed views use a view template", noTemplate.Count == 0 ? "PASS" : "INFO", noTemplate.Count + " placed views without one", noTemplate.Select(v => v.Name));
            var schedules = new FilteredElementCollector(doc).OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>().Where(s => !s.IsTemplate && !s.Name.StartsWith("<", StringComparison.Ordinal)).ToList();
            var scheduleRows = schedules.Select(s =>
            {
                int rows = -1; try { rows = s.GetTableData().GetSectionData(SectionType.Body).NumberOfRows; } catch (Exception) { /* not readable */ }
                return (s.Name, rows);
            }).ToList();
            Add("Documentation", "Schedules (take-off lists)", scheduleRows.Count > 0 ? "PASS" : "FAIL", scheduleRows.Count + " schedules", scheduleRows.Select(s => s.Name + " : " + (s.rows < 0 ? "?" : s.rows + " body rows")));
            var emptySchedules = scheduleRows.Where(s => s.rows == 0).Select(s => s.Name).ToList();
            Add("Documentation", "No empty schedules", emptySchedules.Count == 0 ? "PASS" : "WARN", emptySchedules.Count + " empty", emptySchedules);
            var tags = new FilteredElementCollector(doc).OfClass(typeof(IndependentTag)).Cast<IndependentTag>().ToList();
            var tagged = new HashSet<long>();
            foreach (var t in tags) { try { foreach (var id in t.GetTaggedLocalElementIds()) tagged.Add(id.Value); } catch (Exception) { /* a tag of a linked element */ } }
            var untagged = instances.Where(p => !tagged.Contains(p.E.Id.Value)).ToList();
            Add("Documentation", "Sportify pieces are tagged", untagged.Count == 0 ? "PASS" : untagged.Count < instances.Count ? "INFO" : "WARN", tags.Count + " tags in the model; " + untagged.Count + " of " + instances.Count + " Sportify instances untagged", untagged.Select(p => p.Label));
            var rooms = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Rooms).GetElementCount();
            var areas = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Areas).GetElementCount();
            Add("Documentation", "Rooms / areas", "INFO", rooms + " rooms, " + areas + " areas (the DIN 277 list works from the Sportify_DIN277 parameter of the floors)");
            var grids = new FilteredElementCollector(doc).OfClass(typeof(Grid)).GetElementCount();
            Add("Documentation", "Grids and levels for coordination", grids > 0 && levels.Count > 1 ? "PASS" : "WARN", grids + " grids, " + levels.Count + " levels");
        }

        // ------------------------------------------------------------------------------------------------------------------------------------------------ helpers

        static Piece Describe(Document doc, Element e, string sc, string workset, Dictionary<long, string> phases, bool isFloor)
        {
            var type = doc.GetElement(e.GetTypeId());
            var fi = e as FamilyInstance;
            var p = new Piece
            {
                E = e, Kind = isFloor ? "floor" : fi != null ? "instance" : "other", Category = e.Category?.Name ?? "", SportifyCategory = sc,
                Family = fi?.Symbol?.FamilyName ?? (e as Floor != null ? "Floor" : e.Name), Type = type?.Name ?? "", Workset = workset,
                InPlace = fi?.Symbol?.Family?.IsInPlace == true, DirectShape = e is DirectShape,
                Phase = phases.TryGetValue(e.CreatedPhaseId.Value, out var ph) ? ph : "", Option = e.DesignOption?.Name ?? "",
                RoofId = Str(e, "Sportify_RoofId"), Kg = Str(e, "Sportify_KG"), Din277 = Str(e, "Sportify_DIN277"), Mark = e.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString() ?? "",
            };
            if (isFloor) p.Family = "Floor";
            try
            {
                p.PlacementType = fi?.Symbol?.Family?.FamilyPlacementType.ToString() ?? "";
                p.LevelParam = e.get_Parameter(BuiltInParameter.FAMILY_LEVEL_PARAM)?.AsValueString() ?? "";
                p.ScheduleLevel = e.get_Parameter(BuiltInParameter.INSTANCE_SCHEDULE_ONLY_LEVEL_PARAM)?.AsValueString() ?? "";
            }
            catch (Exception) { /* not reported */ }
            try { if (e.LevelId != ElementId.InvalidElementId) p.Level = doc.GetElement(e.LevelId)?.Name ?? ""; else if (e.get_Parameter(BuiltInParameter.INSTANCE_SCHEDULE_ONLY_LEVEL_PARAM)?.AsElementId() is { } sl && sl != ElementId.InvalidElementId) p.Level = (doc.GetElement(sl)?.Name ?? "") + " (Schedule Level)"; } catch (Exception) { /* none */ }
            try { var o = e.get_Parameter(BuiltInParameter.INSTANCE_ELEVATION_PARAM) ?? e.get_Parameter(BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM); if (o != null) p.OffsetM = o.AsDouble() * FeetToM; } catch (Exception) { /* none */ }
            try
            {
                p.IfcAs = e.get_Parameter(BuiltInParameter.IFC_EXPORT_ELEMENT_AS)?.AsString() ?? "";
                if (p.IfcAs.Length == 0) p.IfcAs = type?.get_Parameter(BuiltInParameter.IFC_EXPORT_ELEMENT_TYPE_AS)?.AsString() ?? "";
            }
            catch (Exception) { /* not available */ }
            try
            {
                var bb = e.get_BoundingBox(null);
                if (bb != null) { p.Min = new[] { bb.Min.X * FeetToM, bb.Min.Y * FeetToM, bb.Min.Z * FeetToM }; p.Max = new[] { bb.Max.X * FeetToM, bb.Max.Y * FeetToM, bb.Max.Z * FeetToM }; }
            }
            catch (Exception) { /* none */ }
            try
            {
                var g = e.get_Geometry(new Options { DetailLevel = ViewDetailLevel.Fine, ComputeReferences = false });
                if (g != null) Walk(g, p);
            }
            catch (Exception) { /* none */ }
            return p;
        }

        static void Walk(GeometryElement g, Piece p)
        {
            foreach (var go in g)
            {
                if (go is GeometryInstance gi) { Walk(gi.GetInstanceGeometry(), p); continue; }
                if (go is Solid s && s.Volume > 1e-9 && s.Faces.Size > 0)
                {
                    p.HasSolid = true; p.Volume += s.Volume;
                    foreach (Face f in s.Faces) if (f.MaterialElementId != ElementId.InvalidElementId) { p.HasMaterial = true; break; }
                }
            }
        }

        static string Str(Element e, string name)
        {
            try
            {
                var prm = e.LookupParameter(name);
                if (prm == null || !prm.HasValue) { var t = e.Document.GetElement(e.GetTypeId()); prm = t?.LookupParameter(name); if (prm == null || !prm.HasValue) return ""; }
                return prm.StorageType == StorageType.String ? prm.AsString() ?? "" : prm.AsValueString() ?? "";
            }
            catch (Exception) { return ""; }
        }

        static bool IsPlanter(Piece p) =>
            p.Category == "Planting" || p.SportifyCategory.Equals("gardenBlock", StringComparison.OrdinalIgnoreCase)
            || p.Family.Contains("planter", StringComparison.OrdinalIgnoreCase) || p.Type.Contains("planter", StringComparison.OrdinalIgnoreCase)
            || p.Family.Contains("pflanz", StringComparison.OrdinalIgnoreCase) || p.Type.Contains("pflanz", StringComparison.OrdinalIgnoreCase);

        static bool LooksGeneric(string name)
        {
            var n = name.Trim();
            return n.Length == 0 || n.Equals("Default", StringComparison.OrdinalIgnoreCase) || n.StartsWith("Familie", StringComparison.OrdinalIgnoreCase) || n.StartsWith("Family", StringComparison.OrdinalIgnoreCase)
                   || n.StartsWith("Typ ", StringComparison.OrdinalIgnoreCase) || n.StartsWith("Type ", StringComparison.OrdinalIgnoreCase) || n.StartsWith("Generic Model", StringComparison.OrdinalIgnoreCase)
                   || n.StartsWith("Standard", StringComparison.OrdinalIgnoreCase) && n.Length < 10;
        }

        static string N(double[]? a, int i) => a == null ? "" : a[i].ToString("0.000", CultureInfo.InvariantCulture);
        static string Box(Piece p) => p.Min == null || p.Max == null ? "none" : $"{p.Max[0] - p.Min[0]:0.0} x {p.Max[1] - p.Min[1]:0.0} x {p.Max[2] - p.Min[2]:0.0} m";
        static string Trim(string s, int n) => s.Length <= n ? s : s.Substring(0, n) + "...";

        // ------------------------------------------------------------------------------------------------------------------------------------------------ the layout to compare with

        sealed record Pl(string Label, string Category, double Cx, double Cy);

        sealed class RoofWorld
        {
            public double Ox, Oy, Z, Width; public List<(double X, double Y)> Poly = new();
            public bool Contains(double x, double y, double tolerance)
            {
                var inside = false;
                for (int i = 0, j = Poly.Count - 1; i < Poly.Count; j = i++)
                {
                    var (xi, yi) = Poly[i]; var (xj, yj) = Poly[j];
                    if ((yi > y) != (yj > y) && x < (xj - xi) * (y - yi) / (yj - yi) + xi) inside = !inside;
                }
                if (inside) return true;
                for (int i = 0, j = Poly.Count - 1; i < Poly.Count; j = i++) if (Dist(x, y, Poly[j], Poly[i]) <= tolerance) return true;
                return false;
            }
            static double Dist(double px, double py, (double X, double Y) a, (double X, double Y) b)
            {
                var dx = b.X - a.X; var dy = b.Y - a.Y; var l2 = dx * dx + dy * dy;
                var t = l2 == 0 ? 0 : Math.Max(0, Math.Min(1, ((px - a.X) * dx + (py - a.Y) * dy) / l2));
                return Math.Sqrt(Math.Pow(px - (a.X + t * dx), 2) + Math.Pow(py - (a.Y + t * dy), 2));
            }
        }

        sealed class LayoutInfo
        {
            public List<Pl> Placements = new(); public int ZoneCount; public string SiteText = "";
            public double Ox, Oy, Z, Rot; public List<(double X, double Y)> Poly = new();
            public double Width;
            public RoofWorld? RoofWorld() => Rot != 0 || Poly.Count < 3 ? null
                : new RoofWorld { Ox = Ox, Oy = Oy, Width = Width, Z = Z, Poly = Poly.Select(p => (Ox + p.X, Oy + p.Y)).ToList() };
        }

        static LayoutInfo? ReadLayout()
        {
            var path = Environment.GetEnvironmentVariable("SPORTIFY_INSPECT_LAYOUT");
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
            try
            {
                using var d = JsonDocument.Parse(File.ReadAllText(path).TrimStart('﻿'));
                var r = d.RootElement; var info = new LayoutInfo();
                double N(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
                if (r.TryGetProperty("roof_context", out var rc))
                {
                    info.Ox = N(rc, "world_origin_x_m"); info.Oy = N(rc, "world_origin_y_m"); info.Z = N(rc, "world_origin_z_m"); info.Rot = N(rc, "rotation_deg"); info.Width = N(rc, "width_m");
                    if (rc.TryGetProperty("source_boundary_polygon", out var poly)) foreach (var pt in poly.EnumerateArray()) info.Poly.Add((N(pt, "x_m"), N(pt, "y_m")));
                }
                if (r.TryGetProperty("site_location", out var sl) && sl.ValueKind == JsonValueKind.Object)
                    info.SiteText = string.Join(", ", sl.EnumerateObject().Where(p => p.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number).Take(6).Select(p => p.Name + "=" + p.Value));
                if (r.TryGetProperty("zones", out var zs) && zs.ValueKind == JsonValueKind.Array) info.ZoneCount = zs.GetArrayLength();
                if (r.TryGetProperty("placements", out var ps) && ps.ValueKind == JsonValueKind.Array)
                    foreach (var p in ps.EnumerateArray())
                    {
                        var ip = p.TryGetProperty("insertion_point", out var i) ? i : default;
                        info.Placements.Add(new Pl(p.TryGetProperty("label", out var l) ? l.GetString() ?? "" : "", p.TryGetProperty("category", out var c) ? c.GetString() ?? "" : "", ip.ValueKind == JsonValueKind.Object ? N(ip, "center_x_m") : 0, ip.ValueKind == JsonValueKind.Object ? N(ip, "center_y_m") : 0));
                    }
                return info;
            }
            catch (Exception) { return null; }
        }

        // ------------------------------------------------------------------------------------------------------------------------------------------------ the text

        static string Text()
        {
            var sb = new StringBuilder();
            var counts = Checks.GroupBy(c => c.Status).ToDictionary(g => g.Key, g => g.Count());
            sb.AppendLine("BIM AUDIT  (PASS " + counts.GetValueOrDefault("PASS") + ", WARN " + counts.GetValueOrDefault("WARN") + ", FAIL " + counts.GetValueOrDefault("FAIL") + ", INFO " + counts.GetValueOrDefault("INFO") + ")");
            foreach (var g in Checks.GroupBy(c => c.Group))
            {
                sb.AppendLine();
                sb.AppendLine("== " + g.Key.ToUpperInvariant());
                foreach (var c in g)
                {
                    sb.AppendLine($"[{c.Status}] {c.Name}: {c.Detail}");
                    foreach (var s in c.Samples) sb.AppendLine("        - " + s);
                }
            }
            return sb.ToString();
        }
    }
}
