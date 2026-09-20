using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;

namespace SportfyRevit
{
    /// <summary>
    /// A volleyball court in Revit.
    ///
    /// Third sport, third thing worth modelling. Padel's was the enclosure,
    /// basketball's the markings and the baskets. Volleyball's is the FREE ZONE
    /// and, on a beach court, the SAND.
    ///
    /// The free zone is built as real surface, not left as air. It is part of
    /// the court — a dig carries a player into it and it has to be the same
    /// ground — so a model showing only the 18 × 9 m of lines would understate
    /// the facility by more than half and let something else be placed in space
    /// that is already spoken for.
    ///
    /// The sand is built as a solid of its actual depth. 400 mm over a beach
    /// court and its run-off is around 120 m³ — roughly 200 tonnes — and that
    /// is a thing the deck carries, not a finish on top of it. Modelling it as
    /// a 3 mm skin would hide the only number that matters.
    /// </summary>
    internal static class SportifyVolleyballCourtBuilder
    {
        private static readonly Dictionary<string, ElementId> SessionCache = new(StringComparer.OrdinalIgnoreCase);

        private const string BuilderVersion = "v1";

        private const double SurfaceThicknessM = 0.025;
        private const double MarkingThicknessM = 0.003;
        private const double LineWidthM = 0.05;
        private const double PostSizeM = 0.10;
        private const double AntennaSizeM = 0.02;
        private const double NetCableM = 0.02;

