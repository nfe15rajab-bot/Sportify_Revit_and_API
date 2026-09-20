using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;

namespace SportfyRevit
{
    /// <summary>
    /// A basketball court in Revit.
    ///
    /// The opposite problem to padel. Padel is an enclosure and its value in 3D
    /// is the glass box; basketball is flat, and the two things worth modelling
    /// are the markings — which are what make it a basketball court rather than
    /// a coloured rectangle — and the baskets, which are the only objects on it
    /// and the only part a wind, shading or clearance study can see.
    ///
    /// ── Every marking is its own extrusion, on purpose ──
    /// A dozen thin solids could be one merged profile, and then one bad arc
    /// would cost the whole family. Each is attempted separately and a failure
    /// is counted, not thrown: a court missing its no-charge semicircle is
    /// still a court, and this is the first code here that builds arcs and
    /// bands it has never watched Revit accept.
    ///
    /// Geometry comes from the placement, not from constants here — the same
    /// rule padel follows, so a court configured last month rebuilds as the
    /// court it was.
    /// </summary>
    internal static class SportifyBasketballCourtBuilder
    {
        private static readonly Dictionary<string, ElementId> SessionCache = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Bump when the geometry or the materials change — see the padel builder.</summary>
        private const string BuilderVersion = "v1";

        private const double SurfaceThicknessM = 0.025;
        private const double MarkingThicknessM = 0.003;   // paint, standing just proud of the surface
        private const double LineWidthM = 0.05;           // FIBA
        private const double PostSizeM = 0.15;
        private const double BackboardThicknessM = 0.03;
        private const double RimTubeM = 0.02;

        public static FamilySymbol? GetOrCreateSymbol(Document doc, BasketballDto court)
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
                    ImportDiagnostics.ExplicitFailed(familyName, "the generated basketball family did not load");
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

