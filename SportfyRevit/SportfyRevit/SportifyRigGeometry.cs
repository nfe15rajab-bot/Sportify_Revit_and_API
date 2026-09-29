using System.IO;
using Autodesk.Revit.DB;

namespace SportfyRevit
{
    /// <summary>
    /// Tubes, plates and materials — the vocabulary every rig is written in.
    ///
    /// The calisthenics rig and the CrossFit rig (and, until 2026-09-29, the TRX frame) are the same
    /// kind of object: steel sections between two points. They differ in what
    /// they are FOR, not in how they are drawn, so the drawing lives here once
    /// rather than three times. When the calisthenics builder was the only one,
    /// these were private to it; the second rig is what makes them shared.
    ///
    /// ── Round versus square ──
    /// Both are here because the distinction is real and visible. A calisthenics
    /// rig is round tube throughout — it is a thing you grip. A CrossFit rig's
    /// uprights are square section, which is why a J-cup can clamp to them and
    /// why the two rigs do not look alike even at the same size. Drawing both as
    /// boxes would lose the difference; drawing both as round would lose it the
    /// other way.
    ///
    /// ── Hollow, not solid ──
    /// Every section here is a ring: an outer loop and an inner one. Solid steel
    /// would be several times heavier than the real thing and wrong in the same
    /// direction every time, which is the worst kind of wrong for a structural
    /// check reading these weights.
    /// </summary>
    internal static class SportifyRigGeometry
    {
        /// <summary>
        /// A round tube between two points, in any direction.
        ///
        /// The extrusion runs along the sketch plane's normal, so the plane is
        /// made FROM the direction of travel and the profile is drawn using that
        /// plane's own two axes. That is what lets a vertical post and a
        /// horizontal bar be the same call with no special cases.
        ///
        /// Revit refuses a full circle as one closed curve in a profile, so each
        /// circle is two half-arcs. That is a property of the API, not of the
        /// shape, and it caught the climbing tower first.
        /// </summary>
        public static void Tube(Document fam, XYZ fromM, XYZ toM, double radiusM,
                                double wallM, ElementId? mat)
        {
            var a = FtPoint(fromM);
            var b = FtPoint(toM);
            double length = a.DistanceTo(b);
            double ro = F(radiusM);
            if (length < 1e-7 || ro <= 1e-7) return;

            var plane = Plane.CreateByNormalAndOrigin((b - a).Normalize(), a);
            var profile = new CurveArrArray();
            profile.Append(CircleLoop(a, ro, plane));

            // The bore. Skipped when the wall would swallow it, which is how a
            // solid bar is asked for: pass a wall thicker than the radius.
            double ri = F(Math.Max(0, radiusM - wallM));
            if (ri > 1e-7 && ri < ro - 1e-9) profile.Append(CircleLoop(a, ri, plane));

            var solid = fam.FamilyCreate.NewExtrusion(true, profile, SketchPlane.Create(fam, plane), length);
            SetMaterial(solid, mat);
        }

        /// <summary>
        /// A square hollow section between two points.
        ///
        /// Oriented so one face is level: rig uprights are bolted to things and
        /// a section turned 45 degrees would read as a diamond, which no rig is.
        /// </summary>
        public static void SquareTube(Document fam, XYZ fromM, XYZ toM, double sideM,
                                      double wallM, ElementId? mat)
        {
            var a = FtPoint(fromM);
            var b = FtPoint(toM);
            double length = a.DistanceTo(b);
            if (length < 1e-7 || sideM <= 1e-7) return;

            var dir = (b - a).Normalize();
            var plane = Plane.CreateByNormalAndOrigin(dir, a);
            var u = LevelledAxis(dir, plane);
            var v = dir.CrossProduct(u).Normalize();

            double h = F(sideM) / 2;
            var profile = new CurveArrArray();
            profile.Append(SquareLoop(a, u, v, h));

            double hi = F(Math.Max(0, sideM / 2 - wallM));
            if (hi > 1e-7 && hi < h - 1e-9) profile.Append(SquareLoop(a, u, v, hi));

            var solid = fam.FamilyCreate.NewExtrusion(true, profile, SketchPlane.Create(fam, plane), length);
            SetMaterial(solid, mat);
        }

