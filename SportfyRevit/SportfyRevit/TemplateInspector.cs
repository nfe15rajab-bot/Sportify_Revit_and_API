using System.IO;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace SportfyRevit
{
    /// <summary>
    /// Reads what a Revit template (or any project) contains and writes it as JSON: view templates, views by type, filters, schedules, sheets and title blocks, browser organisations,
    /// phases, levels, text and line styles, project parameters, loaded families by category. The Sportify templates are built from Revit's own German BIM templates, and to adapt
    /// what is there it has to be known first (a .rte cannot be read without Revit). Read only: nothing in the document is changed, and a template opened for this is closed again
    /// without saving. Run unattended through SPORTIFY_INSPECT_TEMPLATES (see SportfyRevitApp), or from code with Inspect(doc).
    /// </summary>
    internal static class TemplateInspector
    {
        private static T? Try<T>(Func<T> read) { try { return read(); } catch (Exception) { return default; } }

        private static string? Param(Element e, BuiltInParameter p) => Try(() => e.get_Parameter(p)?.AsValueString() ?? e.get_Parameter(p)?.AsString());

        private static string? CategoryName(Document doc, ElementId id) => Try(() => Category.GetCategory(doc, id)?.Name);

        /// <summary>The DIN 277 / DIN 276 classes the Sportify template wrote onto elements (Sportify_DIN277, Sportify_KG): how many elements carry each combination, by category. Only for a test to read.</summary>
        public static Dictionary<string, int> NormSummary(Document doc)
        {
            var counts = new Dictionary<string, int>();
            foreach (var e in new FilteredElementCollector(doc).WhereElementIsNotElementType())
            {
                var kg = Try(() => e.LookupParameter("Sportify_KG")?.AsString());
                var din = Try(() => e.LookupParameter("Sportify_DIN277")?.AsString());
                if (string.IsNullOrEmpty(kg) && string.IsNullOrEmpty(din)) continue;
                var key = (e.Category?.Name ?? "?") + " | KG " + kg + " | " + din;
                counts[key] = counts.GetValueOrDefault(key) + 1;
            }
            return counts;
        }

        public static Dictionary<string, object?> Inspect(Document doc)
        {
            var all = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().ToList();
            var viewTemplates = all.Where(v => v.IsTemplate).ToList();
            var sheets = all.OfType<ViewSheet>().Where(s => !s.IsTemplate).ToList();
            var schedules = all.OfType<ViewSchedule>().Where(s => !s.IsTemplate && !s.IsInternalKeynoteSchedule && !s.IsTitleblockRevisionSchedule).ToList();
            var views = all.Where(v => !v.IsTemplate && v is not ViewSheet && v is not ViewSchedule && v.ViewType != ViewType.ProjectBrowser && v.ViewType != ViewType.SystemBrowser
                                       && v.ViewType != ViewType.Internal && v.ViewType != ViewType.Undefined).ToList();

            var result = new Dictionary<string, object?>
            {
                ["title"] = doc.Title,
                ["path"] = doc.PathName,
                ["revit"] = doc.Application.VersionName + " " + doc.Application.VersionBuild,
                ["counts"] = new Dictionary<string, int>
                {
                    ["viewTemplates"] = viewTemplates.Count, ["views"] = views.Count, ["sheets"] = sheets.Count, ["schedules"] = schedules.Count,
                    ["filters"] = new FilteredElementCollector(doc).OfClass(typeof(ParameterFilterElement)).GetElementCount(),
                    ["families"] = new FilteredElementCollector(doc).OfClass(typeof(Family)).GetElementCount(),
                    ["materials"] = new FilteredElementCollector(doc).OfClass(typeof(Material)).GetElementCount(),
                    ["fillPatterns"] = new FilteredElementCollector(doc).OfClass(typeof(FillPatternElement)).GetElementCount(),
                    ["linePatterns"] = new FilteredElementCollector(doc).OfClass(typeof(LinePatternElement)).GetElementCount(),
                    ["levels"] = new FilteredElementCollector(doc).OfClass(typeof(Level)).GetElementCount(),
                    ["grids"] = new FilteredElementCollector(doc).OfClass(typeof(Grid)).GetElementCount(),
                },
                ["viewTemplates"] = viewTemplates.OrderBy(v => v.Name).Select(v => new Dictionary<string, object?>
                {
                    ["name"] = v.Name, ["type"] = v.ViewType.ToString(), ["discipline"] = Param(v, BuiltInParameter.VIEW_DISCIPLINE),
                    ["scale"] = Try(() => (int?)v.Scale), ["detail"] = Try(() => v.DetailLevel.ToString()), ["display"] = Try(() => v.DisplayStyle.ToString()),
                    ["filters"] = Try(() => v.GetFilters().Count), ["notControlled"] = Try(() => v.GetNonControlledTemplateParameterIds().Count),
                }).ToList(),
                ["viewsByType"] = views.GroupBy(v => v.ViewType.ToString()).OrderBy(g => g.Key).ToDictionary(g => g.Key, g => g.Count()),
                ["views"] = views.OrderBy(v => v.ViewType.ToString()).ThenBy(v => v.Name).Take(300).Select(v => new Dictionary<string, object?>
                {
                    ["name"] = v.Name, ["type"] = v.ViewType.ToString(), ["template"] = Try(() => v.ViewTemplateId == ElementId.InvalidElementId ? null : doc.GetElement(v.ViewTemplateId)?.Name),
                    ["discipline"] = Param(v, BuiltInParameter.VIEW_DISCIPLINE), ["scale"] = Try(() => (int?)v.Scale),
                }).ToList(),
                ["filters"] = new FilteredElementCollector(doc).OfClass(typeof(ParameterFilterElement)).Cast<ParameterFilterElement>().OrderBy(f => f.Name).Select(f => new Dictionary<string, object?>
                {
                    ["name"] = f.Name, ["categories"] = Try(() => f.GetCategories().Select(c => CategoryName(doc, c)).ToList()),
                }).ToList(),
                ["schedules"] = schedules.OrderBy(s => s.Name).Select(s => new Dictionary<string, object?>
                {
                    ["name"] = s.Name, ["category"] = CategoryName(doc, s.Definition.CategoryId) ?? "(several or none)", ["fields"] = Try(() => s.Definition.GetFieldCount()),
                    ["materialTakeoff"] = Try(() => s.Definition.IsMaterialTakeoff), ["template"] = Try(() => s.ViewTemplateId == ElementId.InvalidElementId ? null : doc.GetElement(s.ViewTemplateId)?.Name),
                }).ToList(),
                ["sheets"] = sheets.OrderBy(s => s.SheetNumber).Select(s => new Dictionary<string, object?>
                {
                    ["number"] = s.SheetNumber, ["name"] = s.Name, ["views"] = Try(() => s.GetAllPlacedViews().Count),
                    ["titleBlock"] = Try(() => new FilteredElementCollector(doc, s.Id).OfCategory(BuiltInCategory.OST_TitleBlocks).WhereElementIsNotElementType().Cast<FamilyInstance>()
                        .Select(t => t.Symbol.FamilyName + " : " + t.Symbol.Name).FirstOrDefault()),
                }).ToList(),
                ["titleBlockTypes"] = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_TitleBlocks).WhereElementIsElementType().Cast<FamilySymbol>()
                    .Select(t => t.FamilyName + " : " + t.Name).OrderBy(n => n).ToList(),
                ["browserOrganizations"] = Try(() => new FilteredElementCollector(doc).OfClass(typeof(BrowserOrganization)).Cast<BrowserOrganization>().Select(b => b.Name).OrderBy(n => n).ToList()),
                ["currentBrowserForViews"] = Try(() => BrowserOrganization.GetCurrentBrowserOrganizationForViews(doc)?.Name),
                ["currentBrowserForSheets"] = Try(() => BrowserOrganization.GetCurrentBrowserOrganizationForSheets(doc)?.Name),
                ["phases"] = Try(() => doc.Phases.Cast<Phase>().Select(p => p.Name).ToList()),
                ["levels"] = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation).Select(l => l.Name).ToList(),
                ["textTypes"] = new FilteredElementCollector(doc).OfClass(typeof(TextNoteType)).Cast<Element>().Select(t => t.Name).OrderBy(n => n).ToList(),
                ["dimensionTypes"] = new FilteredElementCollector(doc).OfClass(typeof(DimensionType)).Cast<Element>().Select(t => t.Name).OrderBy(n => n).ToList(),
                ["lineStyles"] = Try(() => doc.Settings.Categories.get_Item(BuiltInCategory.OST_Lines).SubCategories.Cast<Category>().Select(c => c.Name).OrderBy(n => n).ToList()),
                ["projectParameters"] = Try(() =>
                {
                    var list = new List<string>();
                    var it = doc.ParameterBindings.ForwardIterator();
                    while (it.MoveNext()) list.Add(((Definition)it.Key).Name);
                    return list.OrderBy(n => n).ToList();
                }),
                ["familiesByCategory"] = new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>().GroupBy(f => f.FamilyCategory?.Name ?? "(none)")
                    .OrderByDescending(g => g.Count()).ToDictionary(g => g.Key, g => g.Count()),
            };
            return result;
        }

        /// <summary>Opens each template (read only, not shown), writes its inspection to outDir\&lt;file name&gt;.json and closes it without saving. Returns what was written.</summary>
        public static List<string> InspectFiles(UIApplication uiApp, IEnumerable<string> paths, string outDir)
        {
            Directory.CreateDirectory(outDir);
            var written = new List<string>();
            foreach (var path in paths)
            {
                Document? doc = null;
                try
                {
                    doc = uiApp.Application.OpenDocumentFile(path);
                    var file = Path.Combine(outDir, Path.GetFileNameWithoutExtension(path) + ".json");
                    File.WriteAllText(file, JsonSerializer.Serialize(Inspect(doc), new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
                    written.Add(file);
                    SportifyLog.Info("templates", "inspected " + path + " -> " + file);
                }
                catch (Exception ex) { SportifyLog.Error("templates", "could not inspect " + path, ex); }
                finally { try { doc?.Close(false); } catch (Exception) { /* already closed */ } }
            }
            return written;
        }
    }
}
