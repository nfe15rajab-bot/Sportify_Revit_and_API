using System.IO;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;

namespace SportfyRevit
{
    /// <summary>
    /// The calisthenics rig, built rather than loaded.
    ///
    /// The design team authors the planters, the trays and the trampoline, and
    /// for those we only set parameters. There is no calisthenics family, so
    /// this one is ours — the same position the padel court and the football
    /// pitch are in.
    ///
    /// It is a good thing to build rather than wait for, because it is honestly
    /// just pipes: a portal frame of posts and top rails with bars hung off it,
    /// and every single piece is a tube between two points. There is no moulded
    /// shape to get wrong and no manufacturer's product to misrepresent.
    ///
    /// ── Tubes, not boxes ──
    /// Everything here goes through Tube(), which extrudes a circle along the
    /// line between two points in any direction. A rig drawn as rectangular bars
    /// would read as scaffolding rather than as something you grip, and the
    /// grip diameter is the one dimension a user of the rig actually feels.
    ///
    /// Revit will not take a full circle as one closed curve in a profile, so
    /// each one is two half-arcs. That is the same thing the climbing tower ran
    /// into and it is a property of the API, not of the shape.
    ///
    /// ── The origin ──
    /// The family is built centred on X and Y and standing on Z=0, because that
    /// is what every other generated family here does and what the placement
    /// code expects: it hands over a centre point and the piece has to sit on
    /// the deck around it.
    /// </summary>
    internal static class SportifyCalisthenicsRigBuilder
    {
        private static readonly Dictionary<string, ElementId> SessionCache = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Bump when the geometry or the materials change — see the padel builder.</summary>
        private const string BuilderVersion = "v1";

        /// <summary>Wall thicknesses, matching what the web app weighs the steel as.</summary>
        private const double PostWallM = 0.004;
        private const double BasePlateM = 0.012;
        private const double BasePlateSideFactor = 2.2;   // plate side as a multiple of the post diameter