        /// <summary>A solid rectangular block — base plates, J-cups, anything not a section.</summary>
        public static void Box(Document fam, double x0M, double y0M, double x1M, double y1M,
                               double zBaseM, double zTopM, ElementId? mat)
        {
            double height = zTopM - zBaseM;
            if (height <= 0 || Math.Abs(x1M - x0M) < 1e-6 || Math.Abs(y1M - y0M) < 1e-6) return;

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
            var solid = fam.FamilyCreate.NewExtrusion(true, profile, SketchPlane.Create(fam, plane), F(height));
            SetMaterial(solid, mat);
        }

        /* ── Profiles ────────────────────────────────────────────────────── */

        private static CurveArray CircleLoop(XYZ centre, double radiusFt, Plane plane)
        {
            var loop = new CurveArray();
            loop.Append(Arc.Create(centre, radiusFt, 0, Math.PI, plane.XVec, plane.YVec));
            loop.Append(Arc.Create(centre, radiusFt, Math.PI, 2 * Math.PI, plane.XVec, plane.YVec));
            return loop;
        }

        private static CurveArray SquareLoop(XYZ centre, XYZ u, XYZ v, double halfFt)
        {
            var p0 = centre - u * halfFt - v * halfFt;
            var p1 = centre + u * halfFt - v * halfFt;
            var p2 = centre + u * halfFt + v * halfFt;
            var p3 = centre - u * halfFt + v * halfFt;

            var loop = new CurveArray();
            loop.Append(Line.CreateBound(p0, p1));
            loop.Append(Line.CreateBound(p1, p2));
            loop.Append(Line.CreateBound(p2, p3));
            loop.Append(Line.CreateBound(p3, p0));
            return loop;
        }

        /// <summary>
        /// An axis across the section that keeps one face level.
        ///
        /// For anything not vertical that is horizontal across the run; for a
        /// vertical post there is no "level" to line up with, so the model's own
        /// X is used and the post reads square to the rig.
        /// </summary>
        private static XYZ LevelledAxis(XYZ dir, Plane plane)
        {
            var across = XYZ.BasisZ.CrossProduct(dir);
            return across.GetLength() < 1e-6 ? XYZ.BasisX : across.Normalize();
        }

        /* ── Materials ───────────────────────────────────────────────────── */

        public static ElementId Material(Document fam, string name, (int R, int G, int B) rgb,
                                         int transparency = 0, int shininess = 64)
        {
            string safe = SanitizeName(name);
            var existing = new FilteredElementCollector(fam).OfClass(typeof(Material))
                .Cast<Material>().FirstOrDefault(x => x.Name.Equals(safe, StringComparison.OrdinalIgnoreCase));
            if (existing != null) return existing.Id;
            try
            {
                var id = Autodesk.Revit.DB.Material.Create(fam, safe);
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

        private static void SetMaterial(Extrusion solid, ElementId? mat)
        {
            if (mat == null || mat == ElementId.InvalidElementId) return;
            var mp = solid.get_Parameter(BuiltInParameter.MATERIAL_ID_PARAM);
            if (mp != null && !mp.IsReadOnly) mp.Set(mat);
        }

        /* ── Units and names ─────────────────────────────────────────────── */

        public static XYZ P(double xM, double yM, double zM) => new XYZ(xM, yM, zM);
        public static double F(double m) => UnitUtils.ConvertToInternalUnits(m, UnitTypeId.Meters);
        public static double Positive(double v, double fallback) => v > 1e-6 ? v : fallback;
        private static XYZ FtPoint(XYZ m) => new XYZ(F(m.X), F(m.Y), F(m.Z));

        private static readonly char[] BadNameChars = { ':', ';', '{', '}', '[', ']', '|', '<', '>', '?', '`', '~' };

        public static string SanitizeName(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars().Concat(BadNameChars))
                name = name.Replace(c, '-');
            return name.Trim();
        }

        public static string CachePath(string folder, string version, string familyName) =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                         "Sportify", "SportifyGeneratedFamilies", folder + "-" + version,
                         familyName + ".rfa");

        /// <summary>Answers Revit's "already loaded" prompt in code, so an import never stops to ask.</summary>
        internal sealed class OverwriteLoadOptions : IFamilyLoadOptions
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
