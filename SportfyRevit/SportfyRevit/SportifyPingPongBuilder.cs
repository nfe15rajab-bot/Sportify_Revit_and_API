using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;

namespace SportfyRevit
{
    /// <summary>
    /// A table tennis table in Revit, and the space it needs.
    ///
    /// The odd one out among the court builders: there is nothing to size. The
    /// ITTF fixes the table at 2.74 x 1.525 m, 0.76 m high, with a 15.25 cm net
    /// reaching 15.25 cm past each side line. Every table is that table.
    ///
    /// So what varies here is the space AROUND it, which is what a roof actually
    /// has to give up: 7.6 x 4.6 m for casual play against 14 x 7 m for the
    /// Olympic minimum. The same table, nearly three times the roof.
    ///
    /// ── The surface may be nothing at all ──
    /// A table can stand on the roof finish that is already there. When it does,
    /// no surface is built: the playing space is real, but it is not a different
    /// material, and laying one nobody asked for would be inventing work.
    /// </summary>
    internal static class SportifyPingPongBuilder
    {
        private static readonly Dictionary<string, ElementId> SessionCache = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Bump when the geometry or the materials change — see the padel builder.</summary>
        private const string BuilderVersion = "v1";

        private const double SurfaceThicknessM = 0.025;
        private const double MarkingThicknessM = 0.002;
        private const double LegSizeM = 0.06;
        private const double NetThicknessM = 0.01;
        private const double NetPostM = 0.03;

        public static FamilySymbol? GetOrCreateSymbol(Document doc, PingPongDto court)
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
                    ImportDiagnostics.ExplicitFailed(familyName, "the generated table tennis family did not load");
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