        public static FamilySymbol? GetOrCreateSymbol(Document doc, CalisthenicsDto rig)
        {
            string familyName = FamilyNameFor(rig);

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
                    GenerateAndSave(doc.Application, rig, rfaPath);

                doc.LoadFamily(rfaPath, new OverwriteFamilyLoadOptions(), out Family _);

                var symbol = new FilteredElementCollector(doc)
                    .OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
                    .FirstOrDefault(fs => fs.Family?.Name == familyName);
                if (symbol == null)
                {
                    ImportDiagnostics.ExplicitFailed(familyName, "the generated calisthenics family did not load");
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

        private static void GenerateAndSave(Application app, CalisthenicsDto rig, string rfaPath)
        {
            string? templatePath = GenericFamilyTemplateLocator.Resolve();
            if (templatePath == null)
                throw new InvalidOperationException("Couldn't locate Revit's \"Metric Generic Model\" family template.");

            var familyDoc = app.NewFamilyDocument(templatePath);
            try
            {
                using (var t = new Transaction(familyDoc, "Build Sportify calisthenics rig"))
                {
                    t.Start();
                    BuildRig(familyDoc, rig);
                    StampParameters(familyDoc.FamilyManager, rig);
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

        /* ── The rig ─────────────────────────────────────────────────────── */

        private static void BuildRig(Document fam, CalisthenicsDto r)
        {
            int bays = Math.Max(1, r.Bays);
            double bay = Positive(r.BayWidthM, 1.8);
            double depth = Positive(r.RigDepthM, 1.2);
            double height = Positive(r.FrameHeightM, 2.5);
            double postR = Positive(r.PostDiameterM, 0.1143) / 2;
            double barR = Positive(r.BarDiameterM, 0.040) / 2;

            var steel = Make(fam, "Sportify - Rig steel", (96, 104, 114), 0, 64);
            var grip = Make(fam, "Sportify - Rig grip", (200, 146, 46), 0, 40);

            // Centred on the origin: the placement hands over a centre point.
            double span = bays * bay;
            double x0 = -span / 2, y0 = -depth / 2, y1 = depth / 2;

            // Posts, and a plate under each so the rig meets the deck rather
            // than ending in mid-air at Z=0.
            double plate = postR * 2 * BasePlateSideFactor / 2;
            for (int i = 0; i <= bays; i++)
            {
                double x = x0 + i * bay;
                foreach (double y in new[] { y0, y1 })
                {
                    Tube(fam, P(x, y, 0), P(x, y, height), postR, steel);
                    Box(fam, x - plate, y - plate, x + plate, y + plate, 0, BasePlateM, steel);
                }
            }

            // Top rails, one down each side, connecting the post tops.
            foreach (double y in new[] { y0, y1 })
                Tube(fam, P(x0, y, height - postR), P(x0 + span, y, height - postR), postR, steel);

            // Monkey bars: rungs across the rig, over the middle bays. The count
            // is the web app's, so the drawing and the model agree on a number
            // someone may have to order.
            if (r.MonkeyBars && r.RungCount > 0)
            {
                int monkeyBays = Math.Max(1, r.MonkeyBays);
                double startX = x0 + (bays >= 3 ? bay : 0);
                double runSpan = monkeyBays * bay;
                double z = height - postR * 2 - barR;
                for (int i = 0; i < r.RungCount; i++)
                {
                    double t = r.RungCount > 1 ? (double)i / (r.RungCount - 1) : 0.5;
                    double x = startX + t * runSpan;
                    Tube(fam, P(x, y0, z), P(x, y1, z), barR, grip);
                }
            }

            // Pull-up bars at the end bays — the ends are where you want clear
            // air behind you, which is exactly what the end of a rig has.
            if (r.PullUpBars)
            {
                double z = Positive(r.PullUpHeightM, 2.4);
                var ends = bays >= 2 ? new[] { 0, bays - 1 } : new[] { 0 };
                foreach (int b in ends)
                {
                    double x = x0 + (b + 0.5) * bay;
                    Tube(fam, P(x, y0, z), P(x, y1, z), barR, grip);
                }
            }

            // Dip bars: a pair running ALONG the rig, so you face out of it.
            if (r.DipBars)
            {
                double z = Positive(r.DipHeightM, 1.3);
                double half = Positive(r.DipSpacingM, 0.6) / 2;
                int b = bays >= 3 ? 1 : 0;
                double xa = x0 + b * bay, xb = xa + bay;
                foreach (double y in new[] { -half, half })
                    Tube(fam, P(xa, y, z), P(xb, y, z), barR, grip);
            }

            // A low bar, across, in the last bay.
            if (r.LowBar)
            {
                double z = Positive(r.LowBarHeightM, 0.9);
                double x = x0 + (bays - 0.5) * bay;
                Tube(fam, P(x, y0, z), P(x, y1, z), barR, grip);
            }
        }

        /* ── Geometry helpers ────────────────────────────────────────────── */

        /// <summary>
        /// A round tube between two points, in any direction.
        ///
        /// The extrusion runs along the sketch plane's normal, so the plane is
        /// made from the direction itself and the circle is drawn in it using
        /// that plane's own two axes — which is what makes this work for a
        /// vertical post and a horizontal bar without special cases.
        /// </summary>
        private static void Tube(Document fam, XYZ fromM, XYZ toM, double radiusM, ElementId? mat)
        {
            var a = FtPoint(fromM);
            var b = FtPoint(toM);
            double length = a.DistanceTo(b);
            double radius = F(radiusM);
            if (length < 1e-7 || radius <= 1e-7) return;

            var dir = (b - a).Normalize();
            var plane = Plane.CreateByNormalAndOrigin(dir, a);

            // Revit refuses a full circle as one closed curve, so: two half-arcs.
            var loop = new CurveArray();
            loop.Append(Arc.Create(a, radius, 0, Math.PI, plane.XVec, plane.YVec));
            loop.Append(Arc.Create(a, radius, Math.PI, 2 * Math.PI, plane.XVec, plane.YVec));

            var profile = new CurveArrArray();
            profile.Append(loop);

            var solid = fam.FamilyCreate.NewExtrusion(true, profile, SketchPlane.Create(fam, plane), length);
            SetMaterial(solid, mat);
        }

        private static void Box(Document fam, double x0M, double y0M, double x1M, double y1M,
                                double zBaseM, double zTopM, ElementId? mat)
        {
            double h = zTopM - zBaseM;
            if (h <= 0 || Math.Abs(x1M - x0M) < 1e-6 || Math.Abs(y1M - y0M) < 1e-6) return;

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
            var solid = fam.FamilyCreate.NewExtrusion(true, profile, SketchPlane.Create(fam, plane), F(h));
            SetMaterial(solid, mat);
        }

        private static void SetMaterial(Extrusion solid, ElementId? mat)
        {
            if (mat == null || mat == ElementId.InvalidElementId) return;
            var mp = solid.get_Parameter(BuiltInParameter.MATERIAL_ID_PARAM);
            if (mp != null && !mp.IsReadOnly) mp.Set(mat);
        }

        private static XYZ P(double xM, double yM, double zM) => new XYZ(xM, yM, zM);
        private static XYZ FtPoint(XYZ m) => new XYZ(F(m.X), F(m.Y), F(m.Z));
        private static double F(double m) => UnitUtils.ConvertToInternalUnits(m, UnitTypeId.Meters);
        private static double Positive(double v, double fallback) => v > 1e-6 ? v : fallback;

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
            catch (Exception)
            {
                return ElementId.InvalidElementId;
            }
        }

        private static void StampParameters(FamilyManager fm, CalisthenicsDto r)
        {
            void Text(string name, string? value)
            {
                if (string.IsNullOrWhiteSpace(value)) return;
                try
                {
                    var fp = fm.AddParameter(name, GroupTypeId.IdentityData, SpecTypeId.String.Text, false);
                    fm.Set(fp, value);
                }
                catch (Exception) { /* a parameter the template will not take is not worth the family */ }
            }

            Text("Sportify_Sport", "Calisthenics");
            Text("Sportify_Bays", $"{r.Bays} × {r.BayWidthM:0.##} m");
            Text("Sportify_RigSize", $"{r.RigLengthM:0.##} x {r.RigWidthM:0.##} m, {r.FrameHeightM:0.##} m high");
            Text("Sportify_Footprint", $"{r.LengthM:0.##} x {r.WidthM:0.##} m including safety area");
            Text("Sportify_SafetyMargin", $"{r.SafetyMarginM:0.##} m");
            Text("Sportify_PullUpHeight", $"{r.PullUpHeightM:0.##} m");
            Text("Sportify_BarDiameter", $"{r.BarDiameterM * 1000:0.#} mm");
            Text("Sportify_PostDiameter", $"{r.PostDiameterM * 1000:0.#} mm");
            Text("Sportify_Stations", Stations(r));
            Text("Sportify_RungCount", r.MonkeyBars ? r.RungCount.ToString() : "0");
            Text("Sportify_WeightKg", Math.Round(r.WeightKg).ToString());
            Text("Sportify_Source", r.Source);
        }

        private static string Stations(CalisthenicsDto r)
        {
            var on = new List<string>();
            if (r.PullUpBars) on.Add("pull-up bars");
            if (r.MonkeyBars) on.Add("monkey bars");
            if (r.DipBars) on.Add("dip bars");
            if (r.LowBar) on.Add("low bar");
            return on.Count == 0 ? "frame only" : string.Join(", ", on);
        }

        private static string FamilyNameFor(CalisthenicsDto r) =>
            SanitizeName($"Sportify - Calisthenics {Math.Max(1, r.Bays)}bay " +
                         $"{r.BayWidthM:0.##}x{r.RigDepthM:0.##}x{r.FrameHeightM:0.##} " +
                         $"{(r.MonkeyBars ? "M" : "")}{(r.PullUpBars ? "P" : "")}{(r.DipBars ? "D" : "")}{(r.LowBar ? "L" : "")}");

        private static string CachePath(string familyName) =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                         "Sportify", "SportifyGeneratedFamilies", "calisthenics-" + BuilderVersion,
                         familyName + ".rfa");

        private static readonly char[] BadNameChars = { ':', ';', '{', '}', '[', ']', '|', '<', '>', '?', '`', '~' };

        private static string SanitizeName(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars().Concat(BadNameChars))
                name = name.Replace(c, '-');
            return name.Trim();
        }

        /// <summary>Answers Revit's "already loaded" prompt in code, so an import never stops to ask.</summary>
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
