using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;

namespace SportfyRevit
{
    /// <summary>
    /// A football court in Revit — futsal, small-sided, or a mini pitch.
    ///
    /// The fourth court to build itself, and the same shape of problem as
    /// basketball: a flat surface where almost everything worth modelling is a
    /// line painted on it. Two objects stand up — the goals — plus the rebound
    /// boards when the court has them.
    ///
    /// ── The penalty area is the thing to get right ──
    /// Futsal's is NOT a rectangle. FIFA Futsal Law 1: a quarter circle of 6 m
    /// radius is struck from the outside of each goalpost into the field, and
    /// the two are joined by a line parallel to the goal line. That curve is
    /// what makes a futsal court recognisable, and squaring it off would be
    /// wrong rather than simplified — so this builder grows an arc-band helper
    /// the other courts did not need.
    ///
    /// ── Why there is no size variant ──
    /// The court type carries the size. A futsal court is 40 x 20 m because
    /// Law 1 says so, so the web app offers a court type and no variant, and
    /// what arrives here is already the right size.
    /// </summary>
    internal static class SportifyFootballCourtBuilder
    {
        private static readonly Dictionary<string, ElementId> SessionCache = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Bump when the geometry or the materials change — see the padel builder.</summary>
        private const string BuilderVersion = "v1";

        private const double SurfaceThicknessM = 0.030;   // turf carpet plus infill reads thicker than paint
        private const double MarkingThicknessM = 0.003;
        private const double GoalPostM = 0.10;
        private const double BoardThicknessM = 0.08;
        private const double MarkDiameterM = 0.10;        // the penalty marks

        public static FamilySymbol? GetOrCreateSymbol(Document doc, FootballDto court)
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
                    ImportDiagnostics.ExplicitFailed(familyName, "the generated football family did not load");
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

