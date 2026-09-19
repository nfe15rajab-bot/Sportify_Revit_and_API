using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;

namespace SportfyRevit
{
    /// <summary>
    /// The Revit calls that read what stands on, opens through and surrounds the pushed roof: the openings in its top face, the stairs,
    /// cores and doors that reach it, the parapets and railings along its edge, the roof drains, the build-up of the slab and the
    /// levels. Everything is handed to RoofFeaturesGeometry (Revit-free, tested) as plain records in metres and model coordinates.
    ///
    /// Read-only and best-effort like the rest of the push: each family of things is read on its own, and one that cannot be read
    /// (an unusual family, an API that throws for an element) costs only itself, with a note for the designer. The searches are
    /// bounding-box filtered to the roof's neighbourhood so a large model is not walked element by element.
    ///
    /// What is decided by NAME or HEIGHT rather than by a Revit class is said here and in the notes: roof drains (Revit has no roof-drain
    /// category: they are plumbing fixtures, generic models or equipment whose family names say drain, Abfluss, Gully ...), and whether a
    /// wall is a parapet (it stands on the roof and is at most 2 m high) or a door leads to the roof (its base is at the roof's top face).
    /// Not tested in a live Revit session.
    /// </summary>
    internal static class RoofFeatureCollector
    {
        /// <summary>Walls and railings up to this height above the roof's top face are taken as its edge protection; taller walls are a stair house or a facade.</summary>
        const double EdgeElementMaxHeightM = 2.0;

        /// <summary>Elements this far above or below the roof's top face are still looked for (the filter box).</summary>
        const double SearchAboveM = 6.0, SearchBelowM = 4.0;

        static readonly string[] DrainNames =
        {
            "drain", "abfluss", "gully", "gulli", "entw", "einlauf", "ablauf", "overflow", "notüberlauf", "notueberlauf", "überlauf", "ueberlauf", "scupper", "speier",
        };

        internal sealed class Collected
        {
            public List<RoofFeaturesGeometry.PointM> Outline = new();
            public List<RoofFeaturesGeometry.OpeningLoop> Openings = new();
            public List<RoofFeaturesGeometry.EntryRecord> Entries = new();
            public List<RoofFeaturesGeometry.EdgeElement> EdgeElements = new();
            public List<RoofFeaturesGeometry.DrainRecord> Drains = new();
            public RoofFeaturesGeometry.SlabRecord? Slab;
            public List<RoofFeaturesGeometry.LevelRecord> Levels = new();
            public string? RoofLevelName;
            public List<string> Notes = new();
        }

        static double M(double feet) => UnitUtils.ConvertFromInternalUnits(feet, UnitTypeId.Meters);
        static double Ft(double metres) => UnitUtils.ConvertToInternalUnits(metres, UnitTypeId.Meters);

        public static Collected Collect(Document doc, Element roofElement, PlanarFace? topFace, IList<XYZ>? outline, double roofTopFt, BoundingBoxXYZ roofBox)
        {
            var c = new Collected();
            var roofTopM = M(roofTopFt);

            if (outline != null) c.Outline = outline.Select(p => new RoofFeaturesGeometry.PointM(M(p.X), M(p.Y))).ToList();

            var box = new Outline(
                new XYZ(roofBox.Min.X - Ft(RoofFeaturesGeometry.EntryReachM), roofBox.Min.Y - Ft(RoofFeaturesGeometry.EntryReachM), roofTopFt - Ft(SearchBelowM)),
                new XYZ(roofBox.Max.X + Ft(RoofFeaturesGeometry.EntryReachM), roofBox.Max.Y + Ft(RoofFeaturesGeometry.EntryReachM), roofTopFt + Ft(SearchAboveM)));
            var near = new BoundingBoxIntersectsFilter(box);

            Try(c, "openings", () => ReadOpenings(c, topFace));
            Try(c, "stairs", () => ReadStairs(c, doc, near, roofTopM));
            Try(c, "cores", () => ReadCores(c, doc, near, roofTopM));
            Try(c, "doors", () => ReadDoors(c, doc, near, roofTopM));
            Try(c, "parapets", () => ReadWalls(c, doc, near, roofTopM));
            Try(c, "railings", () => ReadRailings(c, doc, near, roofTopM));
            Try(c, "drains", () => ReadDrains(c, doc, near, roofTopM));
            Try(c, "slab", () => ReadSlab(c, doc, roofElement));
            Try(c, "levels", () => ReadLevels(c, doc, roofElement));
            return c;
        }

