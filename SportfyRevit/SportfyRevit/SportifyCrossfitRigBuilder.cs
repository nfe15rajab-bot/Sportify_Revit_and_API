using System.IO;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;
using static SportfyRevit.SportifyRigGeometry;

namespace SportfyRevit
{
    /// <summary>
    /// The CrossFit rig.
    ///
    /// ── Why it is not the calisthenics rig with different numbers ──
    /// A calisthenics rig is round tube you hang from. A CrossFit rig is SQUARE
    /// section you bolt things to, and that is the whole of the difference. The
    /// uprights are square so a J-cup can clamp round them at any height, which
    /// is what turns a frame into a squat rack — and a squat rack is what makes
    /// it a rig rather than a pull-up frame.
    ///
    /// So this builder draws square uprights with their perforations, J-cups
    /// clamped to them, and round bars only where you actually grip. Drawing it
    /// with round posts would make the two rigs look alike in the model when
    /// they are not alike on the roof.
    ///
    /// ── Double sided ──
    /// A rig is normally two rows facing each other, with a barbell racking
    /// between them and the rows tied across the top. That is structural, not
    /// decorative: the ties are what stop the two rows folding towards each
    /// other when a loaded bar is dropped into the cups.
    /// </summary>
    internal static class SportifyCrossfitRigBuilder
    {
        private static readonly Dictionary<string, ElementId> SessionCache = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Bump when the geometry or the materials change — see the padel builder.</summary>
        private const string BuilderVersion = "v1";

        private const double UprightWallM = 0.003;
        private const double BarWallM = 0.003;
        private const double BasePlateM = 0.012;
        private const double BasePlateFactor = 2.4;     // plate side as a multiple of the upright

        public static FamilySymbol? GetOrCreateSymbol(Document doc, CrossfitDto rig)
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

