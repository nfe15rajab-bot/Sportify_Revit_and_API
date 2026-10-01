using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;

namespace Sportify.ModelInspect
{
    /// <summary>
    /// Reads one Revit project and writes down what is in it, for the day the Goldbeck IFC model is used with Sportify: which categories and IFC classes its elements have, where its roof
    /// is and what Sportify's Push to Sportify could make of it, how it is worked on (worksets, phases, levels), and where it stands against the origin and true north.
    /// Off unless SPORTIFY_INSPECT_FILE (the project) and SPORTIFY_INSPECT_OUT (a folder for the report) are both set; it works on a COPY of the project, opened detached from its central
    /// model when it is workshared, and never saves it. Writes inspection.json, inspection.txt and two pictures (a 3D view and a view from above), then exits Revit
    /// (SPORTIFY_INSPECT_KEEP=1 leaves it open).
    /// </summary>
    public class InspectApp : IExternalApplication
    {
        const double FeetToM = 0.3048;
        static readonly Regex RoofWords = new(@"roof|dach|slab|decke|platte|deck|terras|attika|parapet|cover", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        static readonly Regex IfcExportWords = new(@"export.*ifc|ifc.*export|predefined|ifctype|objecttype|ifcclass|ifcentity", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public Result OnStartup(UIControlledApplication application)
        {
            var file = Environment.GetEnvironmentVariable("SPORTIFY_INSPECT_FILE");
            var outDir = Environment.GetEnvironmentVariable("SPORTIFY_INSPECT_OUT");
            if (string.IsNullOrWhiteSpace(file) || string.IsNullOrWhiteSpace(outDir)) return Result.Succeeded;

            void OnIdle(object? sender, IdlingEventArgs e)
            {
                application.Idling -= OnIdle;
                if (sender is not UIApplication uiApp) return;
                var report = new Dictionary<string, object?>();
                var failures = new List<string>();
                report["failures"] = failures;
                try { Directory.CreateDirectory(outDir!); Run(uiApp, file!, outDir!, report, failures); }
                catch (Exception ex) { failures.Add("the inspection itself failed: " + ex); }
                try
                {
                    File.WriteAllText(Path.Combine(outDir!, "inspection.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
                    File.WriteAllText(Path.Combine(outDir!, "inspection.txt"), Summary(report));
                }
                catch (Exception ex) { File.WriteAllText(Path.Combine(outDir!, "inspection-error.txt"), ex.ToString()); }
                if (Environment.GetEnvironmentVariable("SPORTIFY_INSPECT_KEEP") != "1")
                {
                    try { uiApp.PostCommand(RevitCommandId.LookupPostableCommandId(PostableCommand.ExitRevit)); }
                    catch (Exception) { Process.GetCurrentProcess().Kill(); }
                }
            }
            application.Idling += OnIdle;
            return Result.Succeeded;
        }

        public Result OnShutdown(UIControlledApplication application) => Result.Succeeded;

        // ------------------------------------------------------------------------------------------------------------------------------------------------ the run

        static void Run(UIApplication uiApp, string file, string outDir, Dictionary<string, object?> report, List<string> failures)
        {
            var clock = Stopwatch.StartNew();
            report["file"] = file;
            report["revit"] = uiApp.Application.VersionName + " " + uiApp.Application.VersionBuild;
            if (!File.Exists(file)) { failures.Add("the file does not exist"); return; }

            bool workshared = false;
            try
            {
                var info = BasicFileInfo.Extract(file);
                workshared = info.IsWorkshared;
                report["file_info"] = new Dictionary<string, object?>
                {
                    ["format"] = info.Format, ["is_workshared"] = info.IsWorkshared, ["is_central"] = info.IsCentral, ["is_local"] = info.IsLocal, ["central_path"] = info.CentralPath,
                    ["username"] = info.Username, ["saved_in_later_version"] = info.IsSavedInLaterVersion, ["size_mb"] = Math.Round(new FileInfo(file).Length / 1048576.0, 1),
                };
            }
            catch (Exception ex) { failures.Add("the file's own information could not be read: " + ex.Message); }

            // work on a copy: nothing here ever writes to the person's project
            var copy = Path.Combine(outDir, "model-copy.rvt");
            File.Copy(file, copy, true);
            var options = new OpenOptions { Audit = false };
            if (workshared)
            {
                options.DetachFromCentralOption = DetachFromCentralOption.DetachAndPreserveWorksets;
                options.SetOpenWorksetsConfiguration(new WorksetConfiguration(WorksetConfigurationOption.OpenAllWorksets));
            }
            Document doc;
            try { doc = uiApp.Application.OpenDocumentFile(ModelPathUtils.ConvertUserVisiblePathToModelPath(copy), options); }
            catch (Exception ex) { failures.Add("the copy could not be opened: " + ex.Message); return; }
            report["opened_in_seconds"] = Math.Round(clock.Elapsed.TotalSeconds, 1);

            try
            {
                Inspect(doc, outDir, report, failures);
                BimAudit.Run(doc, outDir, report, failures);
                Pictures(doc, outDir, report, failures);
            }
            finally
            {
                try { doc.Close(false); } catch (Exception ex) { failures.Add("the copy could not be closed: " + ex.Message); }
                report["total_seconds"] = Math.Round(clock.Elapsed.TotalSeconds, 1);
            }
        }

        // ------------------------------------------------------------------------------------------------------------------------------------------------ what is in it

        static void Inspect(Document doc, string outDir, Dictionary<string, object?> report, List<string> failures)
        {
            report["title"] = doc.Title;
            report["is_workshared"] = doc.IsWorkshared;
            try { report["units_length"] = doc.GetUnits().GetFormatOptions(SpecTypeId.Length).GetUnitTypeId().TypeId; } catch (Exception) { /* not reported */ }

            var pi = doc.ProjectInformation;
            report["project"] = new Dictionary<string, object?> { ["name"] = pi.Name, ["number"] = pi.Number, ["address"] = pi.Address, ["client"] = pi.ClientName, ["building"] = pi.BuildingName, ["author"] = pi.Author };

            // where the model stands: site, the project base point, the survey point, the angle to true north
            var place = new Dictionary<string, object?>();
            try
            {
                var site = doc.SiteLocation;
                place["latitude_deg"] = Math.Round(site.Latitude * 180 / Math.PI, 5);
                place["longitude_deg"] = Math.Round(site.Longitude * 180 / Math.PI, 5);
                place["place_name"] = site.PlaceName;
                place["time_zone_h"] = site.TimeZone;
                var pos = doc.ActiveProjectLocation.GetProjectPosition(XYZ.Zero);
                place["active_location"] = doc.ActiveProjectLocation.Name;
                place["angle_to_true_north_deg"] = Math.Round(pos.Angle * 180 / Math.PI, 4);
                place["shared_east_m"] = Math.Round(pos.EastWest * FeetToM, 3);
                place["shared_north_m"] = Math.Round(pos.NorthSouth * FeetToM, 3);
                place["shared_elevation_m"] = Math.Round(pos.Elevation * FeetToM, 3);
                var pbp = BasePoint.GetProjectBasePoint(doc);
                var sp = BasePoint.GetSurveyPoint(doc);
                place["project_base_point_m"] = Pt(pbp.Position);
                place["survey_point_m"] = Pt(sp.Position);
            }
            catch (Exception ex) { failures.Add("the site could not be read: " + ex.Message); }
            report["place"] = place;

            report["levels"] = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation)
                .Select(l => new Dictionary<string, object?> { ["name"] = l.Name, ["elevation_m"] = Math.Round(l.Elevation * FeetToM, 3) }).ToList();

            var phases = new List<(string Name, long Id)>();
            foreach (Phase p in doc.Phases) phases.Add((p.Name, p.Id.Value));
            var worksets = new Dictionary<int, string>();
            if (doc.IsWorkshared)
                foreach (var w in new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset).ToWorksets()) worksets[w.Id.IntegerValue] = w.Name;

            // every model element: counted by category and class, and by workset and phase; the extents of the whole model
            var all = new FilteredElementCollector(doc).WhereElementIsNotElementType().ToElements();
            var byCategory = new Dictionary<string, CategoryInfo>();
            var perWorkset = new Dictionary<string, int>();
            var perPhase = new Dictionary<string, int>();
            var extent = new Extent();
            var noCategory = 0; var annotation = 0;
            var directShapes = 0;
            foreach (var e in all)
            {
                var cat = e.Category;
                if (cat == null) { noCategory++; continue; }
                if (cat.CategoryType != CategoryType.Model) { annotation++; continue; }
                if (!byCategory.TryGetValue(cat.Name, out var ci)) byCategory[cat.Name] = ci = new CategoryInfo { Name = cat.Name, BuiltIn = cat.BuiltInCategory.ToString() };
                ci.Count++;
                var cls = e.GetType().Name;
                ci.Classes[cls] = ci.Classes.GetValueOrDefault(cls) + 1;
                if (e is DirectShape) directShapes++;
                if (doc.IsWorkshared && worksets.TryGetValue(e.WorksetId.IntegerValue, out var wn)) perWorkset[wn] = perWorkset.GetValueOrDefault(wn) + 1;
                var ph = phases.FirstOrDefault(p => p.Id == e.CreatedPhaseId.Value).Name;
                if (ph != null) perPhase[ph] = perPhase.GetValueOrDefault(ph) + 1;
                var bb = e.get_BoundingBox(null);
                if (bb != null) extent.Add(bb);
            }
            report["element_count"] = all.Count;
            report["elements_without_category"] = noCategory;
            report["annotation_elements"] = annotation;
            report["direct_shapes"] = directShapes;
            report["categories"] = byCategory.Values.OrderByDescending(c => c.Count).Select(c => new Dictionary<string, object?> { ["category"] = c.Name, ["built_in"] = c.BuiltIn, ["count"] = c.Count, ["classes"] = c.Classes }).ToList();
            report["worksets"] = doc.IsWorkshared ? worksets.Values.Select(n => new Dictionary<string, object?> { ["name"] = n, ["elements"] = perWorkset.GetValueOrDefault(n) }).ToList() : null;
            report["phases"] = phases.Select(p => new Dictionary<string, object?> { ["name"] = p.Name, ["elements_created"] = perPhase.GetValueOrDefault(p.Name) }).ToList();
            report["extent_m"] = extent.ToReport();

            // links and imports: an IFC can arrive as a link instead of as elements of this project
            report["links"] = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>().Select(l =>
            {
                var d = l.GetLinkDocument();
                return new Dictionary<string, object?> { ["name"] = l.Name, ["loaded"] = d != null, ["title"] = d?.Title };
            }).ToList();
            report["imports"] = new FilteredElementCollector(doc).OfClass(typeof(ImportInstance)).Cast<ImportInstance>().Select(i => new Dictionary<string, object?> { ["name"] = i.Name, ["category"] = i.Category?.Name, ["linked"] = i.IsLinked }).ToList();

            IfcParameters(all, report);
            RoofCandidates(doc, all, worksets, phases, report, failures);
        }

        sealed class CategoryInfo
        {
            public string Name = "", BuiltIn = "";
            public int Count;
            public Dictionary<string, int> Classes = new();
        }

        sealed class Extent
        {
            double _x0 = double.MaxValue, _y0 = double.MaxValue, _z0 = double.MaxValue, _x1 = double.MinValue, _y1 = double.MinValue, _z1 = double.MinValue;
            int _n;
            public void Add(BoundingBoxXYZ bb)
            {
                _n++;
                _x0 = Math.Min(_x0, bb.Min.X); _y0 = Math.Min(_y0, bb.Min.Y); _z0 = Math.Min(_z0, bb.Min.Z);
                _x1 = Math.Max(_x1, bb.Max.X); _y1 = Math.Max(_y1, bb.Max.Y); _z1 = Math.Max(_z1, bb.Max.Z);
            }
            public object? ToReport() => _n == 0 ? null : new Dictionary<string, object?>
            {
                ["elements_with_a_box"] = _n,
                ["min"] = new[] { R(_x0), R(_y0), R(_z0) }, ["max"] = new[] { R(_x1), R(_y1), R(_z1) },
                ["size"] = new[] { R(_x1 - _x0), R(_y1 - _y0), R(_z1 - _z0) },
                ["centre"] = new[] { R((_x0 + _x1) / 2), R((_y0 + _y1) / 2), R((_z0 + _z1) / 2) },
            };
            static double R(double feet) => Math.Round(feet * FeetToM, 2);
        }

        static double[] Pt(XYZ p) => new[] { Math.Round(p.X * FeetToM, 3), Math.Round(p.Y * FeetToM, 3), Math.Round(p.Z * FeetToM, 3) };

        // ------------------------------------------------------------------------------------------------------------------------------------------------ IFC parameters

        /// <summary>The parameters of an element and of its type whose name says IFC, with their values.</summary>
        static Dictionary<string, string> IfcOf(Document doc, Element e)
        {
            var found = new Dictionary<string, string>();
            void Take(Element x, string prefix)
            {
                foreach (Parameter p in x.Parameters)
                {
                    var name = p.Definition?.Name;
                    if (name == null || name.IndexOf("ifc", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    string? value = null;
                    try { value = p.StorageType == StorageType.String ? p.AsString() : p.AsValueString(); } catch (Exception) { /* unreadable */ }
                    if (!string.IsNullOrWhiteSpace(value)) found[prefix + name] = value!;
                }
            }
            Take(e, "");
            var type = doc.GetElement(e.GetTypeId());
            if (type != null) Take(type, "type: ");
            return found;
        }

        /// <summary>Which IFC parameters the elements carry, and how the IFC classes and predefined types are spread over the categories that could hold a roof.</summary>
        static void IfcParameters(IList<Element> all, Dictionary<string, object?> report)
        {
            var doc = all.Count > 0 ? all[0].Document : null;
            if (doc == null) return;
            var names = new Dictionary<string, int>();
            var spread = new Dictionary<string, Dictionary<string, int>>();     // category -> "parameter = value" -> count
            var seenPerCategory = new Dictionary<string, int>();
            var samples = new List<Dictionary<string, object?>>();
            var sampled = new Dictionary<string, int>();
            foreach (var e in all)
            {
                var cat = e.Category;
                if (cat == null || cat.CategoryType != CategoryType.Model) continue;
                var n = seenPerCategory.GetValueOrDefault(cat.Name);
                if (n >= 4000) continue;                     // enough to say how a category is spread; the counts above are of every element
                seenPerCategory[cat.Name] = n + 1;
                var ifc = IfcOf(doc, e);
                foreach (var k in ifc.Keys) names[k] = names.GetValueOrDefault(k) + 1;
                if (!spread.TryGetValue(cat.Name, out var dist)) spread[cat.Name] = dist = new Dictionary<string, int>();
                foreach (var kv in ifc.Where(kv => IfcExportWords.IsMatch(kv.Key)))
                {
                    var key = kv.Key + " = " + kv.Value;
                    dist[key] = dist.GetValueOrDefault(key) + 1;
                }
                if (sampled.GetValueOrDefault(cat.Name) < 2 && ifc.Count > 0)
                {
                    sampled[cat.Name] = sampled.GetValueOrDefault(cat.Name) + 1;
                    samples.Add(new Dictionary<string, object?> { ["category"] = cat.Name, ["name"] = e.Name, ["ifc_parameters"] = ifc });
                }
            }
            report["ifc_parameter_names"] = names.OrderByDescending(kv => kv.Value).ToDictionary(kv => kv.Key, kv => kv.Value);
            report["ifc_spread_by_category"] = spread.Where(kv => kv.Value.Count > 0).ToDictionary(kv => kv.Key + " (first " + seenPerCategory[kv.Key] + ")", kv => kv.Value.OrderByDescending(x => x.Value).Take(25).ToDictionary(x => x.Key, x => x.Value));
            report["ifc_samples"] = samples.Take(60).ToList();
        }

        // ------------------------------------------------------------------------------------------------------------------------------------------------ the roof

        static void RoofCandidates(Document doc, IList<Element> all, Dictionary<int, string> worksets, List<(string Name, long Id)> phases, Dictionary<string, object?> report, List<string> failures)
        {
            var found = new List<Dictionary<string, object?>>();
            var counts = new Dictionary<string, int>();
            foreach (var e in all)
            {
                var cat = e.Category;
                if (cat == null || cat.CategoryType != CategoryType.Model) continue;
                var bic = cat.BuiltInCategory;
                var isRoofOrFloor = bic == BuiltInCategory.OST_Roofs || bic == BuiltInCategory.OST_Floors;
                var type = doc.GetElement(e.GetTypeId());
                var ifc = IfcOf(doc, e);
                var words = e.Name + " " + type?.Name + " " + string.Join(" ", ifc.Values);
                var says = RoofWords.IsMatch(words);
                if (!isRoofOrFloor && !says) continue;
                var why = isRoofOrFloor ? cat.Name : "named like a roof or slab (" + cat.Name + ")";
                counts[why] = counts.GetValueOrDefault(why) + 1;

                var bb = e.get_BoundingBox(null);
                var faces = UpFaces(e);
                var level = e.LevelId != ElementId.InvalidElementId ? doc.GetElement(e.LevelId)?.Name : null;
                var row = new Dictionary<string, object?>
                {
                    ["id"] = e.Id.Value, ["category"] = cat.Name, ["class"] = e.GetType().Name, ["name"] = e.Name, ["type"] = type?.Name,
                    ["family"] = (e as FamilyInstance)?.Symbol?.FamilyName, ["level"] = level,
                    ["workset"] = doc.IsWorkshared && worksets.TryGetValue(e.WorksetId.IntegerValue, out var wn) ? wn : null,
                    ["phase"] = phases.FirstOrDefault(p => p.Id == e.CreatedPhaseId.Value).Name,
                    ["box_size_m"] = bb == null ? null : new[] { Math.Round((bb.Max.X - bb.Min.X) * FeetToM, 2), Math.Round((bb.Max.Y - bb.Min.Y) * FeetToM, 2), Math.Round((bb.Max.Z - bb.Min.Z) * FeetToM, 2) },
                    ["box_min_m"] = bb == null ? null : Pt(bb.Min),
                    ["up_faces_area_m2"] = faces == null ? null : Math.Round(faces.Value.AreaFt2 * FeetToM * FeetToM, 1),
                    ["top_z_m"] = faces == null || faces.Value.TopZ == double.MinValue ? null : Math.Round(faces.Value.TopZ * FeetToM, 3),
                    ["largest_up_face_vertices"] = faces?.Vertices,
                    ["geometry"] = faces == null ? "none" : faces.Value.Meshes ? "mesh" : "solid",
                    ["why"] = why, ["ifc"] = ifc.Count == 0 ? null : ifc,
                };
                found.Add(row);
            }
            report["roof_candidate_reasons"] = counts;
            report["roof_candidates_total"] = found.Count;
            report["roof_candidates"] = found.OrderByDescending(r => r["up_faces_area_m2"] as double? ?? 0).Take(60).ToList();
        }

        static (double AreaFt2, double TopZ, int Vertices, bool Meshes)? UpFaces(Element e)
        {
            GeometryElement? g;
            try { g = e.get_Geometry(new Options { DetailLevel = ViewDetailLevel.Fine, ComputeReferences = false }); }
            catch (Exception) { return null; }
            if (g == null) return null;
            double area = 0, top = double.MinValue, largest = 0; var vertices = 0; var meshes = false;
            void Take(GeometryElement ge)
            {
                foreach (var go in ge)
                {
                    if (go is GeometryInstance gi) { Take(gi.GetInstanceGeometry()); continue; }
                    if (go is Solid s && s.Faces.Size > 0)
                    {
                        foreach (Face f in s.Faces)
                        {
                            if (f is not PlanarFace pf || pf.FaceNormal.Z < 0.99) continue;
                            var a = pf.Area; area += a; top = Math.Max(top, pf.Origin.Z);
                            if (a > largest) { largest = a; vertices = pf.GetEdgesAsCurveLoops().FirstOrDefault()?.Count() ?? 0; }
                        }
                    }
                    else if (go is Mesh m)
                    {
                        meshes = true;
                        for (var i = 0; i < m.NumTriangles; i++)
                        {
                            var t = m.get_Triangle(i);
                            var a = t.get_Vertex(0); var b = t.get_Vertex(1); var c = t.get_Vertex(2);
                            var n = (b - a).CrossProduct(c - a);
                            var len = n.GetLength();
                            if (len < 1e-12 || n.Z / len < 0.99) continue;
                            area += len / 2; top = Math.Max(top, Math.Max(a.Z, Math.Max(b.Z, c.Z)));
                        }
                    }
                }
            }
            try { Take(g); } catch (Exception) { return null; }
            return (area, top, vertices, meshes);
        }

        // ------------------------------------------------------------------------------------------------------------------------------------------------ pictures

        static void Pictures(Document doc, string outDir, Dictionary<string, object?> report, List<string> failures)
        {
            try
            {
                var typeId = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().FirstOrDefault(v => v.ViewFamily == ViewFamily.ThreeDimensional)?.Id;
                if (typeId == null) { failures.Add("no 3D view type: no pictures"); return; }
                var extent = report.TryGetValue("extent_m", out var ex) ? ex as Dictionary<string, object?> : null;
                double[]? centre = extent?["centre"] as double[], size = extent?["size"] as double[];
                using var t = new Transaction(doc, "Sportify inspect views");
                t.Start();
                var iso = View3D.CreateIsometric(doc, typeId);
                iso.Name = "Sportify inspect 3D";
                iso.DisplayStyle = DisplayStyle.Shading;
                var top = View3D.CreateIsometric(doc, typeId);
                top.Name = "Sportify inspect top";
                top.DisplayStyle = DisplayStyle.Shading;
                if (centre != null && size != null)
                {
                    var c = new XYZ(centre[0] / FeetToM, centre[1] / FeetToM, centre[2] / FeetToM);
                    top.SetOrientation(new ViewOrientation3D(c + new XYZ(0, 0, Math.Max(size[0], size[1]) / FeetToM * 2 + 100), XYZ.BasisY, -XYZ.BasisZ));
                }
                t.Commit();
                report["pictures"] = new[] { Export(doc, iso, Path.Combine(outDir, "view-3d")), Export(doc, top, Path.Combine(outDir, "view-top")) };
            }
            catch (Exception ex) { failures.Add("the pictures could not be made: " + ex.Message); }
        }

        static string? Export(Document doc, View3D view, string path)
        {
            var options = new ImageExportOptions
            {
                FilePath = path, ExportRange = ExportRange.SetOfViews, HLRandWFViewsFileType = ImageFileType.PNG, ShadowViewsFileType = ImageFileType.PNG,
                ImageResolution = ImageResolution.DPI_150, ZoomType = ZoomFitType.FitToPage, PixelSize = 1600, FitDirection = FitDirectionType.Horizontal,
            };
            options.SetViewsAndSheets(new List<ElementId> { view.Id });
            doc.ExportImage(options);
            var dir = Path.GetDirectoryName(path)!;
            return Directory.GetFiles(dir, Path.GetFileName(path) + "*.png").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
        }

        // ------------------------------------------------------------------------------------------------------------------------------------------------ the summary

        static string Summary(Dictionary<string, object?> r)
        {
            var sb = new StringBuilder();
            void Line(string s = "") => sb.AppendLine(s);
            object? Get(string k) => r.TryGetValue(k, out var v) ? v : null;
            Line("MODEL INSPECTION  " + Get("title"));
            Line("Revit " + Get("revit") + "; opened in " + Get("opened_in_seconds") + " s, all done in " + Get("total_seconds") + " s");
            if (Get("file_info") is Dictionary<string, object?> fi) Line("file: " + string.Join(", ", fi.Select(kv => kv.Key + "=" + kv.Value)));
            if (Get("project") is Dictionary<string, object?> pj) Line("project: " + string.Join(", ", pj.Where(kv => !string.IsNullOrWhiteSpace(kv.Value?.ToString())).Select(kv => kv.Key + "=" + kv.Value)));
            if (Get("place") is Dictionary<string, object?> pl) Line("place: " + string.Join(", ", pl.Select(kv => kv.Key + "=" + Show(kv.Value))));
            Line("units: " + Get("units_length") + "; workshared: " + Get("is_workshared") + "; elements: " + Get("element_count") + " (no category " + Get("elements_without_category") + ", annotation " + Get("annotation_elements") + ", direct shapes " + Get("direct_shapes") + ")");
            Line("extent m: " + Show(Get("extent_m")));
            Line();
            Line("LEVELS");
            foreach (var l in (Get("levels") as IEnumerable<Dictionary<string, object?>>) ?? Array.Empty<Dictionary<string, object?>>()) Line("  " + l["name"] + "  " + l["elevation_m"] + " m");
            Line();
            Line("CATEGORIES (count, revit classes)");
            foreach (var c in ((Get("categories") as IEnumerable<Dictionary<string, object?>>) ?? Array.Empty<Dictionary<string, object?>>()).Take(45))
                Line("  " + c["count"] + "  " + c["category"] + "  [" + string.Join(", ", ((Dictionary<string, int>)c["classes"]!).Select(kv => kv.Key + " " + kv.Value)) + "]");
            Line();
            Line("WORKSETS"); foreach (var w in (Get("worksets") as IEnumerable<Dictionary<string, object?>>) ?? Array.Empty<Dictionary<string, object?>>()) Line("  " + w["name"] + "  " + w["elements"]);
            Line("PHASES"); foreach (var p in (Get("phases") as IEnumerable<Dictionary<string, object?>>) ?? Array.Empty<Dictionary<string, object?>>()) Line("  " + p["name"] + "  " + p["elements_created"]);
            Line("LINKS: " + Show(Get("links")) + "   IMPORTS: " + Show(Get("imports")));
            Line();
            Line("IFC PARAMETER NAMES SEEN"); foreach (var kv in ((Get("ifc_parameter_names") as Dictionary<string, int>) ?? new()).Take(30)) Line("  " + kv.Value + "  " + kv.Key);
            Line("IFC CLASSES AND TYPES BY CATEGORY"); foreach (var kv in (Get("ifc_spread_by_category") as Dictionary<string, Dictionary<string, int>>) ?? new()) { Line("  " + kv.Key); foreach (var v in kv.Value.Take(12)) Line("      " + v.Value + "  " + v.Key); }
            Line();
            Line("ROOF CANDIDATES: " + Get("roof_candidates_total") + " " + Show(Get("roof_candidate_reasons")));
            foreach (var c in ((Get("roof_candidates") as IEnumerable<Dictionary<string, object?>>) ?? Array.Empty<Dictionary<string, object?>>()).Take(25))
                Line("  #" + c["id"] + "  " + c["category"] + " / " + c["class"] + "  '" + c["name"] + "'  type '" + c["type"] + "'  level " + c["level"] + "  workset " + c["workset"] + "  phase " + c["phase"]
                     + "  box " + Show(c["box_size_m"]) + "  up-faces " + c["up_faces_area_m2"] + " m2 at z " + c["top_z_m"] + "  (" + c["geometry"] + ", " + c["largest_up_face_vertices"] + " corners)  ifc " + Show(c["ifc"]));
            Line();
            Line("FAILURES: " + Show(Get("failures")));
            Line("PICTURES: " + Show(Get("pictures")));
            return sb.ToString();
        }

        static string Show(object? v) => v switch
        {
            null => "-",
            string s => s,
            double d => d.ToString("0.###", CultureInfo.InvariantCulture),
            System.Collections.IDictionary d => "{" + string.Join(", ", d.Keys.Cast<object>().Select(k => k + ": " + Show(d[k]))) + "}",
            System.Collections.IEnumerable e => "[" + string.Join(", ", e.Cast<object?>().Select(Show)) + "]",
            _ => Convert.ToString(v, CultureInfo.InvariantCulture) ?? "-",
        };
    }
}