        static void Try(Collected c, string what, Action read)
        {
            try { read(); }
            catch (Exception ex) { c.Notes.Add($"The {what} could not be read ({ex.Message})."); }
        }

        // ------------------------------------------------------------------ openings

        /// <summary>The loops of the top face other than the outer one: holes in the roof (skylights, shafts, rooflights). Arcs are tessellated.</summary>
        static void ReadOpenings(Collected c, PlanarFace? face)
        {
            if (face == null) return;
            var loops = face.GetEdgesAsCurveLoops();
            if (loops == null || loops.Count < 2) return;

            var outer = loops.OrderByDescending(l => Math.Abs(ExactCurveLoop.SignedArea(l))).First();
            foreach (var loop in loops)
            {
                if (ReferenceEquals(loop, outer)) continue;
                var pts = new List<RoofFeaturesGeometry.PointM>();
                foreach (var curve in loop)
                {
                    if (curve is Line) pts.Add(new RoofFeaturesGeometry.PointM(M(curve.GetEndPoint(0).X), M(curve.GetEndPoint(0).Y)));
                    else
                    {
                        var t = curve.Tessellate();
                        for (var i = 0; i < t.Count - 1; i++) pts.Add(new RoofFeaturesGeometry.PointM(M(t[i].X), M(t[i].Y)));
                    }
                }
                c.Openings.Add(new RoofFeaturesGeometry.OpeningLoop(pts));
            }
        }

        // ------------------------------------------------------------------ entries

        static void ReadStairs(Collected c, Document doc, ElementFilter near, double roofTopM)
        {
            foreach (var el in new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Stairs).WhereElementIsNotElementType().WherePasses(near))
            {
                var bb = el.get_BoundingBox(null);
                if (bb == null) continue;
                // a stair that arrives at the roof: its top is at the roof's top face, its foot well below it
                if (M(bb.Max.Z) < roofTopM - 0.6 || M(bb.Max.Z) > roofTopM + 1.5 || M(bb.Min.Z) > roofTopM - 1.0) continue;

                double x = M((bb.Min.X + bb.Max.X) / 2), y = M((bb.Min.Y + bb.Max.Y) / 2), width = 0;
                try
                {
                    if (el is Stairs stairs)
                    {
                        // the arrival point: the end of the highest run's path
                        StairsRun? top = null;
                        foreach (var id in stairs.GetStairsRuns())
                            if (doc.GetElement(id) is StairsRun r && (top == null || r.TopElevation > top.TopElevation)) top = r;
                        var path = top?.GetStairsPath();
                        if (path != null && path.Any())
                        {
                            var end = path.Last().GetEndPoint(1);
                            x = M(end.X); y = M(end.Y);
                        }
                        if (top != null) width = M(top.ActualRunWidth);
                    }
                }
                catch (Exception) { /* keep the bounding box's centre */ }
                c.Entries.Add(new RoofFeaturesGeometry.EntryRecord("stair", el.Name, x, y, width, el.Id.Value));
            }
        }

        static readonly string[] CoreNames = { "elevator", "lift", "aufzug", "fahrstuhl" };

