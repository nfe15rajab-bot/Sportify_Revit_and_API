using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;

namespace SportfyRevit
{
    /// <summary>
    /// Builds (or reuses a cached) real Revit Family for a placement's
    /// quality_key — an extrusion sized to its footprint, built from
    /// Revit's own "Metric Generic Model" template so it works for any
    /// placement category with no hand-built specialty family required,
    /// carrying the full SportifyFamilyParameters data set. One Family per
    /// quality_key, a single Type (named the same as the family) — the
    /// user-confirmed grouping: simplest, a direct 1:1 mirror of the app's
    /// own placements.
    ///
    /// Two cache layers, since a fresh family document + geometry + ~28
    /// parameters + save + load is real work (roughly a second, not
    /// instant) — fine for an occasional manual Import, too slow to redo
    /// on every one of AutoImportSync's 2-second ticks:
    /// - In-memory, per (document, quality_key): reused for the rest of
    ///   this Revit session without touching disk at all.
    /// - On-disk, under %AppData%\...\SportifyGeneratedFamilies\: reused
    ///   across Revit sessions/projects — the first time any user on this
    ///   machine hits a given quality_key, every later sync/session/project
    ///   just loads the saved .rfa directly, no regeneration.
    ///
    /// Untested beyond compilation against the real RevitAPI.dll installed
    /// on this machine — same honest caveat this project's other
    /// first-real-mutation code (SportifySharedParameters) already carries;
    /// creating/saving/loading a family document has no dry-run and
    /// couldn't be exercised inside an actual Revit session from here.
    /// </summary>
    internal static class SportifyFamilyGenerator
    {
        private const double DefaultThicknessM = 0.1;

        private static readonly Dictionary<string, ElementId> SessionCache = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Single entry point FamilyPlacementBuilder calls when no already-loaded family matched.</summary>
        public static FamilySymbol? GetOrCreateSymbol(Document doc, PlacementDto p)
        {
            var qualityKey = p.Parameters?.QualityKey;
            if (string.IsNullOrWhiteSpace(qualityKey)) return null; // nothing stable to name/cache a family by

            // Scoped by document too — an ElementId cached against a
            // different open document is meaningless (and, rarely, could
            // coincidentally resolve to an unrelated real element).
            string sessionKey = (doc.PathName ?? doc.Title) + "::" + qualityKey;
            if (SessionCache.TryGetValue(sessionKey, out var cachedId) && doc.GetElement(cachedId) is FamilySymbol cachedSymbol)
                return cachedSymbol;

            var symbol = LoadOrGenerate(doc, p, qualityKey);
            if (symbol != null) SessionCache[sessionKey] = symbol.Id;
            return symbol;
        }

        private static FamilySymbol? LoadOrGenerate(Document doc, PlacementDto p, string qualityKey)
        {
            string rfaPath = CachePath(qualityKey);

            if (!File.Exists(rfaPath))
                GenerateAndSaveFamily(doc.Application, p, qualityKey, rfaPath);

            if (!doc.LoadFamily(rfaPath, new OverwriteFamilyLoadOptions(), out Family family))
                return null;

            var symbolId = family.GetFamilySymbolIds().FirstOrDefault();
            if (symbolId == null || symbolId == ElementId.InvalidElementId) return null;

            var symbol = doc.GetElement(symbolId) as FamilySymbol;
            if (symbol != null && !symbol.IsActive)
            {
                symbol.Activate();
                doc.Regenerate();
            }
            return symbol;
        }

