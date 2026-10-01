using Autodesk.Revit.DB;
using Sportify.Simulation.Sun;

namespace SportfyRevit
{
    /// <summary>
    /// Puts back the posts of Kinetics units that lost them, where they stood, without moving anything (user, 2026-10-01: "the pergolas should stay in this
    /// place ... but make the columns not deleted"). Until bf74016 every sync onto a roof swept "untracked Sportify leftovers standing on it", and of a pergola
    /// only its posts stand on the slab: the five pergolas of the team's model kept their blades, rails and rods and lost every post. A post stood under each
    /// end of each rail (KineticUnits.Overhead), from the unit's base up to the rail's underside: rebuilt from the rails still there, with the same adaptive
    /// bar family, the post size of the Kinetics inputs, and the rail's unit, roof, workset and phase. A unit that still has a post is left as it is.
    /// </summary>
    internal static class KineticPostRepair
    {
        const double FeetPerMetre = 1.0 / 0.3048;

        static string Role(Element e) => e.LookupParameter("Sportify_TypeId")?.AsString() ?? "";

        static XYZ Mean(IEnumerable<XYZ> points)
        {
            var list = points.ToList();
            var sum = list.Aggregate(XYZ.Zero, (s, p) => s + p);
            return sum / list.Count;
        }

        /// <summary>Opens its own transaction. Returns how many posts were put back; `lines` says what was done, unit by unit.</summary>
        internal static int Run(Document doc, List<string> lines)
        {
            var kinetic = new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>()
                .Where(e => e.DesignOption == null && e.LookupParameter("Sportify_Category")?.AsString() == SportifyKineticFamilyBuilder.KineticsCategoryValue)
                .ToList();
            var units = kinetic.GroupBy(e => e.LookupParameter("Sportify_Variant")?.AsString() ?? "").Where(g => g.Key.Length > 0).ToList();
            if (units.Count == 0) { lines.Add("posts: no kinetic unit in the project"); return 0; }

            var postSizeM = KineticsInputsFile.LoadDesign()["post_size_m"];
            var roofs = RoofIdentity.BuildingRoofs(doc).Select(r => r.get_BoundingBox(null)).Where(b => b != null).ToList();
            var added = 0;
            using var t = new Transaction(doc, "Sportify: put back the posts of the kinetic units");
            t.Start();
            var warnings = ImportWarnings.On(t);                     // an unattended run has nobody to click OK on a warning
            SportifySharedParameters.EnsureBound(doc);                // as a placement does: without it the posts carried no Sportify tags (found in the first test)
            // where a post already stands (two pergolas side by side share a corner) no second one goes: found on the first try, Revit's
            // "identical instances in the same place" for pergola_3 and pergola_4, which meet at x = 10 m
            var standing = new List<XYZ>();
            foreach (var unit in units)
            {
                if (unit.Any(e => Role(e) == "post")) continue;
                var rails = unit.Where(e => Role(e) == "rail").ToList();
                if (rails.Count == 0) { lines.Add($"posts: \"{unit.Key}\" has no post and no rail to put one under: left as it is"); continue; }
                var symbol = doc.GetElement(rails[0].GetTypeId()) as FamilySymbol;
                if (symbol == null) continue;
                if (!symbol.IsActive) symbol.Activate();
                var index = unit.Count() + 1;
                var here = 0;
                foreach (var rail in rails)
                {
                    var pts = AdaptiveComponentInstanceUtils.GetInstancePlacementPointElementRefIds(rail)
                        .Select(id => (doc.GetElement(id) as ReferencePoint)?.Position).Where(p => p != null).Select(p => p!).ToList();
                    if (pts.Count != 8) continue;
                    var ends = new[] { pts.Take(4).ToList(), pts.Skip(4).ToList() };
                    var along = Mean(ends[1]) - Mean(ends[0]);
                    var horizontal = new XYZ(along.X, along.Y, 0);
                    if (horizontal.GetLength() < 1e-6) continue;
                    horizontal = horizontal.Normalize();
                    foreach (var end in ends)
                    {
                        var c = Mean(end);
                        var top = end.Min(p => p.Z);                                         // the rail's underside at this end
                        var under = roofs.Where(b => c.X >= b!.Min.X && c.X <= b.Max.X && c.Y >= b.Min.Y && c.Y <= b.Max.Y && b.Max.Z < top - 0.5 * FeetPerMetre)
                                         .Select(b => b!.Max.Z).DefaultIfEmpty(double.NaN).Max();
                        if (double.IsNaN(under)) continue;                                  // no slab under it: no post invented
                        if (standing.Any(p => Math.Abs(p.X - c.X) < 0.15 * FeetPerMetre && Math.Abs(p.Y - c.Y) < 0.15 * FeetPerMetre && Math.Abs(p.Z - top) < 0.3 * FeetPerMetre)) continue;
                        standing.Add(new XYZ(c.X, c.Y, top));
                        var post = new BarPlan
                        {
                            Role = "post", Dynamic = false, SizeU = postSizeM, SizeV = postSizeM,
                            P0 = new V3(c.X / FeetPerMetre, c.Y / FeetPerMetre, under / FeetPerMetre),
                            P1 = new V3(c.X / FeetPerMetre, c.Y / FeetPerMetre, top / FeetPerMetre),
                            U = new V3(horizontal.X, horizontal.Y, 0),
                        };
                        var corners = post.Corners().Select(v => new XYZ(v.X * FeetPerMetre, v.Y * FeetPerMetre, v.Z * FeetPerMetre)).ToList();
                        var instance = AdaptiveUnitPlacer.PlaceAdaptive(doc, symbol, corners);
                        AdaptiveUnitPlacer.TagPart(instance, "post", unit.Key, index++, rail.WorksetId, rail.LookupParameter("Sportify_RoofId")?.AsString() ?? "");     // the rail's own value: one unit, one roof
                        try { instance.CreatedPhaseId = rail.CreatedPhaseId; } catch (Exception) { /* the template's phase */ }
                        here++;
                    }
                }
                added += here;
                lines.Add($"posts: \"{unit.Key}\": {here} post(s) put back under its {rails.Count} rail(s), where they stood");
            }
            t.Commit();
            warnings.Log("kinetics", "putting back the posts");
            SportifyLog.Info("kinetics", $"{added} post(s) of kinetic units put back");
            return added;
        }
    }
}
