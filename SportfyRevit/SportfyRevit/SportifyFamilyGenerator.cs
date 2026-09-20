using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;

namespace SportfyRevit
{
    /// <summary>
    /// Builds (or reuses a cached) real Revit Family for a placement's quality_key — an extrusion sized to its footprint, built from Revit's own
    /// Generic Model template (GenericFamilyTemplateLocator finds it in any language) so it works for any placement category with no hand-built
    /// specialty family required, carrying the full SportifyFamilyParameters data set.
    ///
    /// One Family per quality_key AND size. The size is part of the name (POLYVALENT_MINI_MEDIUM__2000x1200): the extrusion is a fixed rectangle, so a
    /// family cached under the quality_key alone gave every later layout the size of the first placement that ever asked for it (a 24 x 15 m
    /// volleyball court got the 16 x 8 m file). Two cache layers, since a fresh family document + geometry + ~28 parameters + save + load is real
    /// work (roughly a second):
    /// - In-memory, per (document, family name): reused for the rest of this Revit session.
    /// - On-disk, under %AppData%\...\SportifyGeneratedFamilies\: reused across sessions and projects.
    ///
    /// This class never swallows a failure: it throws, with the reason in the message, and FamilyPreparation records it in the import report and
    /// the log. It must run OUTSIDE the import transaction (FamilyPreparation opens its own small ones): creating and loading families is
    /// real document mutation with real failure modes, and one bad family must not be able to abort the whole import.
    /// </summary>
    internal static class SportifyFamilyGenerator
    {
        private const double DefaultThicknessM = 0.1;

        private static readonly Dictionary<string, ElementId> SessionCache = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// The name this placement's generated family has, or null when it has no quality_key (then there is nothing stable to name or cache a family
        /// by, and the piece is a placeholder box). quality_key, then the footprint in centimetres, then (gardens) the build-up depth.
        /// </summary>
        public static string? FamilyNameFor(PlacementDto p)
        {
            var qualityKey = p.Parameters?.QualityKey;
            if (string.IsNullOrWhiteSpace(qualityKey)) return null;
            var (lengthM, widthM, depthM) = SizeOf(p);
            var name = $"{SanitizeFileName(qualityKey)}__{Cm(lengthM)}x{Cm(widthM)}";
            if (PlacementDataHelpers.GetGeneralities(p)?.BuildupDepthM != null) name += $"__d{Cm(depthM)}";
            return name;
        }

        private static int Cm(double meters) => (int)Math.Round(meters * 100.0);

        /// <summary>Footprint and thickness the extrusion gets: the generalities block when the export has one, else the dimensions of the field/activity/garden, else the bounding box.</summary>
        private static (double LengthM, double WidthM, double DepthM) SizeOf(PlacementDto p)
        {
            var gen = PlacementDataHelpers.GetGeneralities(p);
            var (_, _, _, unifiedLengthM, unifiedWidthM) = PlacementDataHelpers.GetUnifiedFields(p);
            double lengthM = gen?.LengthM ?? unifiedLengthM ?? p.BoundingBox?.WidthM ?? 1.0;
            double widthM = gen?.WidthM ?? unifiedWidthM ?? p.BoundingBox?.HeightM ?? 1.0;
            // Gardens: real buildup depth (sum of the theme's layer thicknesses) rather than an arbitrary placeholder — fields/activities have no
            // buildup-depth data, so the same nominal thickness PlacePlaceholderBox uses stands in for those.
            double depthM = gen?.BuildupDepthM ?? DefaultThicknessM;
            return (lengthM, widthM, depthM);
        }

        /// <summary>The symbol of a family with exactly this name that is already in the project (a generated one loaded earlier), or null.</summary>
        public static FamilySymbol? FindLoaded(Document doc, string familyName)
        {
            foreach (var symbol in new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>())
                if (string.Equals(symbol.Family?.Name, familyName, StringComparison.OrdinalIgnoreCase)) return symbol;
            return null;
        }

        /// <summary>
        /// Loads the family for this placement, generating and saving it first when it is not on disk yet. Must be called inside a transaction on
        /// <paramref name="doc"/> (LoadFamily and Activate modify it) and NOT inside the import's. Throws with the reason when it cannot.
        /// </summary>
        public static FamilySymbol GetOrCreateSymbol(Document doc, PlacementDto p)
        {
            var familyName = FamilyNameFor(p) ?? throw new InvalidOperationException("the piece has no quality_key, so there is nothing to name a family by");

            // Scoped by document too — an ElementId cached against a different open document is meaningless.
            string sessionKey = (string.IsNullOrEmpty(doc.PathName) ? doc.Title : doc.PathName) + "::" + familyName;
            if (SessionCache.TryGetValue(sessionKey, out var cachedId) && doc.GetElement(cachedId) is FamilySymbol cachedSymbol)
                return cachedSymbol;

            var symbol = LoadOrGenerate(doc, p, familyName);
            SessionCache[sessionKey] = symbol.Id;
            return symbol;
        }

        private static FamilySymbol LoadOrGenerate(Document doc, PlacementDto p, string familyName)
        {
            string rfaPath = CachePath(familyName);

            if (!File.Exists(rfaPath))
                GenerateAndSaveFamily(doc.Application, p, p.Parameters!.QualityKey!, rfaPath);

            if (!doc.LoadFamily(rfaPath, new OverwriteFamilyLoadOptions(), out Family family) && family == null)
                throw new InvalidOperationException("Revit did not load " + rfaPath);

            var symbolId = family.GetFamilySymbolIds().FirstOrDefault();
            if (symbolId == null || symbolId == ElementId.InvalidElementId)
                throw new InvalidOperationException("the generated family " + familyName + " has no type");

            var symbol = doc.GetElement(symbolId) as FamilySymbol ?? throw new InvalidOperationException("the type of " + familyName + " could not be read");
            if (!symbol.IsActive)
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
                throw new InvalidOperationException(GenericFamilyTemplateLocator.FailureReason ?? "Revit's Generic Model family template was not found");

            var familyDoc = app.NewFamilyDocument(templatePath);
            try
            {
                using (var t = new Transaction(familyDoc, "Build Sportify family"))
                {
                    t.Start();

                    var (lengthM, widthM, depthM) = SizeOf(p);
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
        /// A flat rectangle, centered on the family's own origin (not corner-anchored), extruded upward — centered because
        /// FamilyPlacementBuilder.PlaceFamilyInstance places every instance at the placement's CENTER point, so the family's own origin has to
        /// already BE that footprint's center for a placed instance to land where the layout actually put it. Fixed-size, which is why the size
        /// is part of the family's name.
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

        /// <summary>%AppData%\Autodesk\Revit\Addins\2025\SportifyGeneratedFamilies\{family name}.rfa — same ApplicationData-folder convention SportifySharedParameters uses.</summary>
        private static string CachePath(string familyName)
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Autodesk", "Revit", "Addins", "2025", "SportifyGeneratedFamilies");
            return Path.Combine(dir, familyName + ".rfa");
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
