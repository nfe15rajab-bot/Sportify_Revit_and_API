using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Sportify.Simulation.Sun;

namespace SportfyRevit
{
    /// <summary>
    /// Kinetics' own family: a single flat blade for the louvre pergola the sun &amp; shade analysis already recommends
    /// (SunEquipmentDto with key "pergola"). One family per blade LENGTH (= the pergola's own depth, which varies by
    /// size — EquipmentType.Sizes has a couple of variants), the same "bake the size into the cached family" approach
    /// SportifyFamilyGenerator uses for placement pieces, reusing the same Generic Model template and on-disk cache
    /// folder so a mechanical-engineering-authored family can drop into the same slot later (see LoadFamiliesCommand:
    /// a family the mechanical engineer loads and names "SportifyKineticLouvreBlade__L&lt;cm&gt;_C&lt;mm&gt;_T&lt;mm&gt;" —
    /// length, chord and thickness — is found here first, ahead of ever generating a placeholder blade of that section).
    ///
    /// Three steps, one per Kinetics command: FindLoaded ("Choose applicable family"), GenerateAndLoad ("Generate
    /// applicable family"), PlaceAdaptation ("Import analysis adaptation" — arrays blades across the pergola's width,
    /// each rotated to the state's louvre opening).
    /// </summary>
    internal static class SportifyKineticFamilyBuilder
    {
        internal const string KineticsCategoryValue = "kinetics";

        /// <summary>Length in cm, chord and thickness in mm: the blade's section is the mechanical engineer's input (SportifyKineticsInputs.json), so it is part of the family's name like its length is.</summary>
        internal static string BladeFamilyName(double lengthM, double chordM, double thicknessM) =>
            "SportifyKineticLouvreBlade__L" + (int)Math.Round(lengthM * 100.0) + "_C" + (int)Math.Round(chordM * 1000.0) + "_T" + (int)Math.Round(thicknessM * 1000.0);

        /// <summary>Step 1: a family with exactly this blade's name already loaded in the project — a Generate that already ran, or one the mechanical engineer loaded themselves under the same naming convention.</summary>
        internal static FamilySymbol? FindLoaded(Document doc, double lengthM, double chordM, double thicknessM)
        {
            var name = BladeFamilyName(lengthM, chordM, thicknessM);
            foreach (var symbol in new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>())
                if (string.Equals(symbol.Family?.Name, name, StringComparison.OrdinalIgnoreCase))
                    return symbol;
            return null;
        }

        /// <summary>
        /// Step 2: generates (or reuses the on-disk cache of) a flat extruded blade of this length, loads it, and returns its type.
        /// Must run OUTSIDE any open transaction — LoadFamily/Activate manage their own, same rule as SportifyFamilyGenerator.
        /// </summary>
        internal static FamilySymbol GenerateAndLoad(Document doc, double lengthM, double chordM, double thicknessM)
        {
            var existing = FindLoaded(doc, lengthM, chordM, thicknessM);
            if (existing != null) return existing;

            var familyName = BladeFamilyName(lengthM, chordM, thicknessM);
            string rfaPath = CachePath(familyName);

            if (!File.Exists(rfaPath))
            {
                // Resolve() only returns what Prepare() found: the commands call Prepare before their transaction (a dialog is allowed then); this is the
                // no-dialog fallback for any other caller, which searches once per session and never asks.
                string? templatePath = GenericFamilyTemplateLocator.Resolve() ?? GenericFamilyTemplateLocator.Prepare(doc.Application, allowDialog: false);
                if (templatePath == null)
                    throw new InvalidOperationException(GenericFamilyTemplateLocator.FailureReason ?? "Revit's Generic Model family template was not found");

                var familyDoc = doc.Application.NewFamilyDocument(templatePath);
                try
                {
                    using (var t = new Transaction(familyDoc, "Build the Sportify kinetic louvre blade"))
                    {
                        t.Start();
                        BuildBlade(familyDoc, lengthM, chordM, thicknessM);
                        familyDoc.FamilyManager.NewType("Blade");
                        t.Commit();
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(rfaPath)!);
                    familyDoc.SaveAs(rfaPath, new SaveAsOptions { OverwriteExistingFile = true });
                }
                finally { familyDoc.Close(false); }
            }

            if (!doc.LoadFamily(rfaPath, new OverwriteFamilyLoadOptions(), out Family family) && family == null)
                throw new InvalidOperationException("Revit did not load " + rfaPath);

            var symbolId = family.GetFamilySymbolIds().FirstOrDefault();
            if (symbolId == null || symbolId == ElementId.InvalidElementId)
                throw new InvalidOperationException("the generated blade family has no type");

            var symbol = doc.GetElement(symbolId) as FamilySymbol ?? throw new InvalidOperationException("the blade family's type could not be read");
            if (!symbol.IsActive) { symbol.Activate(); doc.Regenerate(); }
            return symbol;
        }