        private static void GenerateAndSaveFamily(Application app, PlacementDto p, string qualityKey, string rfaPath)
        {
            string? templatePath = GenericFamilyTemplateLocator.Resolve();
            if (templatePath == null)
                throw new InvalidOperationException("Couldn't locate (or pick) Revit's \"Metric Generic Model\" family template.");

            var familyDoc = app.NewFamilyDocument(templatePath);
            try
            {
                using (var t = new Transaction(familyDoc, "Build Sportify family"))
                {
                    t.Start();

                    var gen = PlacementDataHelpers.GetGeneralities(p);
                    var (_, _, _, unifiedLengthM, unifiedWidthM) = PlacementDataHelpers.GetUnifiedFields(p);
                    double lengthM = gen?.LengthM ?? unifiedLengthM ?? p.BoundingBox?.WidthM ?? 1.0;
                    double widthM = gen?.WidthM ?? unifiedWidthM ?? p.BoundingBox?.HeightM ?? 1.0;
                    // Gardens: real buildup depth (sum of the theme's layer thicknesses) rather than an
                    // arbitrary placeholder — fields/activities have no buildup-depth data, so the same
                    // nominal thickness PlacePlaceholderBox already uses stands in for those.
                    double depthM = gen?.BuildupDepthM ?? DefaultThicknessM;

                    BuildExtrusion(familyDoc, lengthM, widthM, depthM);

                    var fm = familyDoc.FamilyManager;
                    fm.NewType(qualityKey);
                    SportifyFamilyParameters.DefineOn(fm);
                    SportifyFamilyParameters.SetTypeValues(fm, p);

                    t.Commit();
                }

                Directory.CreateDirectory(Path.GetDirectoryName(rfaPath)!);
                familyDoc.SaveAs(rfaPath, new SaveAsOptions { OverwriteExistingFile = true });
            }
            finally
            {
                // Never prompts to save — everything needed is already on disk via SaveAs above.
                familyDoc.Close(false);
            }
        }

        /// <summary>
        /// A flat rectangle, centered on the family's own origin (not
        /// corner-anchored), extruded upward — centered because
        /// FamilyPlacementBuilder.PlaceFamilyInstance places every instance
        /// at the placement's CENTER point, so the family's own origin has
        /// to already BE that footprint's center for a placed instance to
        /// land where the layout actually put it. Fixed-size, not
        /// parameter-driven witness lines: since grouping is one Family per
        /// quality_key, a Type here never needs to resize, so parametric
        /// geometry constraints would add real complexity for no
        /// behavioral benefit.
        /// </summary>
        private static void BuildExtrusion(Document familyDoc, double lengthM, double widthM, double depthM)
        {
            double lengthFt = UnitUtils.ConvertToInternalUnits(lengthM, UnitTypeId.Meters);
            double widthFt = UnitUtils.ConvertToInternalUnits(widthM, UnitTypeId.Meters);
            double depthFt = UnitUtils.ConvertToInternalUnits(Math.Max(depthM, 0.01), UnitTypeId.Meters);
            double halfL = lengthFt / 2.0, halfW = widthFt / 2.0;

            var pts = new[]
            {
                new XYZ(-halfL, -halfW, 0),
                new XYZ(halfL, -halfW, 0),
                new XYZ(halfL, halfW, 0),
                new XYZ(-halfL, halfW, 0),
            };

            var loop = new CurveArray();
            for (int i = 0; i < pts.Length; i++)
                loop.Append(Line.CreateBound(pts[i], pts[(i + 1) % pts.Length]));
            var profile = new CurveArrArray();
            profile.Append(loop);

            var plane = Plane.CreateByNormalAndOrigin(XYZ.BasisZ, XYZ.Zero);
            var sketchPlane = SketchPlane.Create(familyDoc, plane);
            familyDoc.FamilyCreate.NewExtrusion(true, profile, sketchPlane, depthFt);
        }

        /// <summary>%AppData%\Autodesk\Revit\Addins\2025\SportifyGeneratedFamilies\{quality_key}.rfa — same ApplicationData-folder convention SportifySharedParameters already uses for its own Sportify-owned file.</summary>
        private static string CachePath(string qualityKey)
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Autodesk", "Revit", "Addins", "2025", "SportifyGeneratedFamilies");
            return Path.Combine(dir, SanitizeFileName(qualityKey) + ".rfa");
        }

        private static string SanitizeFileName(string name)
        {
            foreach (var c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name;
        }

        /// <summary>Always accepts/overwrites — this runs from an unattended sync loop (AutoImportSync), so it must never block on a dialog.</summary>
        private class OverwriteFamilyLoadOptions : IFamilyLoadOptions
        {
            public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues)
            {
                overwriteParameterValues = true;
                return true;
            }

            public bool OnSharedFamilyFound(Family sharedFamily, bool familyInUse, out FamilySource source, out bool overwriteParameterValues)
            {
                source = FamilySource.Family;
                overwriteParameterValues = true;
                return true;
            }
        }
    }
}
