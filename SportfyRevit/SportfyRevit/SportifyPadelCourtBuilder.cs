using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;

namespace SportfyRevit
{
    /// <summary>
    /// A padel court as the thing it is, rather than an extrusion of its
    /// footprint.
    ///
    /// Every other placement generates a flat slab the size of its plan, which
    /// is honest for a yoga deck and wrong for a court enclosed by 4 m of glass
    /// and mesh. A padel court is mostly vertical: the back walls, the stepped
    /// side panels and the net are what make it recognisable, and they are the
    /// parts a wind or shading study actually needs.
    ///
    /// Built from the figures carried on the placement, not from constants
    /// here. A court configured last month should rebuild as the court it was
    /// even if the FIP revises the rules, and the geometry is the web app's
    /// statement about what was specified.
    ///
    /// One family per distinct specification — court type, wall system, surface
    /// and colour — cached in memory for the session and on disk between them,
    /// the same two layers SportifyPlantFamilyBuilder uses. Two courts with the
    /// same options share a family; a blue turf court and a terracotta acrylic
    /// one do not, because they are not the same object.
    /// </summary>
    internal static class SportifyPadelCourtBuilder
    {
        private static readonly Dictionary<string, ElementId> SessionCache = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Drawn thickness for a mesh panel — the mesh itself has no meaningful one.</summary>
        private const double MeshThicknessM = 0.03;
        private const double PostSizeM = 0.08;
        private const double NetThicknessM = 0.02;
        private const double SurfaceThicknessM = 0.025;