        /// <summary>
        /// A thin flat slat, centered on the family's own origin (so a placed instance's insertion point IS its
        /// footprint's center, matching FamilyPlacementBuilder's convention): short across local X (the chord),
        /// long across local Y (lengthM — the pergola's own depth), thin up Z (the blade's thickness). Built flat/closed
        /// by construction: PlaceAdaptation applies the open-angle rotation on top of this rest position. A solid
        /// extrusion of the blade's outer section — the hollow's wall is in the mass the mechanics model reports, not in this shape.
        /// </summary>
        private static void BuildBlade(Document familyDoc, double lengthM, double chordM, double thicknessM)
        {
            double halfXFt = UnitUtils.ConvertToInternalUnits(chordM, UnitTypeId.Meters) / 2.0;
            double halfYFt = UnitUtils.ConvertToInternalUnits(lengthM, UnitTypeId.Meters) / 2.0;
            double thicknessFt = UnitUtils.ConvertToInternalUnits(thicknessM, UnitTypeId.Meters);

            var pts = new[]
            {
                new XYZ(-halfXFt, -halfYFt, 0),
                new XYZ(halfXFt, -halfYFt, 0),
                new XYZ(halfXFt, halfYFt, 0),
                new XYZ(-halfXFt, halfYFt, 0),
            };
            var loop = new CurveArray();
            for (int i = 0; i < pts.Length; i++) loop.Append(Line.CreateBound(pts[i], pts[(i + 1) % pts.Length]));
            var profile = new CurveArrArray();
            profile.Append(loop);

            var plane = Plane.CreateByNormalAndOrigin(XYZ.BasisZ, XYZ.Zero);
            var sketchPlane = SketchPlane.Create(familyDoc, plane);
            familyDoc.FamilyCreate.NewExtrusion(true, profile, sketchPlane, thicknessFt);
        }

        /// <summary>%AppData%\...\SportifyGeneratedFamilies\{family name}.rfa — the same cache folder SportifyFamilyGenerator uses, so the two never collide by name.</summary>
        private static string CachePath(string familyName) => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Autodesk", "Revit", "Addins", "2025", "SportifyGeneratedFamilies", familyName + ".rfa");