        /// <summary>
        /// Lifts that reach the roof. Revit models a lift as a family of no fixed category (the built-in lift category is rarely used), so
        /// these are found by family name in the equipment and generic-model categories, and by the extent that runs from well below to about the roof's top face.
        /// </summary>
        static void ReadCores(Collected c, Document doc, ElementFilter near, double roofTopM)
        {
            var categories = new List<BuiltInCategory> { BuiltInCategory.OST_Elev, BuiltInCategory.OST_SpecialityEquipment, BuiltInCategory.OST_GenericModel, BuiltInCategory.OST_MechanicalEquipment };
            foreach (var el in new FilteredElementCollector(doc).WherePasses(new ElementMulticategoryFilter(categories)).WhereElementIsNotElementType().WherePasses(near))
            {
                var label = ((el is FamilyInstance fi ? fi.Symbol?.FamilyName ?? "" : "") + " " + el.Name).ToLowerInvariant();
                if (el.Category?.Id.Value != (long)BuiltInCategory.OST_Elev && !CoreNames.Any(n => label.Contains(n))) continue;

                var bb = el.get_BoundingBox(null);
                if (bb == null) continue;
                // a lift that serves the roof: it runs from well below to (about) the roof's top face
                if (M(bb.Max.Z) < roofTopM - 1.2 || M(bb.Min.Z) > roofTopM - 2.0) continue;
                c.Entries.Add(new RoofFeaturesGeometry.EntryRecord("core", el.Name, M((bb.Min.X + bb.Max.X) / 2), M((bb.Min.Y + bb.Max.Y) / 2), 0, el.Id.Value));
            }
        }