        private static void GenerateAndSave(Application app, PingPongDto c, string familyName, string rfaPath)
        {
            string? templatePath = GenericFamilyTemplateLocator.Resolve();
            if (templatePath == null)
                throw new InvalidOperationException("Couldn't locate Revit's \"Metric Generic Model\" family template.");

            var familyDoc = app.NewFamilyDocument(templatePath);
            try
            {
                using (var t = new Transaction(familyDoc, "Build Sportify table tennis table"))
                {
                    t.Start();
                    BuildTable(familyDoc, c);
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

        private sealed class TableMaterials
        {
            public ElementId Surface = ElementId.InvalidElementId;
            public ElementId TableTop = ElementId.InvalidElementId;
            public ElementId Frame = ElementId.InvalidElementId;
            public ElementId Marking = ElementId.InvalidElementId;
            public ElementId Net = ElementId.InvalidElementId;
        }

        private static TableMaterials BuildMaterials(Document fam, PingPongDto c)
        {
            var surf = ParseHex(c.AppearanceHex) ?? (0x3F, 0x6F, 0x9C);
            // An ITTF table is dark and MATT. A shiny one would be wrong: the
            // matt finish is what makes the ball readable against it.
            var top = (0x1E, 0x4A, 0x72);
            var frame = (c.Table ?? "steel_composite") switch
            {
                "concrete"  => (0xB8, 0xB4, 0xAC),
                "aluminium" => (0xC8, 0xCC, 0xD0),
                _           => (0x6E, 0x74, 0x7C),
            };
            return new TableMaterials
            {
                Surface  = Make(fam, SurfaceMaterialName(c), surf, 0, 12),
                TableTop = Make(fam, "Sportify - Table tennis top, matt", top, 0, 4),
                Frame    = Make(fam, FrameMaterialName(c), frame, 0, c.Table == "concrete" ? 8 : 70),
                Marking  = Make(fam, "Sportify - Court line marking, white", (0xF2, 0xF2, 0xF2), 0, 8),
                Net      = Make(fam, "Sportify - Table tennis net", (0x2A, 0x2A, 0x2A), 40, 10),
            };
        }

        private static string SurfaceMaterialName(PingPongDto c) => (c.Surface ?? "existing") switch
        {
            "polyurethane" => "Sportify - Poured polyurethane sports surface",
            "tiles"        => "Sportify - Modular sports tiles",
            "acrylic"      => "Sportify - Acrylic sports surface",
            _              => "Sportify - Existing roof finish",
        };

        private static string FrameMaterialName(PingPongDto c) => (c.Table ?? "steel_composite") switch
        {
            "concrete"  => "Sportify - Table tennis table, concrete",
            "aluminium" => "Sportify - Table tennis table, aluminium",
            _           => "Sportify - Table tennis table, galvanised steel",
        };

        /// <summary>
        /// Long axis along X, origin at the centre of the playing space — the
        /// convention the other courts use. The table sits in the middle of it,
        /// which is what the clearances either side of it mean.
        /// </summary>
        private static void BuildTable(Document fam, PingPongDto c)
        {
            var m = BuildMaterials(fam, c);

            double spaceL = c.LengthM > 0 ? c.LengthM : 7.6;
            double spaceW = c.WidthM > 0 ? c.WidthM : 4.6;
            double tl = c.TableLengthM > 0 ? c.TableLengthM : 2.74;
            double tw = c.TableWidthM > 0 ? c.TableWidthM : 1.525;
            double th = c.TableHeightM > 0 ? c.TableHeightM : 0.76;
            double topT = c.TableTopThicknessM > 0 ? c.TableTopThicknessM : 0.025;
            double lw = c.LineWidthM > 0 ? c.LineWidthM : 0.02;

            double halfSL = spaceL / 2, halfSW = spaceW / 2;
            double halfTL = tl / 2, halfTW = tw / 2;

            int failed = 0;
            void Try(Action a) { try { a(); } catch { failed++; } }

            // The playing space, only when it is a surface of its own.
            if (!string.Equals(c.Surface, "existing", StringComparison.OrdinalIgnoreCase))
                Try(() => Box(fam, -halfSL, -halfSW, halfSL, halfSW, -SurfaceThicknessM, 0, m.Surface));

            // ── The table ──
            Try(() => Box(fam, -halfTL, -halfTW, halfTL, halfTW, th - topT, th, m.TableTop));

            // Its markings: side and end lines, and the centre line that only
            // doubles uses — standing on the top, not cut into it.
            double top = th + MarkingThicknessM;
            Try(() => RectRing(fam, -halfTL + lw / 2, -halfTW + lw / 2, halfTL - lw / 2, halfTW - lw / 2,
                               lw, th, top, m.Marking));
            double cl = c.CentreLineWidthM > 0 ? c.CentreLineWidthM : 0.003;
            Try(() => Box(fam, -halfTL, -cl / 2, halfTL, cl / 2, th, top, m.Marking));

            // ── What holds it up ──
            if (string.Equals(c.Table, "concrete", StringComparison.OrdinalIgnoreCase))
            {
                // A concrete table is a pedestal, not legs. That is why it weighs
                // 400 kg, and why it does not move again once it is placed.
                Try(() => Box(fam, -halfTL * 0.45, -halfTW * 0.5, halfTL * 0.45, halfTW * 0.5, 0, th - topT, m.Frame));
            }
            else
            {
                foreach (var sx in new[] { -1.0, 1.0 })
                    foreach (var sy in new[] { -1.0, 1.0 })
                    {
                        double lx = sx * (halfTL - 0.20), ly = sy * (halfTW - 0.15);
                        Try(() => Box(fam, lx - LegSizeM / 2, ly - LegSizeM / 2,
                                           lx + LegSizeM / 2, ly + LegSizeM / 2, 0, th - topT, m.Frame));
                    }
                // The rail the legs hang from, just under the top.
                Try(() => RectRing(fam, -halfTL + 0.20, -halfTW + 0.15, halfTL - 0.20, halfTW - 0.15,
                                   0.04, th - topT - 0.06, th - topT, m.Frame));
            }

            // ── The net, and the posts that tension it ──
            double netH = c.NetHeightM > 0 ? c.NetHeightM : 0.1525;
            double over = c.NetOverhangM > 0 ? c.NetOverhangM : 0.1525;
            Try(() => Box(fam, -NetThicknessM / 2, -halfTW - over, NetThicknessM / 2, halfTW + over,
                          th, th + netH, m.Net));
            foreach (var sy in new[] { -1.0, 1.0 })
            {
                double py = sy * (halfTW + over);
                Try(() => Box(fam, -NetPostM / 2, py - NetPostM / 2, NetPostM / 2, py + NetPostM / 2,
                              th, th + netH, m.Frame));
            }

            if (failed > 0)
                ImportDiagnostics.ExplicitFailed(FamilyNameFor(c), $"{failed} part(s) could not be drawn; the rest of the table was built");
        }

        /* ── Geometry helpers, as the football builder ──────────────────────── */

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

        private static void StampParameters(FamilyManager fm, PingPongDto c)
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

            Text("Sportify_Sport", "Table tennis");
            Text("Sportify_PlayingSpace", c.PlayingSpace);
            Text("Sportify_Table", c.Table);
            Text("Sportify_Net", c.Net);
            Text("Sportify_Surface", c.Surface);
            Text("Sportify_TableSize", $"{c.TableLengthM} x {c.TableWidthM} m, {c.TableHeightM} m high");
            Text("Sportify_ClearanceEnd", $"{c.ClearanceEndM} m");
            Text("Sportify_ClearanceSide", $"{c.ClearanceSideM} m");
            Text("Sportify_ClearHeightMin", $"{c.ClearHeightMinM} m");
            Text("Sportify_WeightKg", Math.Round(c.WeightKg).ToString());
            Text("Sportify_Source", c.Source);
        }

        private static string FamilyNameFor(PingPongDto c) =>
            SanitizeName($"Sportify - Table tennis {c.PlayingSpace ?? "recreational"} " +
                         $"{c.Table ?? "steel_composite"} {c.Surface ?? "existing"}");

        private static string CachePath(string familyName) =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                         "Sportify", "SportifyGeneratedFamilies", "pingpong-" + BuilderVersion,
                         familyName + ".rfa");

        private static string SanitizeName(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars().Concat(BadNameChars))
                name = name.Replace(c, '-');
            return name.Trim();
        }

        /// <summary>Characters Revit accepts in a file name but not happily in a family name.</summary>
        private static readonly char[] BadNameChars =
            { '{', '}', '[', ']', '|', ';', '<', '>', '?', '~', '`' };

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
