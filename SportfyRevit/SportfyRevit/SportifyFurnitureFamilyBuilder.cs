using System.IO;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;

namespace SportfyRevit
{
    /// <summary>
    /// Builds a real Revit family per catalogue product of site furniture (a bench, a table, a bin, a bollard, a light).
    ///
    /// Furniture pieces have no quality_key, so the generic family generator never saw them and every bench was a labelled box. A product has a published size and (usually) a
    /// weight, and one product is placed many times, so it is a family of its own, made once: its solids come from FurnitureShape (a bench is a seat, a backrest and two end
    /// frames, a table a top on four legs, a bin a cylinder with a lid, a bollard a post, a light a base, a pole and a head), sized exactly from the catalogue, centred on the
    /// family origin because every instance is placed at the placement's centre point. The product's identity, size, seats, capacity and weight are written onto the family so the
    /// model can be scheduled without the web app.
    ///
    /// Which family a product gets, in order: a family the project already has under the export's own name (revit_family_name: a firm that modelled the real product wins outright);
    /// the family this builder made for exactly this size (named with the size in centimetres, so a product whose size changed in the catalogue is a new family and not the old one);
    /// a new one. Like the plant builder it never starts a transaction of its own: FamilyPreparation gives it one per family.
    /// </summary>
    internal static class SportifyFurnitureFamilyBuilder
    {
        private static readonly Dictionary<string, ElementId> SessionCache = new(StringComparer.OrdinalIgnoreCase);

        public static FamilySymbol? GetOrCreateSymbol(Document doc, FurnitureDto f, out bool firmsOwn)
        {
            firmsOwn = false;
            if (f.LengthM <= 0 || f.WidthM <= 0 || f.HeightM <= 0)
                throw new InvalidOperationException($"the product \"{f.Product ?? f.Key}\" has no usable size ({f.LengthM} x {f.WidthM} x {f.HeightM} m)");

            // 1. The firm's own family of this product, under the name the export gives it.
            if (!string.IsNullOrWhiteSpace(f.RevitFamilyName))
            {
                var own = FindLoaded(doc, f.RevitFamilyName!.Trim());
                if (own != null) { firmsOwn = true; return Activate(own); }
            }

            // 2. The family made for exactly this size.
            var familyName = FurnitureShape.FamilyName(f.RevitFamilyName, f.Label ?? f.Product, f.Key, f.LengthM, f.WidthM, f.HeightM);
            string sessionKey = (doc.PathName ?? doc.Title) + "::furniture::" + familyName;
            if (SessionCache.TryGetValue(sessionKey, out var cachedId) && doc.GetElement(cachedId) is FamilySymbol cached) return cached;
            var loaded = FindLoaded(doc, familyName);
            if (loaded != null) { SessionCache[sessionKey] = loaded.Id; return Activate(loaded); }

            // 3. Build it (cached on disk: building a family document is real work).
            string rfaPath = CachePath(familyName);
            if (!File.Exists(rfaPath)) GenerateAndSave(doc.Application, f, familyName, rfaPath);

            if (!doc.LoadFamily(rfaPath, new OverwriteFamilyLoadOptions(), out Family family) && family == null)
                throw new InvalidOperationException($"Revit would not load the generated family \"{familyName}\" from {rfaPath}");
            var symbol = family?.GetFamilySymbolIds().Select(id => doc.GetElement(id) as FamilySymbol).FirstOrDefault(s => s != null);
            if (symbol == null) throw new InvalidOperationException($"the generated family \"{familyName}\" has no type");
            SessionCache[sessionKey] = symbol.Id;
            return Activate(symbol);
        }

        private static FamilySymbol? FindLoaded(Document doc, string familyName) =>
            new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
                .FirstOrDefault(s => string.Equals(s.Family?.Name, familyName, StringComparison.OrdinalIgnoreCase));

        private static FamilySymbol Activate(FamilySymbol symbol)
        {
            if (!symbol.IsActive) symbol.Activate();
            return symbol;
        }

