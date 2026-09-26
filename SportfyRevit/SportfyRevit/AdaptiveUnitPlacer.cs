using Autodesk.Revit.DB;
using Sportify.Simulation.Sun;

namespace SportfyRevit
{
    /// <summary>What a placement made: the instances (bars and membranes) of one kinetic unit.</summary>
    internal sealed class UnitPlacement
    {
        public List<ElementId> Ids = new();
        public int Bars, Surfaces;
    }

    /// <summary>
    /// Puts a planned kinetic unit (KineticUnits: bars and membranes in a local frame) into a project as adaptive component instances: one instance per bar (its eight
    /// corner points set from the plan) and per membrane (its four corners). The tilt of a blade, the place of a mast on its track, the height of a curtain are only where the points
    /// are. Moving parts go on the dynamic furniture workset, the frame that carries them on the structure workset; every instance is tagged Sportify_Category = "kinetics"
    /// with its unit and role, so the next placement can replace it and the schedules can count it. Inside a transaction.
    /// </summary>
    internal static class AdaptiveUnitPlacer
    {
        const double FeetPerMetre = 1.0 / 0.3048;

        static XYZ Ft(V3 world) => new(world.X * FeetPerMetre, world.Y * FeetPerMetre, world.Z * FeetPerMetre);

        /// <summary>One adaptive instance with its placement points moved to <paramref name="points"/> (feet, in the order of the family's placement numbers).</summary>
        internal static FamilyInstance PlaceAdaptive(Document doc, FamilySymbol symbol, IReadOnlyList<XYZ> points)
        {
            var instance = AdaptiveComponentInstanceUtils.CreateAdaptiveComponentInstance(doc, symbol);
            var ids = AdaptiveComponentInstanceUtils.GetInstancePlacementPointElementRefIds(instance);
            if (ids.Count != points.Count) throw new InvalidOperationException($"an adaptive instance has {ids.Count} placement points, the plan gives {points.Count}");
            for (var i = 0; i < points.Count; i++)
            {
                var point = doc.GetElement(ids[i]) as ReferencePoint ?? throw new InvalidOperationException("placement point " + (i + 1) + " of an adaptive instance is not a reference point");
                point.Position = points[i];
            }
            return instance;
        }

        /// <param name="frame">The unit's local frame in the WORLD, in metres: <see cref="UnitFrame.ToWorld"/> gives the world point (metres) for a local one.</param>
        internal static UnitPlacement Place(Document doc, UnitPlan plan, UnitFrame frame, FamilySymbol barSymbol, FamilySymbol surfaceSymbol,
                                            WorksetId dynamicWorkset, WorksetId structureWorkset, string unitKey)
        {
            SportifySharedParameters.EnsureBound(doc);
            var placed = new UnitPlacement();
            var n = 0;
            foreach (var bar in plan.Bars)
            {
                var world = new BarPlan { Role = bar.Role, P0 = frame.ToWorld(bar.P0), P1 = frame.ToWorld(bar.P1), SizeU = bar.SizeU, SizeV = bar.SizeV, U = frame.DirToWorld(bar.U), Dynamic = bar.Dynamic };
                var instance = PlaceAdaptive(doc, barSymbol, world.Corners().Select(Ft).ToList());
                Tag(instance, bar.Role, unitKey, ++n, bar.Dynamic ? dynamicWorkset : structureWorkset);
                placed.Ids.Add(instance.Id); placed.Bars++;
            }
            foreach (var surface in plan.Surfaces)
            {
                var corners = new[] { surface.A, surface.B, surface.C, surface.D }.Select(frame.ToWorld).Select(Ft).ToList();
                var instance = PlaceAdaptive(doc, surfaceSymbol, corners);
                Tag(instance, surface.Role, unitKey, ++n, surface.Dynamic ? dynamicWorkset : structureWorkset);
                placed.Ids.Add(instance.Id); placed.Surfaces++;
            }
            doc.Regenerate();
            return placed;
        }

        /// <summary>
        /// Deletes an earlier placement of this kind (the generic-model instances tagged Sportify_Category = "kinetics" whose unit key starts with the kind's), and only this kind's:
        /// the fence placed yesterday stays when the louvre is placed today. Includes the single blades an earlier version placed on a pergola. Inside a transaction.
        /// </summary>
        internal static int ClearKind(Document doc, KineticKind kind)
        {
            var prefixes = kind switch
            {
                KineticKind.Overhead => new[] { "pergola", "canopy" },
                KineticKind.Slats => new[] { "slats" },
                KineticKind.Fins => new[] { "fins" },
                KineticKind.Sail => new[] { "sail" },
                _ => new[] { "fence" },
            };
            var toDelete = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_GenericModel).WhereElementIsNotElementType()
                .Where(el => el.LookupParameter("Sportify_Category")?.AsString() == SportifyKineticFamilyBuilder.KineticsCategoryValue &&
                             prefixes.Any(p => (el.LookupParameter("Sportify_Variant")?.AsString() ?? "").StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                .Select(el => el.Id).ToList();
            if (toDelete.Count > 0) doc.Delete(toDelete);
            return toDelete.Count;
        }

        static void Tag(FamilyInstance instance, string role, string unitKey, int index, WorksetId workset)
        {
            SportifyLayoutBuilder.SetWorkset(instance, workset);
            SportifySharedParameters.SetValues(instance, category: SportifyKineticFamilyBuilder.KineticsCategoryValue, typeId: role, variant: unitKey,
                qualityLevel: null, norm: "Kinetics (screening, PRELIMINARY until the inputs are entered)", lengthM: null, widthM: null,
                referenceMaterial: null, referenceProvider: null, qualityKey: unitKey + "_" + role + "_" + index);
        }
    }
}