        public static FamilySymbol? GetOrCreateSymbol(Document doc, PadelDto padel)
        {
            string familyName = FamilyNameFor(padel);

            if (SessionCache.TryGetValue(familyName, out var cachedId))
            {
                if (doc.GetElement(cachedId) is FamilySymbol cached) return Activate(cached);
                SessionCache.Remove(familyName);
            }

            var existing = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
                .FirstOrDefault(fs => fs.Family?.Name == familyName);
            if (existing != null)
            {
                SessionCache[familyName] = existing.Id;
                return Activate(existing);
            }

            string rfaPath = CachePath(familyName);
            try
            {
                if (!File.Exists(rfaPath))
                    GenerateAndSave(doc.Application, padel, familyName, rfaPath);

                // LoadFamily returns false when the family is already in the
                // document; the out parameter still gives it, so the name lookup
                // below is what actually decides.
                doc.LoadFamily(rfaPath, new OverwriteFamilyLoadOptions(), out Family loaded);

                var symbol = new FilteredElementCollector(doc)
                    .OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
                    .FirstOrDefault(fs => fs.Family?.Name == familyName);
                if (symbol == null)
                {
                    ImportDiagnostics.ExplicitFailed(familyName, "the generated padel family did not load");
                    return null;
                }
                SessionCache[familyName] = symbol.Id;
                return Activate(symbol);
            }
            catch (Exception ex)
            {
                ImportDiagnostics.ExplicitFailed(familyName, $"{ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        private static FamilySymbol Activate(FamilySymbol symbol)
        {
            if (!symbol.IsActive) symbol.Activate();
            return symbol;
        }

        private static void GenerateAndSave(Application app, PadelDto p, string familyName, string rfaPath)
        {
            string? templatePath = GenericFamilyTemplateLocator.Resolve();
            if (templatePath == null)
                throw new InvalidOperationException("Couldn't locate Revit's \"Metric Generic Model\" family template.");

            var familyDoc = app.NewFamilyDocument(templatePath);
            try
            {
                using (var t = new Transaction(familyDoc, "Build Sportify padel court"))
                {
                    t.Start();
                    BuildCourt(familyDoc, p);
                    StampParameters(familyDoc.FamilyManager, p);
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
        /// The court, centred on the family origin because FamilyPlacementBuilder
        /// places every instance at the placement's centre point.
        ///
        /// Laid out along X: the net at x = 0, the back walls at ±length/2.
        /// That matches the web app's drawing, so a court rotated on the canvas
        /// arrives rotated the same way.
        /// </summary>
        private static void BuildCourt(Document fam, PadelDto p)
        {
            double L = p.LengthM > 0 ? p.LengthM : 20;
            double W = p.WidthM > 0 ? p.WidthM : 10;
            double halfL = L / 2, halfW = W / 2;

            double glassT = Math.Max(0.008, (p.GlassThicknessMm > 0 ? p.GlassThicknessMm : 10) / 1000.0);
            double backGlassH = p.BackWallGlassHeightM > 0 ? p.BackWallGlassHeightM : 3.0;
            double backMeshH = p.BackWallMeshHeightM > 0 ? p.BackWallMeshHeightM : 1.0;
            double cornerLen = p.SideCornerGlass?.LengthM > 0 ? p.SideCornerGlass.LengthM : 2.0;
            double cornerH = p.SideCornerGlass?.HeightM > 0 ? p.SideCornerGlass.HeightM : 3.0;
            double stepLen = p.SideStepGlass?.LengthM > 0 ? p.SideStepGlass.LengthM : 2.0;
            double stepH = p.SideStepGlass?.HeightM > 0 ? p.SideStepGlass.HeightM : 2.0;
            double centreMeshH = p.SideCentreMeshHeightM > 0 ? p.SideCentreMeshHeightM : 3.0;
            double netH = p.NetCentreHeightM > 0 ? p.NetCentreHeightM : 0.88;
            double postH = p.NetPostHeightM > 0 ? p.NetPostHeightM : 0.92;

            var mats = BuildMaterials(fam, p);

            // ── Playing surface ──
            Box(fam, -halfL, -halfW, halfL, halfW, -SurfaceThicknessM, 0, mats.Surface);

            // ── Back walls: glass below, mesh above, full width ──
            foreach (double sx in new[] { -1.0, 1.0 })
            {
                double xOuter = sx * halfL;
                double xInner = xOuter - sx * glassT;
                double x0 = Math.Min(xOuter, xInner), x1 = Math.Max(xOuter, xInner);
                Box(fam, x0, -halfW, x1, halfW, 0, backGlassH, mats.Glass);
                Box(fam, x0, -halfW, x1, halfW, backGlassH, backGlassH + backMeshH, mats.Mesh);
            }

            // ── Side walls ──
            // Glass steps down from each corner, then mesh along the middle.
            // This stepped profile is the whole point of padel's geometry, and
            // it is the part a footprint extrusion cannot express.
            double meshStart = -halfL + cornerLen + stepLen;
            double meshEnd = halfL - cornerLen - stepLen;

            foreach (double sy in new[] { -1.0, 1.0 })
            {
                double yOuter = sy * halfW;
                double yInner = yOuter - sy * glassT;
                double y0 = Math.Min(yOuter, yInner), y1 = Math.Max(yOuter, yInner);

                foreach (double sx in new[] { -1.0, 1.0 })
                {
                    double cornerFrom = sx < 0 ? -halfL : halfL - cornerLen;
                    double stepFrom = sx < 0 ? -halfL + cornerLen : halfL - cornerLen - stepLen;

                    Box(fam, cornerFrom, y0, cornerFrom + cornerLen, y1, 0, cornerH, mats.Glass);
                    Box(fam, stepFrom, y0, stepFrom + stepLen, y1, 0, stepH, mats.Glass);

                    // Mesh fills back up to the corner height above both steps.
                    double meshY0 = sy < 0 ? yOuter : yOuter - MeshThicknessM;
                    double meshY1 = meshY0 + MeshThicknessM;
                    if (backGlassH + backMeshH > cornerH)
                        Box(fam, cornerFrom, meshY0, cornerFrom + cornerLen, meshY1, cornerH, backGlassH + backMeshH, mats.Mesh);
                    if (cornerH > stepH)
                        Box(fam, stepFrom, meshY0, stepFrom + stepLen, meshY1, stepH, cornerH, mats.Mesh);
                }

                // Centre run: mesh only, no glass.
                double cy0 = sy < 0 ? yOuter : yOuter - MeshThicknessM;
                Box(fam, meshStart, cy0, meshEnd, cy0 + MeshThicknessM, 0, centreMeshH, mats.Mesh);
            }

            // ── Net, across the court at the halfway line ──
            Box(fam, -NetThicknessM / 2, -halfW, NetThicknessM / 2, halfW, 0, netH, mats.Net);
            foreach (double sy in new[] { -1.0, 1.0 })
            {
                double py = sy * halfW;
                Box(fam, -PostSizeM / 2, py - PostSizeM / 2, PostSizeM / 2, py + PostSizeM / 2, 0, postH, mats.Steel);
            }

            // ── Posts where the frame really is: the four corners and each
            //    glass step joint. A panoramic court has fewer, which is the
            //    point of paying for one, so its intermediate posts are skipped.
            bool panoramic = string.Equals(p.WallSystem, "panoramic", StringComparison.OrdinalIgnoreCase);
            var postXs = new List<double> { -halfL, halfL };
            if (!panoramic) { postXs.Add(meshStart); postXs.Add(meshEnd); }

            double fullH = backGlassH + backMeshH;
            foreach (double px in postXs)
                foreach (double sy in new[] { -1.0, 1.0 })
                    Box(fam, px - PostSizeM / 2, sy * halfW - PostSizeM / 2,
                             px + PostSizeM / 2, sy * halfW + PostSizeM / 2, 0, fullH, mats.Steel);
        }


        /* ── Materials ───────────────────────────────────────────────────────
           Named before coloured. A model where every element says "Default" is
           useless for takeoff; one with consistent names is valuable even
           rendering entirely grey — and names are what survive IFC export,
           where appearance assets mostly do not.

           The names match the catalog, so a Revit material takeoff and the web
           app's receipt group by the same thing and cannot disagree.

           No physical properties: density and thermal only matter when Revit
           itself runs the analysis, and the weight is already computed from the
           parts and carried on the family. Asking Revit to re-derive a number
           we have, less accurately, buys nothing. */

        private sealed class CourtMaterials
        {
            public ElementId Glass = ElementId.InvalidElementId;
            public ElementId Mesh = ElementId.InvalidElementId;
            public ElementId Steel = ElementId.InvalidElementId;
            public ElementId Surface = ElementId.InvalidElementId;
            public ElementId Net = ElementId.InvalidElementId;
        }

        private static CourtMaterials BuildMaterials(Document fam, PadelDto p)
        {
            double glassMm = p.GlassThicknessMm > 0 ? p.GlassThicknessMm : 10;
            var surfaceRgb = ParseHex(p.AppearanceHex) ?? (0x2F, 0x6F, 0xB5);
            string surfaceName = SurfaceMaterialName(p);

            return new CourtMaterials
            {
                // Transparency is what makes a padel court read as one. The
                // enclosure is most of the object, and opaque it is a box.
                Glass   = Make(fam, $"Sportify - Tempered glass {glassMm:0}mm", (0xBF, 0xD9, 0xE8), 78, 96),
                Mesh    = Make(fam, "Sportify - Fence mesh, galvanised",        (0x8C, 0x93, 0x99), 55, 40),
                Steel   = Make(fam, "Sportify - Steel frame, galvanised",       (0x9A, 0xA0, 0xA6),  0, 72),
                Surface = Make(fam, surfaceName,                                surfaceRgb,          0, 12),
                Net     = Make(fam, "Sportify - Padel net",                     (0x35, 0x39, 0x40), 35, 10),
            };
        }

        /// <summary>The surface names the material it actually is, and its colour.</summary>
        private static string SurfaceMaterialName(PadelDto p)
        {
            string what = (p.Surface ?? "artificial_grass") switch
            {
                "concrete" => "Porous concrete",
                "acrylic"  => "Acrylic sports surface",
                _          => "Artificial grass, sand-filled",
            };
            string colour = string.IsNullOrWhiteSpace(p.SurfaceColour) ? "" : $", {p.SurfaceColour}";
            return $"Sportify - {what}{colour}";
        }

        private static ElementId Make(Document fam, string name, (int R, int G, int B) rgb,
                                      int transparency, int shininess)
        {
            string safe = SanitizeName(name);
            var existing = new FilteredElementCollector(fam).OfClass(typeof(Material))
                .Cast<Material>().FirstOrDefault(m => m.Name.Equals(safe, StringComparison.OrdinalIgnoreCase));
            if (existing != null) return existing.Id;

            try
            {
                var id = Material.Create(fam, safe);
                if (fam.GetElement(id) is Material m)
                {
                    // Fully qualified: UseWindowsForms pulls System.Drawing.Color
                    // into scope and the two are ambiguous otherwise.
                    m.Color = new Autodesk.Revit.DB.Color((byte)rgb.R, (byte)rgb.G, (byte)rgb.B);
                    m.Transparency = Math.Max(0, Math.Min(100, transparency));
                    m.Shininess = Math.Max(0, Math.Min(128, shininess));
                    m.SurfaceForegroundPatternColor = new Autodesk.Revit.DB.Color((byte)rgb.R, (byte)rgb.G, (byte)rgb.B);
                }
                return id;
            }
            catch (Exception)
            {
                // A material Revit will not take is not worth failing the court
                // over — the geometry is still right, it just arrives grey.
                return ElementId.InvalidElementId;
            }
        }

        private static (int, int, int)? ParseHex(string? hex)
        {
            if (string.IsNullOrWhiteSpace(hex)) return null;
            var h = hex.TrimStart('#');
            if (h.Length != 6) return null;
            try
            {
                return (Convert.ToInt32(h.Substring(0, 2), 16),
                        Convert.ToInt32(h.Substring(2, 2), 16),
                        Convert.ToInt32(h.Substring(4, 2), 16));
            }
            catch { return null; }
        }

        /// <summary>Revit rejects these in a material name, same list as a type name.</summary>
        private static string SanitizeName(string name)
        {
            foreach (char c in new[] { '{', '}', '[', ']', '|', ';', '<', '>', '?', '`', '~', ':', '\\', '/' })
                name = name.Replace(c, '-');
            return name.Trim();
        }

        /// <summary>A rectangular solid, in metres, extruded along Z.</summary>
        private static void Box(Document fam, double x0M, double y0M, double x1M, double y1M,
                                double zBaseM, double zTopM, ElementId? materialId = null)
        {
            double h = zTopM - zBaseM;
            if (h <= 0 || Math.Abs(x1M - x0M) < 1e-6 || Math.Abs(y1M - y0M) < 1e-6) return;

            double F(double m) => UnitUtils.ConvertToInternalUnits(m, UnitTypeId.Meters);
            double x0 = F(Math.Min(x0M, x1M)), x1 = F(Math.Max(x0M, x1M));
            double y0 = F(Math.Min(y0M, y1M)), y1 = F(Math.Max(y0M, y1M));
            double z = F(zBaseM);

            var loop = new CurveArray();
            loop.Append(Line.CreateBound(new XYZ(x0, y0, z), new XYZ(x1, y0, z)));
            loop.Append(Line.CreateBound(new XYZ(x1, y0, z), new XYZ(x1, y1, z)));
            loop.Append(Line.CreateBound(new XYZ(x1, y1, z), new XYZ(x0, y1, z)));
            loop.Append(Line.CreateBound(new XYZ(x0, y1, z), new XYZ(x0, y0, z)));

            var profile = new CurveArrArray();
            profile.Append(loop);

            var plane = Plane.CreateByNormalAndOrigin(XYZ.BasisZ, new XYZ(0, 0, z));
            var sketchPlane = SketchPlane.Create(fam, plane);
            var solid = fam.FamilyCreate.NewExtrusion(true, profile, sketchPlane, F(h));
            if (materialId != null && materialId != ElementId.InvalidElementId)
            {
                var mp = solid.get_Parameter(BuiltInParameter.MATERIAL_ID_PARAM);
                if (mp != null && !mp.IsReadOnly) mp.Set(materialId);
            }
        }

        /// <summary>
        /// The specification, on the family itself, so the court can be
        /// scheduled and checked without the web app.
        ///
        /// Weight is here because it is the first real load a sport puts on the
        /// deck, and clear height because a padel court needs 6 m of air above
        /// it — neither can be recovered from the geometry by anything
        /// downstream, and both decide whether the court can be there at all.
        /// </summary>
        private static void StampParameters(FamilyManager fm, PadelDto p)
        {
            void Text(string name, string? value)
            {
                if (string.IsNullOrWhiteSpace(value)) return;
                try
                {
                    var fp = fm.AddParameter(name, GroupTypeId.IdentityData, SpecTypeId.String.Text, false);
                    fm.Set(fp, value);
                }
                catch { /* a name Revit will not take is not worth failing the family over */ }
            }

            Text("Sportify_Sport", "Padel");
            Text("Sportify_CourtType", p.CourtType);
            Text("Sportify_WallSystem", p.WallSystem);
            Text("Sportify_Surface", p.Surface);
            Text("Sportify_SurfaceColour", p.SurfaceColour);
            Text("Sportify_CourtSizeM", $"{p.LengthM:0.##} x {p.WidthM:0.##}");
            Text("Sportify_ClearHeightMinM", p.ClearHeightMinM.ToString("0.##"));
            Text("Sportify_WeightKg", p.WeightKg.ToString("0"));
            Text("Sportify_WeightKgM2", p.WeightKgM2.ToString("0.#"));
            Text("Sportify_WeightBasis", p.WeightBasis ?? "estimated");
            Text("Sportify_Source", p.Source);
        }

        /// <summary>
        /// One family per distinct specification. Two courts configured the same
        /// way share it; a different surface or wall system is a different
        /// object and gets its own.
        /// </summary>
        private static string FamilyNameFor(PadelDto p) =>
            SanitizeFileName($"Sportify - Padel {p.CourtType ?? "double"} " +
                             $"{p.WallSystem ?? "panoramic"} {p.Surface ?? "grass"} {p.SurfaceColour ?? "blue"}");

        /// <summary>
        /// Bump when the geometry or the materials change.
        ///
        /// The cache exists because building a family document is a second of
        /// real work, and it is keyed on the specification — which does not
        /// change when this builder does. Without a version here, improving the
        /// court would silently keep handing out the old one, and the bug would
        /// look like the new code not working.
        ///
        /// v2: every part carries its own material.
        /// </summary>
        private const string BuilderVersion = "v2";

        private static string CachePath(string familyName) =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                         "Sportify", "SportifyGeneratedFamilies", BuilderVersion, familyName + ".rfa");

        /// <summary>
        /// Revit rejects these in a type or family name, and they are also
        /// illegal in a filename — the same list SportifyFloorTypeBuilder found
        /// the hard way.
        /// </summary>
        /// <summary>
        /// Regenerating a family the project already holds should replace it,
        /// not prompt. The fourth copy of this in the add-in — the three
        /// existing ones are private to their own builders, and pulling them
        /// into one shared type is a tidy-up for its own commit rather than a
        /// change smuggled into this one.
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
            foreach (char c in Path.GetInvalidFileNameChars().Concat(new[] { '{', '}', '[', ']', '|', ';', '<', '>', '?', '`', '~' }))
                name = name.Replace(c, '-');
            return name.Trim();
        }
    }
}
