using System.IO;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;

namespace SportfyRevit
{
    /// <summary>
    /// The kinetic families, authored as REVIT ADAPTIVE COMPONENTS: a shape whose geometry is defined by placement points, so that an instance takes any position,
    /// length and tilt by moving its points (and a user can drag them in Revit). Two families cover every kind of kinetic unit:
    ///   SportifyKineticAdaptiveBar       8 placement points, a straight member with a rectangular section: four corner points at each end, lofted. A blade, a fin, a
    ///                                    post, a rail, a rod, a mast, a roller housing, a bottom bar are all this one family: their tilt and size are just where the
    ///                                    eight points are put.
    ///   SportifyKineticAdaptiveMembrane   4 placement points, a ruled membrane between two opposite edges (a hyperbolic paraboloid when the corners are not level),
    ///                                    with its four edge cables: a tensile sail, a roller curtain.
    /// Built from Revit's own adaptive Generic Model family template (found in any language, confirmed by reading the family: AdaptiveComponentFamilyUtils),
    /// cached under %AppData%\...\SportifyGeneratedFamilies, loaded into the project on demand. Every API call is a named step, so a failure says which.
    /// </summary>
    internal static class AdaptiveFamilyBuilder
    {
        internal const string BarName = "SportifyKineticAdaptiveBar";
        internal const string SurfaceName = "SportifyKineticAdaptiveMembrane";   // (was ...Surface, whose loft was a void form: a new name so an old cached copy is not reused)

        static string? _template;
        static bool _searched;

        /// <summary>Why there is no adaptive template, in words a person can act on; null when one was found.</summary>
        internal static string? FailureReason { get; private set; } = "the search for Revit's adaptive family template has not run";

        static string SavedChoiceFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Sportify", "adaptive-template.txt");

        static string CachePath(string name) => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Autodesk", "Revit", "Addins", "2025", "SportifyGeneratedFamilies", name + ".rfa");

        static T Step<T>(string what, Func<T> f)
        {
            try { return f(); }
            catch (Exception ex) { throw new InvalidOperationException("adaptive family, " + what + ": " + ex.Message, ex); }
        }

        static void Step(string what, Action a) => Step<object?>(what, () => { a(); return null; });

        // ------------------------------------------------------------------ the template

        /// <summary>Finds Revit's adaptive Generic Model template (once per session): the remembered choice, else a search of the template folders, each candidate confirmed as an adaptive family by opening it.</summary>
        internal static string? FindTemplate(Application app)
        {
            if (_template != null && File.Exists(_template)) return _template;
            if (_searched) return null;
            _searched = true;

            _template = FromSavedChoice(app) ?? FromSearch(app);
            if (_template != null) { Remember(_template); FailureReason = null; }
            else FailureReason = "Revit's adaptive Generic Model family template was not found in this Revit's template folders (it is called \"Metric Generic Model Adaptive.rft\" in English)";
            SportifyLog.Info("adaptive-template", _template != null ? "template: " + _template : "no template: " + FailureReason);
            return _template;
        }

        static string? FromSavedChoice(Application app)
        {
            try
            {
                if (!File.Exists(SavedChoiceFile)) return null;
                var saved = File.ReadAllText(SavedChoiceFile).Trim();
                return File.Exists(saved) && IsAdaptiveTemplate(app, saved) ? saved : null;
            }
            catch (Exception ex) { SportifyLog.Warn("adaptive-template", "the remembered template could not be used: " + ex.Message); return null; }
        }

        static string? FromSearch(Application app)
        {
            var roots = new List<string>();
            void Add(string? dir) { if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir) && !roots.Contains(dir, StringComparer.OrdinalIgnoreCase)) roots.Add(dir); }
            try { Add(app.FamilyTemplatePath); } catch (Exception) { }
            try { if (!string.IsNullOrWhiteSpace(app.FamilyTemplatePath)) Add(Path.GetDirectoryName(app.FamilyTemplatePath.TrimEnd('\\', '/'))); } catch (Exception) { }
            Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Autodesk", "RVT " + app.VersionNumber, "Family Templates"));