        public static FamilySymbol? GetOrCreateSymbol(Document doc, VolleyballDto court)
        {
            string familyName = FamilyNameFor(court);

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
                    GenerateAndSave(doc.Application, court, familyName, rfaPath);

                doc.LoadFamily(rfaPath, new OverwriteFamilyLoadOptions(), out Family _);

                var symbol = new FilteredElementCollector(doc)
                    .OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
                    .FirstOrDefault(fs => fs.Family?.Name == familyName);
                if (symbol == null)
                {
                    ImportDiagnostics.ExplicitFailed(familyName, "the generated volleyball family did not load");
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

        private static void GenerateAndSave(Application app, VolleyballDto c, string familyName, string rfaPath)
        {
            string? templatePath = GenericFamilyTemplateLocator.Resolve();
            if (templatePath == null)
                throw new InvalidOperationException("Couldn't locate Revit's \"Metric Generic Model\" family template.");

            var familyDoc = app.NewFamilyDocument(templatePath);
            try
            {
                using (var t = new Transaction(familyDoc, "Build Sportify volleyball court"))
                {
                    t.Start();
                    BuildCourt(familyDoc, c);
                    StampParameters(familyDoc.FamilyManager, c);
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

        private sealed class CourtMaterials
        {
            public ElementId Surface = ElementId.InvalidElementId;
            public ElementId FreeZone = ElementId.InvalidElementId;
            public ElementId Marking = ElementId.InvalidElementId;
            public ElementId Steel = ElementId.InvalidElementId;
            public ElementId Net = ElementId.InvalidElementId;
            public ElementId Antenna = ElementId.InvalidElementId;
        }

        private static CourtMaterials BuildMaterials(Document fam, VolleyballDto c)
        {
            bool beach = string.Equals(c.PlayType, "beach", StringComparison.OrdinalIgnoreCase);
            var surf = ParseHex(c.AppearanceHex) ?? (beach ? (0xD8, 0xC1, 0x93) : (0x2F, 0x6F, 0xB5));

            return new CourtMaterials
            {
                Surface  = Make(fam, SurfaceMaterialName(c), surf, 0, beach ? 4 : 24),
                // The free zone reads slightly darker so the court still reads
                // as the court — it is the same material, not a different one.
                FreeZone = Make(fam, SurfaceMaterialName(c) + ", free zone", Darken(surf, 0.16), 0, beach ? 4 : 18),
                Marking  = Make(fam, "Sportify - Court line marking, white", (0xF2, 0xF2, 0xF2), 0, 8),
                Steel    = Make(fam, "Sportify - Net posts, steel", (0x6E, 0x74, 0x7C), 0, 72),
                Net      = Make(fam, "Sportify - Volleyball net", (0x2B, 0x2F, 0x36), 45, 10),
                // Antennae are red and white by rule, and they are what a
                // player aims inside — worth being able to see in the model.
                Antenna  = Make(fam, "Sportify - Net antenna", (0xE8, 0x43, 0x3F), 0, 40),
            };
        }

        private static string SurfaceMaterialName(VolleyballDto c)
        {
            string what = (c.Surface ?? "polyurethane") switch
            {
                "sand"         => $"Beach sand, {Math.Round((c.SandDepthM > 0 ? c.SandDepthM : 0.4) * 1000)} mm",
                "acrylic"      => "Acrylic sports surface",
                "tiles"        => "Modular sports tiles",
                _              => "Poured polyurethane sports surface",
            };
            string colour = string.IsNullOrWhiteSpace(c.CourtColour) ? "" : $", {c.CourtColour}";
            return $"Sportify - {what}{colour}";
        }

        /// <summary>
        /// Origin at the centre of the whole facility, long axis along X, so a
        /// court rotated on the canvas arrives rotated the same way.
        /// </summary>
        private static void BuildCourt(Document fam, VolleyballDto c)
        {
            var m = BuildMaterials(fam, c);
            bool beach = string.Equals(c.PlayType, "beach", StringComparison.OrdinalIgnoreCase);

            double fpL = c.LengthM > 0 ? c.LengthM : 24;
            double fpW = c.WidthM > 0 ? c.WidthM : 15;
            double cL = c.CourtLengthM > 0 ? c.CourtLengthM : 18;
            double cW = c.CourtWidthM > 0 ? c.CourtWidthM : 9;
            double halfFL = fpL / 2, halfFW = fpW / 2;
            double halfCL = cL / 2, halfCW = cW / 2;

            int failed = 0;
            void Try(Action a) { try { a(); } catch { failed++; } }

            // ── Ground ──
            // Sand is a solid of its real depth; a hard surface is a finish.
            // The difference is the whole reason a beach court is a structural
            // question, and flattening the two would hide it.
            double depth = beach ? (c.SandDepthM > 0 ? c.SandDepthM : 0.40) : SurfaceThicknessM;

            // Free zone first, then the court on top, so the court reads as the
            // court while both are genuinely the same ground.
            Try(() => Box(fam, -halfFL, -halfFW, halfFL, halfFW, -depth, 0, m.FreeZone));
            Try(() => Box(fam, -halfCL, -halfCW, halfCL, halfCW, -depth, MarkingThicknessM * 0.4, m.Surface));

            double top = MarkingThicknessM;

            // ── Markings ──
            Try(() => RectRing(fam, -halfCL, -halfCW, halfCL, halfCW, LineWidthM, 0, top, m.Marking));
            // Centre line, under the net
            Try(() => Box(fam, -LineWidthM / 2, -halfCW, LineWidthM / 2, halfCW, 0, top, m.Marking));

            // Attack lines — indoor only. A beach court has none, and drawing
            // them would make two different games look like one.
            if (!beach && c.AttackLineFromCentreM > 0)
            {
                foreach (double sx in new[] { -1.0, 1.0 })
                {
                    double ax = sx * c.AttackLineFromCentreM;
                    Try(() => Box(fam, ax - LineWidthM / 2, -halfCW, ax + LineWidthM / 2, halfCW, 0, top, m.Marking));
                }
            }

            // ── Net, posts and antennae ──
            double netH = c.NetHeightM > 0 ? c.NetHeightM : 2.43;
            double netDepth = c.NetDepthM > 0 ? c.NetDepthM : 1.0;
            double postOut = c.PostOutsideSidelineM > 0 ? c.PostOutsideSidelineM : 1.0;
            double postH = c.PostHeightM > 0 ? c.PostHeightM : 2.55;
            double antenna = c.AntennaLengthM > 0 ? c.AntennaLengthM : 1.8;
            double antennaUp = c.AntennaAboveNetM > 0 ? c.AntennaAboveNetM : 0.80;

            // The net hangs from its top band down by its own depth.
            Try(() => Box(fam, -NetCableM / 2, -halfCW - postOut, NetCableM / 2, halfCW + postOut,
                          Math.Max(0, netH - netDepth), netH, m.Net));

            foreach (double sy in new[] { -1.0, 1.0 })
            {
                double py = sy * (halfCW + postOut);
                Try(() => Box(fam, -PostSizeM / 2, py - PostSizeM / 2, PostSizeM / 2, py + PostSizeM / 2,
                              0, postH, m.Steel));
                // Antennae stand on the sidelines, not on the posts — they mark
                // the crossing space, which is narrower than the net.
                double ay = sy * halfCW;
                Try(() => Box(fam, -AntennaSizeM / 2, ay - AntennaSizeM / 2, AntennaSizeM / 2, ay + AntennaSizeM / 2,
                              netH + antennaUp - antenna, netH + antennaUp, m.Antenna));
            }

            if (failed > 0)
                ImportDiagnostics.Note($"Volleyball court: {failed} part(s) could not be built and were left off.");
        }

        /* ── Geometry helpers ──────────────────────────────────────────────── */

        private static double F(double m) => UnitUtils.ConvertToInternalUnits(m, UnitTypeId.Meters);

        private static void Extrude(Document fam, CurveArrArray profile, double zBaseM, double zTopM, ElementId? mat)
        {
            double h = zTopM - zBaseM;
            if (h <= 0) return;
            var plane = Plane.CreateByNormalAndOrigin(XYZ.BasisZ, new XYZ(0, 0, F(zBaseM)));
            var sketchPlane = SketchPlane.Create(fam, plane);
            var solid = fam.FamilyCreate.NewExtrusion(true, profile, sketchPlane, F(h));
            if (mat != null && mat != ElementId.InvalidElementId)
            {
                var mp = solid.get_Parameter(BuiltInParameter.MATERIAL_ID_PARAM);
                if (mp != null && !mp.IsReadOnly) mp.Set(mat);
            }
        }

        private static CurveArray RectLoop(double x0M, double y0M, double x1M, double y1M, double zM)
        {
            double x0 = F(Math.Min(x0M, x1M)), x1 = F(Math.Max(x0M, x1M));
            double y0 = F(Math.Min(y0M, y1M)), y1 = F(Math.Max(y0M, y1M));
            double z = F(zM);
            var loop = new CurveArray();
            loop.Append(Line.CreateBound(new XYZ(x0, y0, z), new XYZ(x1, y0, z)));
            loop.Append(Line.CreateBound(new XYZ(x1, y0, z), new XYZ(x1, y1, z)));
            loop.Append(Line.CreateBound(new XYZ(x1, y1, z), new XYZ(x0, y1, z)));
            loop.Append(Line.CreateBound(new XYZ(x0, y1, z), new XYZ(x0, y0, z)));
            return loop;
        }

        private static void Box(Document fam, double x0M, double y0M, double x1M, double y1M,
                                double zBaseM, double zTopM, ElementId? mat = null)
        {
            if (Math.Abs(x1M - x0M) < 1e-6 || Math.Abs(y1M - y0M) < 1e-6) return;
            var profile = new CurveArrArray();
            profile.Append(RectLoop(x0M, y0M, x1M, y1M, zBaseM));
            Extrude(fam, profile, zBaseM, zTopM, mat);
        }

        /// <summary>The line itself, not the area it encloses — two loops, one profile.</summary>
        private static void RectRing(Document fam, double x0M, double y0M, double x1M, double y1M,
                                     double widthM, double zBaseM, double zTopM, ElementId? mat)
        {
            double h = widthM / 2;
            var profile = new CurveArrArray();
            profile.Append(RectLoop(x0M - h, y0M - h, x1M + h, y1M + h, zBaseM));
            profile.Append(RectLoop(x0M + h, y0M + h, x1M - h, y1M - h, zBaseM));
            Extrude(fam, profile, zBaseM, zTopM, mat);
        }

        /* ── Materials, parameters, caching ────────────────────────────────── */

        private static (int, int, int) Darken((int R, int G, int B) c, double t) =>
            ((int)(c.R * (1 - t)), (int)(c.G * (1 - t)), (int)(c.B * (1 - t)));

        private static ElementId Make(Document fam, string name, (int R, int G, int B) rgb,
                                      int transparency, int shininess)
        {
            string safe = SanitizeName(name);
            var existing = new FilteredElementCollector(fam).OfClass(typeof(Material))
                .Cast<Material>().FirstOrDefault(x => x.Name.Equals(safe, StringComparison.OrdinalIgnoreCase));
            if (existing != null) return existing.Id;
            try
            {
                var id = Material.Create(fam, safe);
                if (fam.GetElement(id) is Material mat)
                {
                    mat.Color = new Autodesk.Revit.DB.Color((byte)rgb.R, (byte)rgb.G, (byte)rgb.B);
                    mat.Transparency = Math.Max(0, Math.Min(100, transparency));
                    mat.Shininess = Math.Max(0, Math.Min(128, shininess));
                    mat.SurfaceForegroundPatternColor = new Autodesk.Revit.DB.Color((byte)rgb.R, (byte)rgb.G, (byte)rgb.B);
                }
                return id;
            }
            catch { return ElementId.InvalidElementId; }
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

        private static void StampParameters(FamilyManager fm, VolleyballDto c)
        {
            void Text(string name, string? value)
            {
                if (string.IsNullOrWhiteSpace(value)) return;
                try
                {
                    var fp = fm.AddParameter(name, GroupTypeId.IdentityData, SpecTypeId.String.Text, false);
                    fm.Set(fp, value);
                }
                catch { }
            }

            Text("Sportify_Sport", "Volleyball");
            Text("Sportify_PlayType", c.PlayType);
            Text("Sportify_CourtVariant", c.Variant);
            Text("Sportify_CourtSizeM", $"{c.CourtLengthM:0.##} x {c.CourtWidthM:0.##}");
            Text("Sportify_FootprintM", $"{c.LengthM:0.##} x {c.WidthM:0.##}");
            Text("Sportify_FreeZoneM", $"{c.FreeZoneSidesM:0.##} sides / {c.FreeZoneEndsM:0.##} ends");
            Text("Sportify_NetHeightM", c.NetHeightM.ToString("0.00"));
            Text("Sportify_Surface", c.Surface);
            Text("Sportify_ClearHeightMinM", c.ClearHeightMinM.ToString("0.##"));
            if (c.SandVolumeM3 > 0)
            {
                Text("Sportify_SandVolumeM3", c.SandVolumeM3.ToString("0.#"));
                Text("Sportify_SandDepthMm", Math.Round(c.SandDepthM * 1000).ToString("0"));
            }
            Text("Sportify_WeightKg", c.WeightKg.ToString("0"));
            Text("Sportify_WeightKgM2", c.WeightKgM2.ToString("0.#"));
            Text("Sportify_WeightBasis", c.WeightBasis ?? "estimated");
            Text("Sportify_Source", c.Source);
        }

        private static string FamilyNameFor(VolleyballDto c) =>
            SanitizeName($"Sportify - Volleyball {c.PlayType ?? "indoor"} {c.Variant ?? "standard"} " +
                         $"net{c.NetHeightM:0.00} {c.Surface ?? "pu"} {c.CourtColour ?? "blue"}");

        private static string CachePath(string familyName) =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                         "Sportify", "SportifyGeneratedFamilies", "volleyball-" + BuilderVersion,
                         familyName + ".rfa");

        private static string SanitizeName(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars()
                         .Concat(new[] { '{', '}', '[', ']', '|', ';', '<', '>', '?', '`', '~' }))
                name = name.Replace(c, '-');
            return name.Trim();
        }

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
    }
}