        /// <summary>
        /// Step 3: arrays the mechanics' blade count (LouvreMechanics: the width over the blade pitch) across one pergola's
        /// width, each centred on its depth and mounted at its height, opened to <paramref name="state"/>'s louvre angle.
        /// Called once per recommended pergola — the caller clears any earlier Kinetics placement ONCE, before the first
        /// call, not per piece (this does not: two pergolas would otherwise have the second call's ClearPrevious wipe out
        /// the first's just-placed blades). Must run inside a transaction.
        /// </summary>
        internal static int PlaceAdaptation(Document doc, FamilySymbol bladeSymbol, SunEquipmentDto piece, LouvreState state, WorksetId worksetId, LouvreMechanicsReport mech)
        {
            if (!bladeSymbol.IsActive) { bladeSymbol.Activate(); doc.Regenerate(); }
            SportifySharedParameters.EnsureBound(doc);

            int bladeCount = mech.BladeCount;
            double stepM = piece.WidthM / bladeCount;
            double yCenterM = piece.YM + piece.DepthM / 2.0;
            double heightFt = SportifyLayoutBuilder.FeetFromMeters(piece.HeightM);

            var turnRad = SportifyLayoutBuilder.CurrentAngleRad;
            // The blade's own long axis (local Y, before any rotation) turned by the roof's own turn — the axis the
            // open-angle tilt rotates about, so a blade on a turned roof still opens along ITS pergola's depth
            // direction, not the untouched model's Y axis. Position itself already goes through PlanToWorldFt,
            // which applies this same turn — this only re-derives the axis DIRECTION for the second rotation.
            var tiltAxisDir = new XYZ(-Math.Sin(turnRad), Math.Cos(turnRad), 0);

            int placed = 0;
            for (int i = 0; i < bladeCount; i++)
            {
                double xM = piece.XM + (i + 0.5) * stepM;
                var (xFt, yFt) = SportifyLayoutBuilder.PlanToWorldFt(xM, yCenterM);
                // the blades overlap when closed (pitch below the chord), so alternate ones sit a blade's thickness higher, lapped like a real louvre's
                var center = new XYZ(xFt, yFt, SportifyLayoutBuilder.CurrentOriginZFt + heightFt + (i % 2) * SportifyLayoutBuilder.FeetFromMeters(mech.ThicknessM));

                var instance = doc.Create.NewFamilyInstance(center, bladeSymbol, StructuralType.NonStructural);
                SportifyLayoutBuilder.SetWorkset(instance, worksetId);
                doc.Regenerate();
                MoveToElevation(doc, instance, center.Z);

                if (Math.Abs(turnRad) > 1e-9)
                    ElementTransformUtils.RotateElement(doc, instance.Id, Line.CreateBound(center, center + XYZ.BasisZ), turnRad);

                var openRad = state.LouvreOpenAngleDeg * Math.PI / 180.0;
                if (Math.Abs(openRad) > 1e-9)
                    ElementTransformUtils.RotateElement(doc, instance.Id, Line.CreateBound(center, center + tiltAxisDir), openRad);

                SportifySharedParameters.SetValues(instance, category: KineticsCategoryValue, typeId: "louvre_blade",
                    variant: piece.Key, qualityLevel: null, norm: "LouvreActuationModel + LouvreMechanics (screening, PRELIMINARY until the inputs are entered)",
                    lengthM: piece.DepthM, widthM: mech.ChordM, referenceMaterial: "aluminium extrusion (assumed)", referenceProvider: null,
                    qualityKey: "KINETICS_LOUVRE_" + (i + 1));

                placed++;
            }
            return placed;
        }

        /// <summary>Same trick FamilyPlacementBuilder uses: a family instance ignores the Z of the point it's given (it anchors to a level), so place it, measure, and move the remaining distance.</summary>
        private static void MoveToElevation(Document doc, Element instance, double targetZFt)
        {
            var point = (instance.Location as LocationPoint)?.Point;
            if (point == null) return;
            var deltaZ = targetZFt - point.Z;
            if (Math.Abs(deltaZ) > 1e-6)
                ElementTransformUtils.MoveElement(doc, instance.Id, new XYZ(0, 0, deltaZ));
        }

        /// <summary>Every Generic Model instance this project already carries with Sportify_Category = "kinetics" — an earlier Kinetics placement.</summary>
        internal static void ClearPrevious(Document doc)
        {
            var toDelete = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_GenericModel).WhereElementIsNotElementType()
                .Where(el => (el.LookupParameter("Sportify_Category")?.AsString()) == KineticsCategoryValue)
                .Select(el => el.Id).ToList();
            if (toDelete.Count > 0) doc.Delete(toDelete);
        }

        /// <summary>Always accepts/overwrites — same rule as SportifyFamilyGenerator's loader (this never runs from an unattended sync loop today, but nothing here should ever block on a dialog either).</summary>
        private sealed class OverwriteFamilyLoadOptions : IFamilyLoadOptions
        {
            public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues) { overwriteParameterValues = true; return true; }
            public bool OnSharedFamilyFound(Family sharedFamily, bool familyInUse, out FamilySource source, out bool overwriteParameterValues)
            { source = FamilySource.Family; overwriteParameterValues = true; return true; }
        }
    }

    /// <summary>The one actuation state PlaceAdaptation builds the model at — Revit shows a single static placement, not a sweep; the isolated video (separate command) is what shows all of LouvreActuationModel's states in motion.</summary>
    internal sealed class LouvreState
    {
        public string Label = "";
        public double LouvreOpenAngleDeg;
    }
}