            string rfaPath = CachePath("crossfit", BuilderVersion, familyName);
            try
            {
                if (!File.Exists(rfaPath)) GenerateAndSave(doc.Application, rig, rfaPath);
                doc.LoadFamily(rfaPath, new OverwriteLoadOptions(), out Family _);

                var symbol = new FilteredElementCollector(doc)
                    .OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
                    .FirstOrDefault(fs => fs.Family?.Name == familyName);
                if (symbol == null)
                {
                    ImportDiagnostics.ExplicitFailed(familyName, "the generated CrossFit rig family did not load");
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

        private static void GenerateAndSave(Application app, CrossfitDto rig, string rfaPath)
        {
            string? templatePath = GenericFamilyTemplateLocator.Resolve();
            if (templatePath == null)
                throw new InvalidOperationException("Couldn't locate Revit's \"Metric Generic Model\" family template.");

            var familyDoc = app.NewFamilyDocument(templatePath);
            try
            {
                using (var t = new Transaction(familyDoc, "Build Sportify CrossFit rig"))
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

        private static void BuildRig(Document fam, CrossfitDto r)
        {
            int bays = Math.Max(1, r.Bays);
            double bay = Positive(r.BayWidthM, 1.2);
            double up = Positive(r.UprightSizeM, 0.075);
            double height = Positive(r.UprightHeightM, 2.75);
            double barR = Positive(r.BarDiameterM, 0.032) / 2;
            double depth = Positive(r.RigDepthM, 1.1);

            var steel = Material(fam, "Sportify - Rig steel", (86, 94, 104), 0, 60);
            var grip = Material(fam, "Sportify - Rig grip", (200, 146, 46), 0, 40);
            var cup = Material(fam, "Sportify - J-cup", (184, 67, 47), 0, 30);

            double span = bays * bay;
            double x0 = -span / 2;
            // A single-sided rig is one row on the centre line; a double one has
            // its two rows either side of it, so both are centred the same way.
            double[] rowY = r.DoubleSided ? new[] { -depth / 2, depth / 2 } : new[] { 0.0 };

            double plate = up * BasePlateFactor / 2;
            foreach (double y in rowY)
            {
                for (int i = 0; i <= bays; i++)
                {
                    double x = x0 + i * bay;
                    SquareTube(fam, P(x, y, 0), P(x, y, height), up, UprightWallM, steel);
                    Box(fam, x - plate, y - plate, x + plate, y + plate, 0, BasePlateM, steel);
                }
            }

            // Pull-up bars: one per bay, along each row, set into the uprights.
            if (r.PullUpBars)
            {
                double z = Positive(r.PullUpHeightM, 2.4);
                foreach (double y in rowY)
                    for (int b = 0; b < bays; b++)
                    {
                        double xa = x0 + b * bay, xb = xa + bay;
                        Tube(fam, P(xa, y, z), P(xb, y, z), barR, BarWallM, grip);
                    }
            }

            // The cross ties that make two rows one structure.
            if (r.DoubleSided && rowY.Length == 2)
            {
                double z = height - up / 2;
                for (int i = 0; i <= bays; i++)
                {
                    double x = x0 + i * bay;
                    SquareTube(fam, P(x, rowY[0], z), P(x, rowY[1], z), up, UprightWallM, steel);
                }
            }

            // J-cups, a pair per upright, facing into the rig.
            if (r.SquatStations)
            {
                double z = Positive(r.JCupHeightM, 1.2);
                double reach = up * 1.5;
                foreach (double y in rowY)
                {
                    // Into the rig on a double; outward on a single, where the
                    // lifter stands in front of the one row there is.
                    double sign = r.DoubleSided ? (y < 0 ? 1 : -1) : 1;
                    for (int i = 0; i <= bays; i++)
                    {
                        double x = x0 + i * bay;
                        double ya = y + sign * up / 2, yb = ya + sign * reach;
                        Box(fam, x - up / 2, Math.Min(ya, yb), x + up / 2, Math.Max(ya, yb), z, z + up * 0.8, cup);
                    }
                }
            }

            // Dip station: a pair of bars running ALONG the rig, so you face out.
            if (r.DipBars)
            {
                double z = Positive(r.DipHeightM, 1.35);
                int b = bays >= 2 ? 1 : 0;
                double xa = x0 + b * bay, xb = xa + bay;
                double half = Math.Min(0.3, depth / 4);
                foreach (double y in new[] { -half, half })
                    Tube(fam, P(xa, y, z), P(xb, y, z), barR, BarWallM, grip);
            }

            // Plate pegs on the outer face — the reason plate storage costs depth.
            if (r.PlateStorage)
            {
                double z = Positive(r.PegHeightM, 1.5);
                double outM = Positive(r.PegProjectionM, 0.4);
                foreach (double y in rowY)
                {
                    double sign = r.DoubleSided ? (y < 0 ? -1 : 1) : -1;
                    for (int i = 0; i <= bays; i++)
                    {
                        double x = x0 + i * bay;
                        Tube(fam, P(x, y + sign * up / 2, z), P(x, y + sign * (up / 2 + outM), z), 0.025, 0.025, steel);
                    }
                }
            }
        }

        private static void StampParameters(FamilyManager fm, CrossfitDto r)
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

            Text("Sportify_Sport", "CrossFit");
            Text("Sportify_Bays", $"{r.Bays} × {r.BayWidthM:0.##} m");
            Text("Sportify_Sided", r.DoubleSided ? "Double sided" : "Single sided");
            Text("Sportify_Stations", r.Stations.ToString());
            Text("Sportify_RigSize", $"{r.RigLengthM:0.##} x {r.RigWidthM:0.##} m, {r.UprightHeightM:0.##} m high");
            Text("Sportify_Footprint", $"{r.LengthM:0.##} x {r.WidthM:0.##} m including working room");
            Text("Sportify_WorkingDepth", $"{r.WorkingDepthM:0.##} m per face");
            Text("Sportify_Upright", $"{r.UprightSizeM * 1000:0.#} mm square");
            Text("Sportify_BarDiameter", $"{r.BarDiameterM * 1000:0.#} mm");
            Text("Sportify_Fitted", Fitted(r));
            Text("Sportify_WeightKg", Math.Round(r.WeightKg).ToString());
            Text("Sportify_Source", r.Source);
        }

        private static string Fitted(CrossfitDto r)
        {
            var on = new List<string>();
            if (r.PullUpBars) on.Add("pull-up bars");
            if (r.SquatStations) on.Add("J-cups");
            if (r.DipBars) on.Add("dip station");
            if (r.PlateStorage) on.Add("plate storage");
            return on.Count == 0 ? "frame only" : string.Join(", ", on);
        }

        private static string FamilyNameFor(CrossfitDto r) =>
            SanitizeName($"Sportify - CrossFit {Math.Max(1, r.Bays)}bay " +
                         $"{r.BayWidthM:0.##}x{r.RigDepthM:0.##}x{r.UprightHeightM:0.##} " +
                         $"{(r.DoubleSided ? "dbl" : "sgl")} " +
                         $"{(r.PullUpBars ? "P" : "")}{(r.SquatStations ? "J" : "")}{(r.DipBars ? "D" : "")}{(r.PlateStorage ? "S" : "")}");
    }
}