        static void ReadDoors(Collected c, Document doc, ElementFilter near, double roofTopM)
        {
            foreach (var fi in new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Doors).WhereElementIsNotElementType().WherePasses(near).OfType<FamilyInstance>())
            {
                if (fi.Location is not LocationPoint lp) continue;
                // a door onto the roof stands ON it (a stair house, a parapet); a door of the top storey is below the roof's top face
                var baseZ = M(lp.Point.Z);
                if (baseZ < roofTopM - 0.15 || baseZ > roofTopM + 1.2) continue;

                double width = 0;
                try { width = M(fi.Symbol.get_Parameter(BuiltInParameter.DOOR_WIDTH)?.AsDouble() ?? 0); } catch (Exception) { /* width unknown */ }
                c.Entries.Add(new RoofFeaturesGeometry.EntryRecord("door", fi.Symbol.FamilyName, M(lp.Point.X), M(lp.Point.Y), width, fi.Id.Value));
            }
        }

        // ------------------------------------------------------------------ edge: parapets and railings

        static void ReadWalls(Collected c, Document doc, ElementFilter near, double roofTopM)
        {
            foreach (var wall in new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Walls).WhereElementIsNotElementType().WherePasses(near).OfType<Wall>())
            {
                var bb = wall.get_BoundingBox(null);
                if (bb == null || wall.Location is not LocationCurve lc) continue;
                var height = M(bb.Max.Z) - roofTopM;
                // stands on the roof (its base at the top face or dipping into the slab), and is low
                if (M(bb.Min.Z) > roofTopM + 0.3 || M(bb.Min.Z) < roofTopM - 1.5 || height < 0.2 || height > EdgeElementMaxHeightM) continue;
                AddSegments(c, "parapet", wall.Name, lc.Curve, height, M(wall.Width));
            }
        }

        static void ReadRailings(Collected c, Document doc, ElementFilter near, double roofTopM)
        {
            foreach (var railing in new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Railings).WhereElementIsNotElementType().WherePasses(near).OfType<Railing>())
            {
                var bb = railing.get_BoundingBox(null);
                if (bb == null) continue;
                var height = M(bb.Max.Z) - roofTopM;
                if (M(bb.Min.Z) > roofTopM + 0.3 || M(bb.Min.Z) < roofTopM - 0.6 || height < 0.5 || height > EdgeElementMaxHeightM) continue;
                foreach (var curve in railing.GetPath()) AddSegments(c, "railing", railing.Name, curve, height, 0.05);
            }
        }

        static void AddSegments(Collected c, string kind, string name, Curve curve, double heightM, double thicknessM)
        {
            if (curve is Line)
            {
                var a = curve.GetEndPoint(0); var b = curve.GetEndPoint(1);
                c.EdgeElements.Add(new RoofFeaturesGeometry.EdgeElement(kind, name, M(a.X), M(a.Y), M(b.X), M(b.Y), heightM, thicknessM));
                return;
            }
            var t = curve.Tessellate();
            for (var i = 0; i < t.Count - 1; i++)
                c.EdgeElements.Add(new RoofFeaturesGeometry.EdgeElement(kind, name, M(t[i].X), M(t[i].Y), M(t[i + 1].X), M(t[i + 1].Y), heightM, thicknessM));
        }

        // ------------------------------------------------------------------ drains

        static void ReadDrains(Collected c, Document doc, ElementFilter near, double roofTopM)
        {
            var categories = new List<BuiltInCategory>
            {
                BuiltInCategory.OST_PlumbingFixtures, BuiltInCategory.OST_GenericModel, BuiltInCategory.OST_MechanicalEquipment,
                BuiltInCategory.OST_PipeAccessory, BuiltInCategory.OST_SpecialityEquipment,
            };
            foreach (var fi in new FilteredElementCollector(doc).WherePasses(new ElementMulticategoryFilter(categories)).WhereElementIsNotElementType().WherePasses(near).OfType<FamilyInstance>())
            {
                if (fi.Location is not LocationPoint lp) continue;
                var z = M(lp.Point.Z);
                if (z < roofTopM - 0.6 || z > roofTopM + 0.6) continue;

                var label = ((fi.Symbol?.FamilyName ?? "") + " " + (fi.Symbol?.Name ?? "")).ToLowerInvariant();
                if (!DrainNames.Any(n => label.Contains(n))) continue;

                var kind = label.Contains("overflow") || label.Contains("notü") || label.Contains("notue") || label.Contains("überlauf") || label.Contains("ueberlauf") ? "overflow"
                         : label.Contains("scupper") || label.Contains("speier") ? "scupper" : "drain";
                c.Drains.Add(new RoofFeaturesGeometry.DrainRecord(kind, fi.Symbol?.FamilyName ?? fi.Name, M(lp.Point.X), M(lp.Point.Y), fi.Id.Value));
            }
        }

        // ------------------------------------------------------------------ slab and levels

        static void ReadSlab(Collected c, Document doc, Element roofElement)
        {
            if (doc.GetElement(roofElement.GetTypeId()) is not HostObjAttributes type) return;
            var structure = type.GetCompoundStructure();
            if (structure == null) return;

            var layers = new List<RoofFeaturesGeometry.LayerRecord>();
            foreach (var layer in structure.GetLayers())
            {
                var material = layer.MaterialId != ElementId.InvalidElementId ? (doc.GetElement(layer.MaterialId) as Material)?.Name ?? "" : "";
                layers.Add(new RoofFeaturesGeometry.LayerRecord(layer.Function.ToString(), material, M(layer.Width)));
            }
            c.Slab = new RoofFeaturesGeometry.SlabRecord(type.Name, layers);
        }

        static void ReadLevels(Collected c, Document doc, Element roofElement)
        {
            foreach (var level in new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>())
                c.Levels.Add(new RoofFeaturesGeometry.LevelRecord(level.Name, M(level.Elevation)));

            var baseLevel = roofElement.LevelId != ElementId.InvalidElementId ? doc.GetElement(roofElement.LevelId) as Level : null;
            if (baseLevel == null)
            {
                var id = roofElement.get_Parameter(BuiltInParameter.ROOF_BASE_LEVEL_PARAM)?.AsElementId();
                if (id != null && id != ElementId.InvalidElementId) baseLevel = doc.GetElement(id) as Level;
            }
            c.RoofLevelName = baseLevel?.Name;
        }
    }
}