            var files = new List<string>();
            foreach (var root in roots)
            {
                try { files.AddRange(Directory.EnumerateFiles(root, "*.rft", SearchOption.AllDirectories)); }
                catch (Exception ex) { SportifyLog.Warn("adaptive-template", "could not list " + root + ": " + ex.Message); }
            }
            // "adaptive" in English, "adaptiv" in German, "adaptatif" in French: the name only suggests, opening the file confirms
            var candidates = files.Where(f => { var n = Path.GetFileNameWithoutExtension(f).ToLowerInvariant(); return n.Contains("adaptiv") || n.Contains("adaptat"); })
                .OrderBy(f => { var n = Path.GetFileNameWithoutExtension(f).ToLowerInvariant(); return (n.Contains("generic") || n.Contains("allgemein") || n.Contains("modele") ? 0 : 1) + (n.StartsWith("metric") || n.Contains("metrisch") ? 0 : 2); })
                .ToList();
            SportifyLog.Info("adaptive-template", $"{files.Count} template file(s) in {roots.Count} folder(s), {candidates.Count} look adaptive");
            foreach (var c in candidates.Take(6)) if (IsAdaptiveTemplate(app, c)) return c;
            return null;
        }

        static bool IsAdaptiveTemplate(Application app, string path)
        {
            Document? familyDoc = null;
            try
            {
                familyDoc = app.NewFamilyDocument(path);
                return familyDoc.OwnerFamily != null && AdaptiveComponentFamilyUtils.IsAdaptiveComponentFamily(familyDoc.OwnerFamily);
            }
            catch (Exception ex) { SportifyLog.Warn("adaptive-template", "cannot use " + path + ": " + ex.Message); return false; }
            finally { try { familyDoc?.Close(false); } catch (Exception) { } }
        }

        static void Remember(string path)
        {
            try { Directory.CreateDirectory(Path.GetDirectoryName(SavedChoiceFile)!); File.WriteAllText(SavedChoiceFile, path); }
            catch (Exception ex) { SportifyLog.Warn("adaptive-template", "could not remember the template: " + ex.Message); }
        }

        // ------------------------------------------------------------------ authoring

        /// <summary>The eight corners of a 0.1 x 0.1 x 1 (feet) bar, as the seed positions of the placement points (a proper prism, so the loft can be made).</summary>
        static XYZ[] BarSeed()
        {
            const double h = 0.05, l = 1.0;
            XYZ[] Section(double z) => new[] { new XYZ(-h, -h, z), new XYZ(h, -h, z), new XYZ(h, h, z), new XYZ(-h, h, z) };
            return Section(0).Concat(Section(l)).ToArray();
        }

        /// <summary>A closed loop of four curves through consecutive points of a quad: one profile of the loft.</summary>
        static ReferenceArray Loop(Document famDoc, ReferencePoint[] pts, int first)
        {
            var refs = new ReferenceArray();
            for (var k = 0; k < 4; k++)
            {
                var arr = new ReferencePointArray();
                arr.Append(pts[first + k]);
                arr.Append(pts[first + (k + 1) % 4]);
                var curve = famDoc.FamilyCreate.NewCurveByPoints(arr);
                refs.Append(curve.GeometryCurve.Reference);
            }
            return refs;
        }

        static void MakePlacementPoints(Document famDoc, ReferencePoint[] pts)
        {
            foreach (var p in pts) AdaptiveComponentFamilyUtils.MakeAdaptivePoint(famDoc, p.Id, AdaptivePointType.PlacementPoint);
            for (var i = 0; i < pts.Length; i++)
            {
                // creation order is placement order; set it explicitly all the same, so an instance's points come back in the order the placer expects
                try { if (AdaptiveComponentFamilyUtils.GetPlacementNumber(famDoc, pts[i].Id) != i + 1) AdaptiveComponentFamilyUtils.SetPlacementNumber(famDoc, pts[i].Id, i + 1); }
                catch (Exception ex) { SportifyLog.Warn("adaptive-family", "placement number " + (i + 1) + ": " + ex.Message); }
            }
        }

        static void BuildBar(Document famDoc)
        {
            var seed = BarSeed();
            var pts = Step("the eight reference points", () => seed.Select(p => famDoc.FamilyCreate.NewReferencePoint(p)).ToArray());
            Step("making them placement points", () => MakePlacementPoints(famDoc, pts));
            var loft = Step("the two end profiles", () =>
            {
                var raa = new ReferenceArrayArray();
                raa.Append(Loop(famDoc, pts, 0));
                raa.Append(Loop(famDoc, pts, 4));
                return raa;
            });
            Step("the loft between them", () => famDoc.FamilyCreate.NewLoftForm(true, loft));
        }

        static void BuildSurface(Document famDoc)
        {
            // a unit square, one diagonal pair lifted: a hyperbolic paraboloid from the start
            var seed = new[] { new XYZ(0, 0, 0.1), new XYZ(1, 0, -0.1), new XYZ(1, 1, 0.1), new XYZ(0, 1, -0.1) };
            var pts = Step("the four reference points", () => seed.Select(p => famDoc.FamilyCreate.NewReferencePoint(p)).ToArray());
            Step("making them placement points", () => MakePlacementPoints(famDoc, pts));
            ReferencePointArray Two(int a, int b) { var r = new ReferencePointArray(); r.Append(pts[a]); r.Append(pts[b]); return r; }
            var edges = Step("the four edge curves", () => new[] { famDoc.FamilyCreate.NewCurveByPoints(Two(0, 1)), famDoc.FamilyCreate.NewCurveByPoints(Two(3, 2)),
                                                                   famDoc.FamilyCreate.NewCurveByPoints(Two(1, 2)), famDoc.FamilyCreate.NewCurveByPoints(Two(0, 3)) });
            Step("the ruled surface between two opposite edges", () =>
            {
                var a = new ReferenceArray(); a.Append(edges[0].GeometryCurve.Reference);
                var b = new ReferenceArray(); b.Append(edges[1].GeometryCurve.Reference);
                var raa = new ReferenceArrayArray(); raa.Append(a); raa.Append(b);
                return famDoc.FamilyCreate.NewLoftForm(true, raa);      // true: with open profiles this is a surface; false would make a VOID form, which no view shows
            });
        }

        /// <summary>Authors, saves and closes one adaptive family; returns the .rfa path. Reuses the file when it is already cached (delete it to rebuild).</summary>
        internal static string Author(Application app, string name, bool bar, bool rebuild = false)
        {
            var path = CachePath(name);
            if (File.Exists(path) && !rebuild) return path;

            var template = FindTemplate(app) ?? throw new InvalidOperationException(FailureReason ?? "Revit's adaptive family template was not found");
            var famDoc = Step("opening the adaptive template", () => app.NewFamilyDocument(template));
            try
            {
                using (var t = new Transaction(famDoc, "Sportify: build " + name))
                {
                    t.Start();
                    if (bar) BuildBar(famDoc); else BuildSurface(famDoc);
                    t.Commit();
                }
                var expected = bar ? 8 : 4;
                var actual = AdaptiveComponentFamilyUtils.GetNumberOfPlacementPoints(famDoc.OwnerFamily);
                if (actual != expected) throw new InvalidOperationException($"adaptive family, checking it: {actual} placement points, {expected} expected");
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                Step("saving " + path, () => famDoc.SaveAs(path, new SaveAsOptions { OverwriteExistingFile = true }));
            }
            finally { try { famDoc.Close(false); } catch (Exception) { } }
            return path;
        }

        // ------------------------------------------------------------------ loading

        /// <summary>The symbol of the adaptive bar or surface family in this project: found by name when loaded, else authored (or taken from the cache) and loaded. Inside a transaction on <paramref name="doc"/>.</summary>
        internal static FamilySymbol GetOrLoad(Document doc, bool bar)
        {
            var name = bar ? BarName : SurfaceName;
            var symbol = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
                .FirstOrDefault(s => string.Equals(s.Family?.Name, name, StringComparison.OrdinalIgnoreCase));
            if (symbol == null)
            {
                var path = Author(doc.Application, name, bar);
                if (!doc.LoadFamily(path, new OverwriteLoad(), out Family family) && family == null) throw new InvalidOperationException("Revit did not load " + path);
                var id = family.GetFamilySymbolIds().FirstOrDefault();
                symbol = id != null && id != ElementId.InvalidElementId ? doc.GetElement(id) as FamilySymbol : null;
                if (symbol == null) throw new InvalidOperationException("the adaptive family " + name + " has no type");
            }
            if (!symbol.IsActive) { symbol.Activate(); doc.Regenerate(); }
            if (!AdaptiveComponentInstanceUtils.IsAdaptiveFamilySymbol(symbol)) throw new InvalidOperationException(name + " is loaded but is not an adaptive family: delete it from the project and from SportifyGeneratedFamilies and build again");
            return symbol;
        }

        sealed class OverwriteLoad : IFamilyLoadOptions
        {
            public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues) { overwriteParameterValues = true; return true; }
            public bool OnSharedFamilyFound(Family sharedFamily, bool familyInUse, out FamilySource source, out bool overwriteParameterValues) { source = FamilySource.Family; overwriteParameterValues = true; return true; }
        }
    }
}