        private static void GenerateAndSave(Application app, FootballDto c, string familyName, string rfaPath)
        {
            string? templatePath = GenericFamilyTemplateLocator.Resolve();
            if (templatePath == null)
                throw new InvalidOperationException("Couldn't locate Revit's \"Metric Generic Model\" family template.");

            var familyDoc = app.NewFamilyDocument(templatePath);
            try
            {
                using (var t = new Transaction(familyDoc, "Build Sportify football court"))
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
            public ElementId Marking = ElementId.InvalidElementId;
            public ElementId Steel = ElementId.InvalidElementId;
            public ElementId Board = ElementId.InvalidElementId;
        }

        private static CourtMaterials BuildMaterials(Document fam, FootballDto c)
        {
            var surf = ParseHex(c.AppearanceHex) ?? (0x3F, 0x7D, 0x3A);
            // Turf is matt; a poured surface has a sheen. The difference is
            // visible in a rendered view and costs nothing to state.
            int shine = (c.Texture ?? "pile") switch { "sheen" => 30, "tiles" => 16, _ => 6 };
            return new CourtMaterials
            {
                Surface = Make(fam, SurfaceMaterialName(c), surf, 0, shine),
                Marking = Make(fam, "Sportify - Court line marking, white", (0xF2, 0xF2, 0xF2), 0, 8),
                Steel   = Make(fam, "Sportify - Goal frame, steel", (0xD8, 0xDC, 0xE0), 0, 80),
                Board   = Make(fam, "Sportify - Rebound board, timber", (0x8A, 0x5A, 0x2B), 0, 20),
            };
        }

        private static string SurfaceMaterialName(FootballDto c)
        {
            string what = (c.Surface ?? "artificial_turf") switch
            {
                "needle_punch" => "Needle-punch turf",
                "polyurethane" => "Poured polyurethane sports surface",
                "tiles"        => "Modular sports tiles",
                _              => "Artificial turf, 3G",
            };
            string colour = string.IsNullOrWhiteSpace(c.CourtColour) ? "" : $", {c.CourtColour}";
            return $"Sportify - {what}{colour}";
        }

        /// <summary>
        /// Long axis along X, origin at the centre of the footprint — the same
        /// convention the other three courts use, so a court rotated on the
        /// canvas arrives rotated the same way.
        /// </summary>
        private static void BuildCourt(Document fam, FootballDto c)
        {
            var m = BuildMaterials(fam, c);

            double areaL = c.LengthM > 0 ? c.LengthM : 44;
            double areaW = c.WidthM > 0 ? c.WidthM : 24;
            double playL = c.PlayLengthM > 0 ? c.PlayLengthM : 40;
            double playW = c.PlayWidthM > 0 ? c.PlayWidthM : 20;

            double halfAL = areaL / 2, halfAW = areaW / 2;
            double halfPL = playL / 2, halfPW = playW / 2;

            double lw = c.LineWidthM > 0 ? c.LineWidthM : 0.08;
            double top = MarkingThicknessM;

            // The surface covers the whole footprint, run-off included: it is all
            // one laid surface, and the run-off is where a player stops, not a
            // different material.
            Box(fam, -halfAL, -halfAW, halfAL, halfAW, -SurfaceThicknessM, 0, m.Surface);

            int failed = 0;
            void Try(Action a) { try { a(); } catch { failed++; } }

            // ── The lines every football court has ──
            Try(() => RectRing(fam, -halfPL, -halfPW, halfPL, halfPW, lw, 0, top, m.Marking));
            Try(() => Box(fam, -lw / 2, -halfPW, lw / 2, halfPW, 0, top, m.Marking));
            Try(() => CircleRing(fam, 0, 0, c.CentreCircleRadiusM > 0 ? c.CentreCircleRadiusM : 3.0,
                                 lw, 0, top, m.Marking));
            Try(() => CircleRing(fam, 0, 0, MarkDiameterM / 2, MarkDiameterM / 2, 0, top, m.Marking));

            double gHalf = (c.GoalWidthM > 0 ? c.GoalWidthM : 3.0) / 2;

            // ── The futsal markings ──
            if (!string.Equals(c.Markings, "simple", StringComparison.OrdinalIgnoreCase))
            {
                double r = c.PenaltyArcRadiusM > 0 ? c.PenaltyArcRadiusM : 6.0;
                double pen = c.PenaltyMarkM > 0 ? c.PenaltyMarkM : 6.0;
                double pen2 = c.SecondPenaltyMarkM > 0 ? c.SecondPenaltyMarkM : 10.0;

                // Each end: a quarter circle from each post, joined across.
                // The angles are spelled out per end rather than mirrored with a
                // sign, because an arc's direction is not symmetric and getting
                // it wrong draws the D inside out.
                foreach (var (goalX, into) in new[] { (-halfPL, 1.0), (halfPL, -1.0) })
                {
                    double a0 = into > 0 ? 270 : 180, a1 = into > 0 ? 360 : 270;   // post on -Y
                    double b0 = into > 0 ? 0 : 90, b1 = into > 0 ? 90 : 180;       // post on +Y
                    Try(() => ArcBand(fam, goalX, -gHalf, r, lw, a0, a1, 0, top, m.Marking));
                    Try(() => ArcBand(fam, goalX, gHalf, r, lw, b0, b1, 0, top, m.Marking));
                    Try(() => Box(fam, goalX + into * r - lw / 2, -gHalf,
                                       goalX + into * r + lw / 2, gHalf, 0, top, m.Marking));

                    Try(() => CircleRing(fam, goalX + into * pen, 0, MarkDiameterM / 2, MarkDiameterM / 2, 0, top, m.Marking));
                    Try(() => CircleRing(fam, goalX + into * pen2, 0, MarkDiameterM / 2, MarkDiameterM / 2, 0, top, m.Marking));
                }

                // Corner arcs.
                double ca = c.CornerArcRadiusM > 0 ? c.CornerArcRadiusM : 0.25;
                Try(() => ArcBand(fam, -halfPL, -halfPW, ca, lw, 0, 90, 0, top, m.Marking));
                Try(() => ArcBand(fam, -halfPL, halfPW, ca, lw, 270, 360, 0, top, m.Marking));
                Try(() => ArcBand(fam, halfPL, -halfPW, ca, lw, 90, 180, 0, top, m.Marking));
                Try(() => ArcBand(fam, halfPL, halfPW, ca, lw, 180, 270, 0, top, m.Marking));
            }

            // ── The goals: the only things that stand up ──
            double gh = c.GoalHeightM > 0 ? c.GoalHeightM : 2.0;
            foreach (var (goalX, outward) in new[] { (-halfPL, -1.0), (halfPL, 1.0) })
            {
                double x0 = Math.Min(goalX, goalX + outward * GoalPostM);
                double x1 = Math.Max(goalX, goalX + outward * GoalPostM);
                // Two posts and a crossbar.
                Try(() => Box(fam, x0, -gHalf - GoalPostM, x1, -gHalf, 0, gh, m.Steel));
                Try(() => Box(fam, x0, gHalf, x1, gHalf + GoalPostM, 0, gh, m.Steel));
                Try(() => Box(fam, x0, -gHalf - GoalPostM, x1, gHalf + GoalPostM, gh, gh + GoalPostM, m.Steel));
            }

            // ── Rebound boards, both touchlines ──
            if (string.Equals(c.Boards, "low", StringComparison.OrdinalIgnoreCase))
            {
                const double boardHeightM = 1.0;
                Try(() => Box(fam, -halfPL, -halfPW - BoardThicknessM, halfPL, -halfPW, 0, boardHeightM, m.Board));
                Try(() => Box(fam, -halfPL, halfPW, halfPL, halfPW + BoardThicknessM, 0, boardHeightM, m.Board));
            }

            if (failed > 0)
                ImportDiagnostics.ExplicitFailed(FamilyNameFor(c), $"{failed} marking(s) could not be drawn; the rest of the court was built");
        }

        /// <summary>
        /// A band following part of a circle — the futsal penalty area's quarter
        /// circles, and the corner arcs.
        ///
        /// CircleRing cannot do this: a partial ring is one closed loop of an
        /// outer arc, an end cap, the inner arc reversed, and the other cap, not
        /// two separate loops with a hole between them.
        /// </summary>
        private static void ArcBand(Document fam, double cxM, double cyM, double radiusM, double widthM,
                                    double startDeg, double endDeg, double zBaseM, double zTopM, ElementId? mat)
        {
            double rOut = radiusM + widthM / 2, rIn = radiusM - widthM / 2;
            if (rIn <= 0) return;

            double a0 = startDeg * Math.PI / 180.0, a1 = endDeg * Math.PI / 180.0;
            var centre = new XYZ(F(cxM), F(cyM), F(zBaseM));

            XYZ At(double r, double a) =>
                new XYZ(F(cxM + r * Math.Cos(a)), F(cyM + r * Math.Sin(a)), F(zBaseM));

            var outer = Arc.Create(centre, F(rOut), a0, a1, XYZ.BasisX, XYZ.BasisY);
            var inner = Arc.Create(centre, F(rIn), a0, a1, XYZ.BasisX, XYZ.BasisY);

            var loop = new CurveArray();
            loop.Append(outer);
            loop.Append(Line.CreateBound(At(rOut, a1), At(rIn, a1)));
            loop.Append(inner.CreateReversed());
            loop.Append(Line.CreateBound(At(rIn, a0), At(rOut, a0)));

            var profile = new CurveArrArray();
            profile.Append(loop);
            Extrude(fam, profile, zBaseM, zTopM, mat);
        }

        /* ── Geometry helpers, as the basketball builder ────────────────────── */

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

        private static void RectRing(Document fam, double x0M, double y0M, double x1M, double y1M,
                                     double widthM, double zBaseM, double zTopM, ElementId? mat)
        {
            double h = widthM / 2;
            var profile = new CurveArrArray();
            profile.Append(RectLoop(x0M - h, y0M - h, x1M + h, y1M + h, zBaseM));
            profile.Append(RectLoop(x0M + h, y0M + h, x1M - h, y1M - h, zBaseM));
            Extrude(fam, profile, zBaseM, zTopM, mat);
        }

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

        private static void StampParameters(FamilyManager fm, FootballDto c)
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

            Text("Sportify_Sport", "Football");
            Text("Sportify_CourtType", c.CourtType);
            Text("Sportify_Surface", c.Surface);
            Text("Sportify_CourtColour", c.CourtColour);
            Text("Sportify_Boards", c.Boards);
            Text("Sportify_Goals", c.Goals);
            Text("Sportify_PlayingArea", $"{c.PlayLengthM} x {c.PlayWidthM} m");
            Text("Sportify_ClearHeightMin", $"{c.ClearHeightMinM} m");
            Text("Sportify_WeightKg", Math.Round(c.WeightKg).ToString());
            Text("Sportify_WeightKgPerM2", c.WeightKgM2.ToString());
            Text("Sportify_Source", c.Source);
        }

        private static string FamilyNameFor(FootballDto c) =>
            SanitizeName($"Sportify - Football {c.CourtType ?? "futsal"} " +
                         $"{c.Surface ?? "artificial_turf"} {c.CourtColour ?? "green"} " +
                         $"{(string.Equals(c.Boards, "low", StringComparison.OrdinalIgnoreCase) ? "boarded" : "open")}");

        private static string CachePath(string familyName) =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                         "Sportify", "SportifyGeneratedFamilies", "football-" + BuilderVersion,
                         familyName + ".rfa");

        private static string SanitizeName(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars()
                         .Concat(new[] { '{', '}', '[', ']', '|', ';', '<', '>', '?', '`', '~' }))
                name = name.Replace(c, '-');
            return name.Trim();
        }

        private static double F(double metres) => UnitUtils.ConvertToInternalUnits(metres, UnitTypeId.Meters);

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
