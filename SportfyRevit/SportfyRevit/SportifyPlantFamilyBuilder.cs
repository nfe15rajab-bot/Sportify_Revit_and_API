using System.IO;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;

namespace SportfyRevit
{
    /// <summary>
    /// Builds a real Revit family per plant species.
    ///
    /// A tree is a family, not a build-up — the counterpart to
    /// SportifyFloorTypeBuilder, which turns a provider assembly into a floor
    /// type. Same shape of idea, different Revit object, because the two things
    /// genuinely are different: ground you draw versus an object you place.
    ///
    /// The geometry is a trunk and a crown, sized from the species' own
    /// published figures: Cornus mas is 5-6 m tall with a 5-6 m crown, and
    /// that is what appears in the model. Not a botanical model — a massing
    /// that is correct about the two dimensions anyone designs against, which
    /// is how a landscape architect draws a tree at this stage anyway. A real
    /// species family with foliage is a modelling job, and when a firm has one
    /// it should be matched by name instead of generated.
    ///
    /// Crown diameter varies within a species, so it is a family PARAMETER
    /// rather than baked geometry: one family per species, resized per
    /// instance, instead of a family per size.
    /// </summary>
    internal static class SportifyPlantFamilyBuilder
    {
        /// <summary>Trunk as a share of total height — enough to read as a tree rather than a lollipop.</summary>
        private const double TrunkHeightFraction = 0.35;

        /// <summary>Trunk diameter as a share of crown. Roughly what a mature tree looks like in plan.</summary>
        private const double TrunkDiameterFraction = 0.08;

        private static readonly Dictionary<string, ElementId> SessionCache = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// The symbol for a species, generating the family the first time it is
        /// needed. Cached per document for the session, and on disk across
        /// sessions — building a family document is real work, and
        /// AutoImportSync can run every couple of seconds.
        /// </summary>
        public static FamilySymbol? GetOrCreateSymbol(Document doc, VegetationDto plant)
        {
            var key = plant.SpeciesKey;
            if (string.IsNullOrWhiteSpace(key)) return null;

            string sessionKey = (doc.PathName ?? doc.Title) + "::plant::" + key;
            if (SessionCache.TryGetValue(sessionKey, out var cachedId) && doc.GetElement(cachedId) is FamilySymbol cached)
                return cached;

            string familyName = string.IsNullOrWhiteSpace(plant.RevitFamilyName)
                ? $"Sportify - {plant.BotanicalName ?? key}"
                : plant.RevitFamilyName!;

            // A family the firm already has wins outright: a real species family
            // with foliage beats anything generated here, and a practice that has
            // modelled its planting palette should see its own trees.
            var existing = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilySymbol))
                .Cast<FamilySymbol>()
                .FirstOrDefault(s => string.Equals(s.Family?.Name, familyName, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                SessionCache[sessionKey] = existing.Id;
                return Activate(existing);
            }

            string rfaPath = CachePath(familyName);
            if (!File.Exists(rfaPath))
            {
                GenerateAndSave(doc.Application, plant, familyName, rfaPath);
            }

            if (!doc.LoadFamily(rfaPath, new OverwriteFamilyLoadOptions(), out Family loaded) && loaded == null)
                return null;

            var symbol = loaded?.GetFamilySymbolIds()
                .Select(id => doc.GetElement(id) as FamilySymbol)
                .FirstOrDefault(s => s != null);
            if (symbol == null) return null;

            SessionCache[sessionKey] = symbol.Id;
            return Activate(symbol);
        }

        private static FamilySymbol Activate(FamilySymbol symbol)
        {
            if (!symbol.IsActive) symbol.Activate();
            return symbol;
        }

        private static void GenerateAndSave(Application app, VegetationDto plant, string familyName, string rfaPath)
        {
            string? templatePath = GenericFamilyTemplateLocator.Resolve();
            if (templatePath == null)
                throw new InvalidOperationException("Couldn't locate Revit's \"Metric Generic Model\" family template.");

            var familyDoc = app.NewFamilyDocument(templatePath);
            try
            {
                using (var t = new Transaction(familyDoc, "Build Sportify plant family"))
                {
                    t.Start();

                    double crownM = plant.CrownM > 0 ? plant.CrownM : 1.0;
                    double heightM = plant.HeightM > 0 ? plant.HeightM : 1.0;

                    BuildPlantGeometry(familyDoc, crownM, heightM, plant.Form);
                    StampSpeciesParameters(familyDoc.FamilyManager, plant);

                    t.Commit();
                }

                Directory.CreateDirectory(Path.GetDirectoryName(rfaPath)!);
                familyDoc.SaveAs(rfaPath, new SaveAsOptions { OverwriteExistingFile = true });
            }
            finally
            {
                familyDoc.Close(false);
            }
        }