        private static void GenerateAndSave(Application app, BasketballDto c, string familyName, string rfaPath)
        {
            string? templatePath = GenericFamilyTemplateLocator.Resolve();
            if (templatePath == null)
                throw new InvalidOperationException("Couldn't locate Revit's \"Metric Generic Model\" family template.");

            var familyDoc = app.NewFamilyDocument(templatePath);
            try
            {
                using (var t = new Transaction(familyDoc, "Build Sportify basketball court"))
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
            public ElementId Key = ElementId.InvalidElementId;
            public ElementId Marking = ElementId.InvalidElementId;
            public ElementId Steel = ElementId.InvalidElementId;
            public ElementId Backboard = ElementId.InvalidElementId;
        }

        private static CourtMaterials BuildMaterials(Document fam, BasketballDto c)
        {
            var surf = ParseHex(c.AppearanceHex) ?? (0x2F, 0x6F, 0xB5);
            var key = ParseHex(c.KeyFillHex) ?? (0x9C, 0xC2, 0xE5);
            return new CourtMaterials
            {
                Surface   = Make(fam, SurfaceMaterialName(c), surf, 0, c.Surface == "polyurethane" ? 30 : 12),
                Key       = Make(fam, SurfaceMaterialName(c) + ", key", key, 0, 12),
                Marking   = Make(fam, "Sportify - Court line marking, white", (0xF2, 0xF2, 0xF2), 0, 8),
                Steel     = Make(fam, "Sportify - Basket structure, steel",   (0x6E, 0x74, 0x7C), 0, 72),
                // Backboards are acrylic or glass; transparent is what makes a
                // basket read as a basket rather than a signboard on a stick.
                Backboard = Make(fam, "Sportify - Backboard, acrylic",        (0xDC, 0xE8, 0xF2), 62, 90),
            };
        }

        private static string SurfaceMaterialName(BasketballDto c)
        {
            string what = (c.Surface ?? "acrylic") switch
            {
                "polyurethane" => "Poured polyurethane sports surface",
                "tiles"        => "Modular sports tiles",
                _              => "Acrylic sports surface",
            };
            string colour = string.IsNullOrWhiteSpace(c.CourtColour) ? "" : $", {c.CourtColour}";
            return $"Sportify - {what}{colour}";
        }

        /// <summary>
        /// Laid out with the long axis along X and the origin at the centre of
        /// the court, matching the web app's drawing, so a court rotated on the
        /// canvas arrives rotated the same way.
        /// </summary>
        private static void BuildCourt(Document fam, BasketballDto c)
        {
            var m = BuildMaterials(fam, c);

            double areaL = c.LengthM > 0 ? c.LengthM : 28;
            double areaW = c.WidthM > 0 ? c.WidthM : 15;
            double playL = c.PlayLengthM > 0 ? c.PlayLengthM : areaL;
            double playW = c.PlayWidthM > 0 ? c.PlayWidthM : areaW;

            double halfAL = areaL / 2, halfAW = areaW / 2;
            double halfPL = playL / 2, halfPW = playW / 2;

            double top = MarkingThicknessM;   // markings sit just proud of the surface

            // ── Surface, over the whole area including any free zone ──
            Box(fam, -halfAL, -halfAW, halfAL, halfAW, -SurfaceThicknessM, 0, m.Surface);

            int failed = 0;
            void Try(Action a) { try { a(); } catch { failed++; } }

            int hoops = c.Hoops > 0 ? c.Hoops : 2;
            bool half = hoops == 1;

            // ── Perimeter, centre line, centre circle ──
            Try(() => RectRing(fam, -halfPL, -halfPW, halfPL, halfPW, LineWidthM, 0, top, m.Marking));

            // On a half court the centre line IS the open edge, already drawn as
            // part of the perimeter — a line down the middle of a half court
            // would mark nothing. The centre circle is only the half that falls
            // inside, so on a half court it is left off rather than drawn whole.
            if (!half)
            {
                Try(() => Box(fam, -LineWidthM / 2, -halfPW, LineWidthM / 2, halfPW, 0, top, m.Marking));
                Try(() => CircleRing(fam, 0, 0, c.CentreCircleRadiusM > 0 ? c.CentreCircleRadiusM : 1.80,
                                     LineWidthM, 0, top, m.Marking));
            }

            // ── Per basket end ──
            var ends = half ? new[] { -1.0 } : new[] { -1.0, 1.0 };

            foreach (double sx in ends)
            {
                double endX = sx * halfPL;                       // the endline
                double dir = -sx;                                // into the court
                double keyDepth = c.KeyDepthM > 0 ? c.KeyDepthM : 5.80;
                double keyHalf = (c.KeyWidthM > 0 ? c.KeyWidthM : 4.90) / 2;
                double ftX = endX + dir * keyDepth;
                double basketX = endX + dir * (c.BasketCentreFromEndlineM > 0 ? c.BasketCentreFromEndlineM : 1.575);

                // The key as a colour block — the single thing that makes a
                // court read as basketball at a glance.
                if (!string.IsNullOrWhiteSpace(c.KeyFillHex))
                    Try(() => Box(fam, Math.Min(endX, ftX), -keyHalf, Math.Max(endX, ftX), keyHalf, 0, top * 0.6, m.Key));

                Try(() => RectRing(fam, Math.Min(endX, ftX), -keyHalf, Math.Max(endX, ftX), keyHalf,
                                   LineWidthM, 0, top, m.Marking));
                Try(() => CircleRing(fam, ftX, 0, c.FreeThrowCircleRadiusM > 0 ? c.FreeThrowCircleRadiusM : 1.80,
                                     LineWidthM, 0, top, m.Marking));
                Try(() => CircleRing(fam, basketX, 0, c.NoChargeRadiusM > 0 ? c.NoChargeRadiusM : 1.25,
                                     LineWidthM, 0, top, m.Marking));

                // Three-point: the straights, then the arc as a ring. Where the
                // straights end is not chosen — it falls out of the geometry.
                double tpR = c.ThreePointRadiusM > 0 ? c.ThreePointRadiusM : 6.75;
                double cornerY = halfPW - (c.ThreePointCornerFromSidelineM > 0 ? c.ThreePointCornerFromSidelineM : 0.90);
                double dx = Math.Sqrt(Math.Max(0, tpR * tpR - cornerY * cornerY));
                double junctionX = basketX + dir * dx;
                foreach (double sy in new[] { -1.0, 1.0 })
                {
                    double y = sy * cornerY;
                    Try(() => Box(fam, Math.Min(endX, junctionX), y - LineWidthM / 2,
                                       Math.Max(endX, junctionX), y + LineWidthM / 2, 0, top, m.Marking));
                }
                Try(() => CircleRing(fam, basketX, 0, tpR, LineWidthM, 0, top, m.Marking));

                // ── The basket ──
                double rim = c.RimHeightM > 0 ? c.RimHeightM : 3.05;
                double bbFace = endX + dir * (c.BackboardFaceFromEndlineM > 0 ? c.BackboardFaceFromEndlineM : 1.20);
                double bbW = c.BackboardWidthM > 0 ? c.BackboardWidthM : 1.80;
                double bbH = c.BackboardHeightM > 0 ? c.BackboardHeightM : 1.05;
                double bbLow = c.BackboardLowerEdgeM > 0 ? c.BackboardLowerEdgeM : 2.90;

                // Post outside the endline, where a real one stands — the
                // overhang between post and backboard is the whole reason a
                // basket needs room behind the court.
                double postX = endX - dir * 1.0;
                Try(() => Box(fam, postX - PostSizeM / 2, -PostSizeM / 2, postX + PostSizeM / 2, PostSizeM / 2,
                              0, bbLow + bbH, m.Steel));
                // Arm from post to backboard
                Try(() => Box(fam, Math.Min(postX, bbFace), -PostSizeM / 3, Math.Max(postX, bbFace), PostSizeM / 3,
                              bbLow + bbH - 0.25, bbLow + bbH, m.Steel));
                // Backboard
                Try(() => Box(fam, bbFace - BackboardThicknessM / 2, -bbW / 2, bbFace + BackboardThicknessM / 2, bbW / 2,
                              bbLow, bbLow + bbH, m.Backboard));
                // Ring
                Try(() => CircleRing(fam, basketX, 0, (c.RimInnerDiameterM > 0 ? c.RimInnerDiameterM : 0.45) / 2,
                                     RimTubeM, rim, rim + RimTubeM, m.Steel));
            }

            if (failed > 0)
                ImportDiagnostics.Note($"Basketball court: {failed} marking(s) could not be built and were left off.");
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

        /// <summary>
        /// A rectangular band — the line itself, not the area it encloses.
        /// Two loops in one profile: Revit takes the second as a hole.
        /// </summary>
        private static void RectRing(Document fam, double x0M, double y0M, double x1M, double y1M,
                                     double widthM, double zBaseM, double zTopM, ElementId? mat)
        {
            double h = widthM / 2;
            var profile = new CurveArrArray();
            profile.Append(RectLoop(x0M - h, y0M - h, x1M + h, y1M + h, zBaseM));
            profile.Append(RectLoop(x0M + h, y0M + h, x1M - h, y1M - h, zBaseM));
            Extrude(fam, profile, zBaseM, zTopM, mat);
        }

        /// <summary>
        /// An annulus. Two half-arcs per loop, because Revit rejects a full
        /// circle as one closed curve in a profile — the same thing the plant
        /// builder found.
        /// </summary>
        private static void CircleRing(Document fam, double cxM, double cyM, double radiusM,
                                       double widthM, double zBaseM, double zTopM, ElementId? mat)
        {
            double rOut = F(radiusM + widthM / 2), rIn = F(radiusM - widthM / 2);
            if (rIn <= 0) return;
            var centre = new XYZ(F(cxM), F(cyM), F(zBaseM));

            CurveArray Ring(double r)
            {
                var loop = new CurveArray();
                loop.Append(Arc.Create(centre, r, 0, Math.PI, XYZ.BasisX, XYZ.BasisY));
                loop.Append(Arc.Create(centre, r, Math.PI, 2 * Math.PI, XYZ.BasisX, XYZ.BasisY));
                return loop;
            }

            var profile = new CurveArrArray();
            profile.Append(Ring(rOut));
            profile.Append(Ring(rIn));
            Extrude(fam, profile, zBaseM, zTopM, mat);
        }

        /* ── Materials, parameters, caching — as the padel builder ──────────── */

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

        private static void StampParameters(FamilyManager fm, BasketballDto c)
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

            Text("Sportify_Sport", "Basketball");
            Text("Sportify_CourtVariant", c.Variant);
            Text("Sportify_Baskets", c.Hoops.ToString());
            Text("Sportify_BasketMounting", c.Mounting);
            Text("Sportify_Surface", c.Surface);
            Text("Sportify_CourtColour", c.CourtColour);
            Text("Sportify_CourtSizeM", $"{c.PlayLengthM:0.##} x {c.PlayWidthM:0.##}");
            Text("Sportify_ClearHeightMinM", c.ClearHeightMinM.ToString("0.##"));
            Text("Sportify_WeightKg", c.WeightKg.ToString("0"));
            Text("Sportify_WeightKgM2", c.WeightKgM2.ToString("0.#"));
            Text("Sportify_WeightBasis", c.WeightBasis ?? "estimated");
            Text("Sportify_Source", c.Source);
        }

        private static string FamilyNameFor(BasketballDto c) =>
            SanitizeName($"Sportify - Basketball {c.Variant ?? "standard"} {c.Hoops}-basket " +
                         $"{c.Mounting ?? "ballasted"} {c.Surface ?? "acrylic"} {c.CourtColour ?? "blue"}");

        private static string CachePath(string familyName) =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                         "Sportify", "SportifyGeneratedFamilies", "basketball-" + BuilderVersion,
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
