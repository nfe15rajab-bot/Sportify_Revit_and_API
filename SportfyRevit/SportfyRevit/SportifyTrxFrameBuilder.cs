using System.IO;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;
using static SportfyRevit.SportifyRigGeometry;

namespace SportfyRevit
{
    /// <summary>
    /// The suspension training frame.
    ///
    /// ── Why it is shaped differently from the rigs ──
    /// A rig is loaded downwards: you hang off a bar and the posts push into the
    /// deck. A suspension frame is loaded at an ANGLE, because the whole point
    /// of suspension training is leaning away from the anchor. Every strap pulls
    /// the beam sideways as well as down.
    ///
    /// So the legs splay. The splay is not styling — it is the base width that
    /// resists the overturning, and it is why this family is drawn with angled
    /// legs meeting under the beam rather than as another portal frame. A frame
    /// built with vertical posts has to be bolted to the deck instead, and the
    /// web app says so rather than letting someone draw one that would tip.
    ///
    /// ── Anchors ──
    /// The beam carries anchor points rather than bars. They are small, and they
    /// are the whole reason the thing exists, so they are modelled: a ring at
    /// each point, spaced as the payload says. The count is the number of people
    /// who can train at once, which is a more direct answer than a rig gives.
    /// </summary>
    internal static class SportifyTrxFrameBuilder
    {
        private static readonly Dictionary<string, ElementId> SessionCache = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Bump when the geometry or the materials change — see the padel builder.</summary>
        private const string BuilderVersion = "v1";

        private const double WallM = 0.003;
        private const double FootPlateM = 0.014;
        private const double AnchorRingM = 0.045;

        public static FamilySymbol? GetOrCreateSymbol(Document doc, TrxDto frame)
        {
            string familyName = FamilyNameFor(frame);

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

            string rfaPath = CachePath("trx", BuilderVersion, familyName);
            try
            {
                if (!File.Exists(rfaPath)) GenerateAndSave(doc.Application, frame, rfaPath);
                doc.LoadFamily(rfaPath, new OverwriteLoadOptions(), out Family _);

                var symbol = new FilteredElementCollector(doc)
                    .OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
                    .FirstOrDefault(fs => fs.Family?.Name == familyName);
                if (symbol == null)
                {
                    ImportDiagnostics.ExplicitFailed(familyName, "the generated suspension frame family did not load");
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

        private static void GenerateAndSave(Application app, TrxDto frame, string rfaPath)
        {
            string? templatePath = GenericFamilyTemplateLocator.Resolve();
            if (templatePath == null)
                throw new InvalidOperationException("Couldn't locate Revit's \"Metric Generic Model\" family template.");

            var familyDoc = app.NewFamilyDocument(templatePath);
            try
            {
                using (var t = new Transaction(familyDoc, "Build Sportify suspension frame"))
                {
                    t.Start();
                    BuildFrame(familyDoc, frame);
                    StampParameters(familyDoc.FamilyManager, frame);
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

        private static void BuildFrame(Document fam, TrxDto f)
        {
            double beam = Positive(f.BeamLengthM, 3.0);
            double height = Positive(f.FrameHeightM, 2.45);
            double spread = Positive(f.LegSpreadM, 1.2);
            double beamR = Positive(f.BeamDiameterM, 0.089) / 2;
            double legR = Positive(f.LegDiameterM, 0.076) / 2;

            var steel = Material(fam, "Sportify - Frame steel", (86, 94, 104), 0, 60);
            var strap = Material(fam, "Sportify - Anchor", (200, 146, 46), 0, 40);

            double x0 = -beam / 2, x1 = beam / 2;
            double zBeam = height;

            // The beam the straps hang from.
            Tube(fam, P(x0, 0, zBeam), P(x1, 0, zBeam), beamR, WallM, steel);

            // Legs. Splayed ones meet under the beam and spread to the deck —
            // that spread is what holds the frame down against a sideways pull.
            double half = f.AFrame ? spread / 2 : 0;
            double plate = legR * 3;
            foreach (double x in new[] { x0, x1 })
            {
                foreach (double sign in new[] { -1.0, 1.0 })
                {
                    double yFoot = sign * half;
                    Tube(fam, P(x, yFoot, 0), P(x, 0, zBeam), legR, WallM, steel);
                    Box(fam, x - plate, yFoot - plate, x + plate, yFoot + plate, 0, FootPlateM, steel);
                    // A vertical frame has one post per end, not two.
                    if (!f.AFrame) break;
                }
            }

            // A low rail between the legs, when asked for.
            if (f.MidRail)
            {
                double z = Math.Min(0.45, height / 4);
                Tube(fam, P(x0, 0, z), P(x1, 0, z), legR * 0.8, WallM, steel);
            }

            // The anchor points. Small, and the reason the frame is here at all.
            int n = Math.Max(1, f.AnchorCount);
            double usable = Math.Max(0, beam - 0.3);
            for (int i = 0; i < n; i++)
            {
                double t = n > 1 ? (double)i / (n - 1) : 0.5;
                double x = x0 + 0.15 + t * usable;
                Tube(fam, P(x, -AnchorRingM / 2, zBeam - beamR), P(x, AnchorRingM / 2, zBeam - beamR),
                     AnchorRingM / 2, AnchorRingM / 2, strap);
            }
        }

        private static void StampParameters(FamilyManager fm, TrxDto f)
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

            Text("Sportify_Sport", "Suspension training");
            Text("Sportify_Anchors", f.AnchorCount.ToString());
            Text("Sportify_AnchorSpacing", $"{f.AnchorSpacingM * 1000:0} mm");
            Text("Sportify_FrameSize", $"{f.FrameLengthM:0.##} x {f.FrameWidthM:0.##} m, {f.FrameHeightM:0.##} m anchor height");
            Text("Sportify_Footprint", $"{f.LengthM:0.##} x {f.WidthM:0.##} m including reach");
            Text("Sportify_Reach", $"{f.ReachDepthM:0.##} m each side");
            Text("Sportify_Legs", f.AFrame ? $"Splayed, {f.LegSpreadM:0.##} m base" : "Vertical posts");
            Text("Sportify_Fixing", f.GroundAnchor ? "Bolted to the deck" : "Free-standing, ballasted feet");
            Text("Sportify_WeightKg", Math.Round(f.WeightKg).ToString());
            Text("Sportify_Source", f.Source);
        }

        private static string FamilyNameFor(TrxDto f) =>
            SanitizeName($"Sportify - Suspension frame {f.BeamLengthM:0.##}x{f.FrameHeightM:0.##} " +
                         $"{(f.AFrame ? "splayed" : "vertical")} {f.AnchorCount}anchor" +
                         $"{(f.MidRail ? " rail" : "")}{(f.GroundAnchor ? " bolted" : "")}");
    }
}