        /// <summary>
        /// Trunk plus crown, both centred on the family origin because
        /// FamilyPlacementBuilder places every instance at the placement's
        /// centre point — the trunk has to BE that point or a placed tree lands
        /// half a crown away from where it was drawn.
        ///
        /// Ground cover and grasses get the crown only: a sedum mat has no
        /// trunk, and drawing one would be a lie the model then carries.
        /// </summary>
        private static void BuildPlantGeometry(Document familyDoc, double crownM, double heightM, string? form)
        {
            double crownFt = UnitUtils.ConvertToInternalUnits(crownM, UnitTypeId.Meters);
            double heightFt = UnitUtils.ConvertToInternalUnits(heightM, UnitTypeId.Meters);

            bool woody = form == "tree" || form == "shrub";
            double trunkTopFt = woody ? heightFt * TrunkHeightFraction : 0;

            if (woody && trunkTopFt > 0)
            {
                double trunkRadiusFt = Math.Max(crownFt * TrunkDiameterFraction / 2.0,
                                                UnitUtils.ConvertToInternalUnits(0.05, UnitTypeId.Meters));
                ExtrudeCircle(familyDoc, trunkRadiusFt, 0, trunkTopFt);
            }

            // The crown fills the rest of the height. For a mat or a grass clump
            // that is the whole plant, sitting on the ground.
            ExtrudeCircle(familyDoc, crownFt / 2.0, trunkTopFt, heightFt);
        }

        private static void ExtrudeCircle(Document familyDoc, double radiusFt, double baseFt, double topFt)
        {
            double height = topFt - baseFt;
            if (height <= 0 || radiusFt <= 0) return;

            // Two half-arcs: Revit rejects a full circle as a single closed curve
            // in an extrusion profile.
            var centre = new XYZ(0, 0, baseFt);
            var loop = new CurveArray();
            loop.Append(Arc.Create(centre, radiusFt, 0, Math.PI, XYZ.BasisX, XYZ.BasisY));
            loop.Append(Arc.Create(centre, radiusFt, Math.PI, 2 * Math.PI, XYZ.BasisX, XYZ.BasisY));

            var profile = new CurveArrArray();
            profile.Append(loop);

            var plane = Plane.CreateByNormalAndOrigin(XYZ.BasisZ, centre);
            var sketchPlane = SketchPlane.Create(familyDoc, plane);
            familyDoc.FamilyCreate.NewExtrusion(true, profile, sketchPlane, height);
        }

        /// <summary>
        /// Species data written onto the family itself, so the model can be
        /// scheduled and analysed without the web app.
        ///
        /// Height is here because it was asked for specifically: a shading, wind
        /// or clearance study needs mature height, and it cannot be recovered
        /// from a plan footprint. Substrate depth is here because it is what
        /// decides whether the roof can actually carry the planting.
        /// </summary>
        private static void StampSpeciesParameters(FamilyManager fm, VegetationDto plant)
        {
            fm.NewType(plant.BotanicalName ?? plant.SpeciesKey ?? "Sportify plant");

            Add("Sportify_BotanicalName", SpecTypeId.String.Text, GroupTypeId.IdentityData, plant.BotanicalName);
            Add("Sportify_CommonName", SpecTypeId.String.Text, GroupTypeId.IdentityData, plant.CommonName);
            Add("Sportify_Form", SpecTypeId.String.Text, GroupTypeId.IdentityData, plant.Form);
            Add("Sportify_HeightRange", SpecTypeId.String.Text, GroupTypeId.IdentityData, plant.HeightRange);
            Add("Sportify_Source", SpecTypeId.String.Text, GroupTypeId.IdentityData, plant.Source);
            Add("Sportify_SourceUrl", SpecTypeId.String.Text, GroupTypeId.IdentityData, plant.SourceUrl);
            AddLength("Sportify_MatureHeightM", plant.HeightM);
            AddLength("Sportify_CrownM", plant.CrownM);
            AddLength("Sportify_MinSubstrateM", plant.MinSubstrateMm / 1000.0);

            void Add(string name, ForgeTypeId spec, ForgeTypeId group, string? value)
            {
                if (string.IsNullOrWhiteSpace(value)) return;
                try
                {
                    var p = fm.get_Parameter(name) ?? fm.AddParameter(name, group, spec, false);
                    fm.Set(p, value);
                }
                catch (Exception) { /* identity data must never fail a family */ }
            }

            void AddLength(string name, double metres)
            {
                if (metres <= 0) return;
                try
                {
                    var p = fm.get_Parameter(name) ?? fm.AddParameter(name, GroupTypeId.Geometry, SpecTypeId.Length, false);
                    fm.Set(p, UnitUtils.ConvertToInternalUnits(metres, UnitTypeId.Meters));
                }
                catch (Exception) { }
            }
        }

        private static string CachePath(string familyName)
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Autodesk", "Revit", "Addins", "2025", "SportifyGeneratedFamilies",
                SanitizeFileName(familyName) + ".rfa");
        }

        /// <summary>
        /// Answers Revit's "this family is already loaded" prompt in code, so a
        /// re-import never stops to ask. Its own copy rather than a shared one:
        /// the two existing versions are private to their own builders, and a
        /// three-line policy object is not worth a shared abstraction.
        /// </summary>
        private sealed class OverwriteFamilyLoadOptions : IFamilyLoadOptions
        {
            public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues)
            {
                overwriteParameterValues = true;
                return true;
            }

            public bool OnSharedFamilyFound(Family sharedFamily, bool familyInUse,
                out FamilySource source, out bool overwriteParameterValues)
            {
                source = FamilySource.Family;
                overwriteParameterValues = true;
                return true;
            }
        }

        private static string SanitizeFileName(string name)
        {
            var cleaned = new string(name.Where(c => !Path.GetInvalidFileNameChars().Contains(c) && c != '\'').ToArray()).Trim();
            return cleaned.Length == 0 ? "Sportify plant" : cleaned;
        }
    }
}