        private static void GenerateAndSave(Application app, FurnitureDto f, string familyName, string rfaPath)
        {
            string? templatePath = GenericFamilyTemplateLocator.Resolve();
            if (templatePath == null)
                throw new InvalidOperationException("no Generic Model family template was found (" + (GenericFamilyTemplateLocator.FailureReason ?? "see the log") + ")");

            var familyDoc = app.NewFamilyDocument(templatePath);
            try
            {
                using (var t = new Transaction(familyDoc, "Build Sportify furniture family"))
                {
                    t.Start();
                    foreach (var part in FurnitureShape.Parts(f.Category, f.LengthM, f.WidthM, f.HeightM)) Extrude(familyDoc, part);
                    StampParameters(familyDoc.FamilyManager, f);
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

        private static double Ft(double metres) => UnitUtils.ConvertToInternalUnits(metres, UnitTypeId.Meters);

        private static void Extrude(Document familyDoc, ShapePart part)
        {
            double z0 = Ft(part.Z0), z1 = Ft(part.Z1);
            double height = z1 - z0;
            if (height <= 0) return;

            var profile = new CurveArrArray();
            var loop = new CurveArray();
            if (part.IsCylinder)
            {
                double r = Ft(Math.Min(part.X1 - part.X0, part.Y1 - part.Y0) / 2.0), cx = Ft((part.X0 + part.X1) / 2.0), cy = Ft((part.Y0 + part.Y1) / 2.0);
                var centre = new XYZ(cx, cy, z0);
                // two half-arcs: Revit rejects a full circle as one closed curve in an extrusion profile
                loop.Append(Arc.Create(centre, r, 0, Math.PI, XYZ.BasisX, XYZ.BasisY));
                loop.Append(Arc.Create(centre, r, Math.PI, 2 * Math.PI, XYZ.BasisX, XYZ.BasisY));
            }
            else
            {
                double x0 = Ft(part.X0), x1 = Ft(part.X1), y0 = Ft(part.Y0), y1 = Ft(part.Y1);
                loop.Append(Line.CreateBound(new XYZ(x0, y0, z0), new XYZ(x1, y0, z0)));
                loop.Append(Line.CreateBound(new XYZ(x1, y0, z0), new XYZ(x1, y1, z0)));
                loop.Append(Line.CreateBound(new XYZ(x1, y1, z0), new XYZ(x0, y1, z0)));
                loop.Append(Line.CreateBound(new XYZ(x0, y1, z0), new XYZ(x0, y0, z0)));
            }
            profile.Append(loop);
            var plane = Plane.CreateByNormalAndOrigin(XYZ.BasisZ, new XYZ(0, 0, z0));
            var sketchPlane = SketchPlane.Create(familyDoc, plane);
            familyDoc.FamilyCreate.NewExtrusion(true, profile, sketchPlane, height);
        }

        /// <summary>The product's identity and figures, on the family: schedulable without the web app. A parameter that cannot be added is skipped, never a reason to lose the family.</summary>
        private static void StampParameters(FamilyManager fm, FurnitureDto f)
        {
            fm.NewType(f.Product ?? f.Label ?? f.Key ?? "Sportify furniture");
            Text("Sportify_FurnitureKey", f.Key);
            Text("Sportify_Manufacturer", f.Manufacturer);
            Text("Sportify_Product", f.Product);
            Text("Sportify_FurnitureCategory", f.Category);
            Text("Sportify_Material", f.Material);
            Text("Sportify_SourceUrl", f.SourceUrl);
            Number("Sportify_LengthM", SpecTypeId.Length, GroupTypeId.Geometry, Ft(f.LengthM));
            Number("Sportify_WidthM", SpecTypeId.Length, GroupTypeId.Geometry, Ft(f.WidthM));
            Number("Sportify_HeightM", SpecTypeId.Length, GroupTypeId.Geometry, Ft(f.HeightM));
            if (f.WeightKg is > 0) Number("Sportify_WeightKg", SpecTypeId.Mass, GroupTypeId.Data, UnitUtils.ConvertToInternalUnits(f.WeightKg.Value, UnitTypeId.Kilograms));
            if (f.Seats > 0) Number("Sportify_Seats", SpecTypeId.Number, GroupTypeId.Data, f.Seats);
            if (f.CapacityL is > 0) Number("Sportify_CapacityLitres", SpecTypeId.Number, GroupTypeId.Data, f.CapacityL.Value);

            void Text(string name, string? value)
            {
                if (string.IsNullOrWhiteSpace(value)) return;
                try { var p = fm.get_Parameter(name) ?? fm.AddParameter(name, GroupTypeId.IdentityData, SpecTypeId.String.Text, false); fm.Set(p, value); }
                catch (Exception ex) { SportifyLog.Warn("families", $"furniture parameter {name} not written: {ex.Message}"); }
            }
            void Number(string name, ForgeTypeId spec, ForgeTypeId group, double internalValue)
            {
                try { var p = fm.get_Parameter(name) ?? fm.AddParameter(name, group, spec, false); fm.Set(p, internalValue); }
                catch (Exception ex) { SportifyLog.Warn("families", $"furniture parameter {name} not written: {ex.Message}"); }
            }
        }

        private static string CachePath(string familyName) => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Autodesk", "Revit", "Addins", "2025", "SportifyGeneratedFamilies", SanitizeFileName(familyName) + ".rfa");

        private static string SanitizeFileName(string name)
        {
            var cleaned = new string(name.Where(c => !Path.GetInvalidFileNameChars().Contains(c) && c != '\'').ToArray()).Trim();
            return cleaned.Length == 0 ? "Sportify furniture" : cleaned;
        }

        /// <summary>Answers Revit's "this family is already loaded" prompt in code, so a re-import never stops to ask.</summary>
        private sealed class OverwriteFamilyLoadOptions : IFamilyLoadOptions
        {
            public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues) { overwriteParameterValues = true; return true; }
            public bool OnSharedFamilyFound(Family sharedFamily, bool familyInUse, out FamilySource source, out bool overwriteParameterValues)
            {
                source = FamilySource.Family;
                overwriteParameterValues = true;
                return true;
            }
        }
    }
}
