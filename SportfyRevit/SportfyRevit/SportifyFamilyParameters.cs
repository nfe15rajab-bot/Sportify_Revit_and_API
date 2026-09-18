using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace SportfyRevit
{
    /// <summary>
    /// The native Family/Type/Instance parameter set stamped onto every
    /// SportifyFamilyGenerator-built family — the full generalities/
    /// materials(+LCA+provider)/analysis data model (SportifyLayoutDto),
    /// not the flat 10-field set SportifySharedParameters already stamps
    /// everywhere. Deliberately separate from that class, which is left
    /// untouched as the DirectShape-placeholder-era safety net.
    ///
    /// Split Type vs. Instance the same way the data actually varies: every
    /// placement sharing one quality_key is the same Family+Type (see
    /// SportifyFamilyGenerator — one Family per quality_key), so dimensions/
    /// materials/LCA/provider data are Type parameters; a placement's own
    /// id/label and per-location analysis figures (two same-type courts at
    /// different spots on the roof have different distances to their
    /// nearest entry) are Instance parameters.
    /// </summary>
    internal static class SportifyFamilyParameters
    {
        private const string Prefix = "Sportify_";

        private sealed record Def(string Suffix, ForgeTypeId Spec, ForgeTypeId Group, bool IsInstance);

        private static readonly Def[] Definitions =
        {
            // ── Type parameters — identical for every instance of this quality_key ──
            new("QualityKey", SpecTypeId.String.Text, GroupTypeId.IdentityData, false),
            new("Category", SpecTypeId.String.Text, GroupTypeId.IdentityData, false),
            new("TypeId", SpecTypeId.String.Text, GroupTypeId.IdentityData, false),
            new("Variant", SpecTypeId.String.Text, GroupTypeId.IdentityData, false),
            new("Norm", SpecTypeId.String.Text, GroupTypeId.IdentityData, false),
            // GroupTypeId.Geometry is the ForgeTypeId for what the Properties panel still labels "Dimensions".
            new("LengthM", SpecTypeId.Length, GroupTypeId.Geometry, false),
            new("WidthM", SpecTypeId.Length, GroupTypeId.Geometry, false),
            new("AreaM2", SpecTypeId.Area, GroupTypeId.Geometry, false),
            new("QualityLevel", SpecTypeId.String.Text, GroupTypeId.Data, false),
            new("ReferenceMaterial", SpecTypeId.String.Text, GroupTypeId.Data, false),
            new("ReferenceProvider", SpecTypeId.String.Text, GroupTypeId.Data, false),
            new("Material_NormCode", SpecTypeId.String.Text, GroupTypeId.Data, false),
            new("Material_PerformanceClass", SpecTypeId.String.Text, GroupTypeId.Data, false),
            new("Material_ForceReduction", SpecTypeId.String.Text, GroupTypeId.Data, false),
            new("EmbodiedCarbonValue", SpecTypeId.Number, GroupTypeId.Data, false),
            new("EmbodiedCarbonUnit", SpecTypeId.String.Text, GroupTypeId.Data, false),
            new("EmbodiedCarbonSource", SpecTypeId.String.Text, GroupTypeId.Data, false),
            new("Provider_Country", SpecTypeId.String.Text, GroupTypeId.Data, false),
            new("Provider_Specialty", SpecTypeId.String.Text, GroupTypeId.Data, false),
            new("Provider_Website", SpecTypeId.String.Text, GroupTypeId.Data, false),

            // ── Instance parameters — can differ between two placements sharing this quality_key ──
            new("PlacementId", SpecTypeId.String.Text, GroupTypeId.IdentityData, true),
            new("Label", SpecTypeId.String.Text, GroupTypeId.IdentityData, true),
            new("FireSafety_DistanceToEntryM", SpecTypeId.Length, GroupTypeId.Data, true),
            new("FireSafety_WithinLimit", SpecTypeId.Boolean.YesNo, GroupTypeId.Data, true),
            new("WindExposure_DistanceToEdgeM", SpecTypeId.Length, GroupTypeId.Data, true),
            new("WindExposure_Exposed", SpecTypeId.Boolean.YesNo, GroupTypeId.Data, true),
            new("LiveLoad_KnPerM2", SpecTypeId.Number, GroupTypeId.Data, true),
            new("LiveLoad_WithinReference", SpecTypeId.Boolean.YesNo, GroupTypeId.Data, true),
            new("WaterRetentionPercent", SpecTypeId.Number, GroupTypeId.Data, true),
        };

        /// <summary>Called once per generated family, inside the family document's own transaction.</summary>
        public static void DefineOn(FamilyManager fm)
        {
            foreach (var d in Definitions)
            {
                if (fm.get_Parameter(Prefix + d.Suffix) != null) continue;
                fm.AddParameter(Prefix + d.Suffix, d.Group, d.Spec, d.IsInstance);
            }
        }

        /// <summary>Called once per generated family (single Type — see SportifyFamilyGenerator), right after DefineOn, still inside the family document's transaction.</summary>
        public static void SetTypeValues(FamilyManager fm, PlacementDto p)
        {
            var gen = PlacementDataHelpers.GetGeneralities(p);
            var materials = PlacementDataHelpers.GetMaterials(p);
            var matDetail = PlacementDataHelpers.GetReferenceMaterialDetail(p);
            var provDetail = PlacementDataHelpers.GetReferenceProviderDetail(p);
            var (typeId, variant, norm, unifiedLengthM, unifiedWidthM) = PlacementDataHelpers.GetUnifiedFields(p);

            double? lengthM = gen?.LengthM ?? unifiedLengthM;
            double? widthM = gen?.WidthM ?? unifiedWidthM;
            double? areaM2 = gen?.AreaM2 ?? (lengthM.HasValue && widthM.HasValue ? lengthM * widthM : null);

            TrySetText(fm, "QualityKey", p.Parameters?.QualityKey);
            TrySetText(fm, "Category", p.Category);
            TrySetText(fm, "TypeId", typeId);
            TrySetText(fm, "Variant", variant);
            TrySetText(fm, "Norm", norm);
            TrySetLengthM(fm, "LengthM", lengthM);
            TrySetLengthM(fm, "WidthM", widthM);
            TrySetAreaM2(fm, "AreaM2", areaM2);
            TrySetText(fm, "QualityLevel", materials?.QualityLevel);
            TrySetText(fm, "ReferenceMaterial", materials?.ReferenceMaterial);
            TrySetText(fm, "ReferenceProvider", materials?.ReferenceProvider);
            TrySetText(fm, "Material_NormCode", matDetail?.NormCode);
            TrySetText(fm, "Material_PerformanceClass", matDetail?.PerformanceClass);
            TrySetText(fm, "Material_ForceReduction", matDetail?.ForceReduction);
            TrySetNumber(fm, "EmbodiedCarbonValue", matDetail?.EmbodiedCarbonValue);
            TrySetText(fm, "EmbodiedCarbonUnit", matDetail?.EmbodiedCarbonUnit);
            TrySetText(fm, "EmbodiedCarbonSource", matDetail?.EmbodiedCarbonSource);
            TrySetText(fm, "Provider_Country", provDetail?.Country);
            TrySetText(fm, "Provider_Specialty", provDetail?.Specialty);
            TrySetText(fm, "Provider_Website", provDetail?.Website);
        }

        /// <summary>
        /// Called on every placed element (real generated family, a matched
        /// pre-loaded family, or the DirectShape placeholder alike — same
        /// unconditional-call pattern as SportifySharedParameters.SetValues;
        /// harmless no-op via LookupParameter returning null wherever these
        /// Sportify_* instance parameters don't exist). fireSafetyDistanceM
        /// is Revit's own CirculationEngine-computed distance for this exact
        /// placement id (null when unreachable/not computable) — authoritative
        /// over the JSON's own web-app estimate, same reasoning
        /// AnalyzeFireSafetyCommand already applies at the layout level. Live
        /// load is likewise computed fresh here (ComputeLiveLoad below)
        /// rather than trusted from the JSON. Wind exposure/water management
        /// are passed through from the export's own analysis block — Revit
        /// has no more-authoritative source for those today.
        /// </summary>
        public static void SetInstanceValues(Element instance, PlacementDto p, double? fireSafetyDistanceM)
        {
            TrySetText(instance, "PlacementId", p.Id);
            TrySetText(instance, "Label", p.Label);

            double maxTravelM = AnalysisReferenceData.GetParam("Fire Safety", "max_travel_distance_m");
            TrySetLengthM(instance, "FireSafety_DistanceToEntryM", fireSafetyDistanceM);
            TrySetYesNo(instance, "FireSafety_WithinLimit", fireSafetyDistanceM.HasValue && fireSafetyDistanceM.Value <= maxTravelM);

            var liveLoad = ComputeLiveLoad(p);
            if (liveLoad != null)
            {
                TrySetNumber(instance, "LiveLoad_KnPerM2", liveLoad.Value.KnPerM2);
                TrySetYesNo(instance, "LiveLoad_WithinReference", liveLoad.Value.WithinReference);
            }

            var wind = p.Analysis?.WindExposure;
            if (wind != null && !wind.OutOfBounds)
            {
                TrySetLengthM(instance, "WindExposure_DistanceToEdgeM", wind.DistanceToEdgeM);
                TrySetYesNo(instance, "WindExposure_Exposed", wind.Exposed ?? false);
            }

            var water = p.Analysis?.WaterManagement;
            if (water != null)
                TrySetNumber(instance, "WaterRetentionPercent", water.RetentionPercent);
        }

        /// <summary>Mirrors AnalyzeLiveLoadsCommand.cs's formula exactly — only meaningful for a sport field with a set spectator capacity.</summary>
        public static (double KnPerM2, double ReferenceKnPerM2, bool WithinReference)? ComputeLiveLoad(PlacementDto p)
        {
            if (!string.Equals(p.Category, "field", StringComparison.OrdinalIgnoreCase)) return null;
            int seats = p.Parameters?.Field?.Capacity?.Seats ?? 0;
            if (seats <= 0) return null;

            var bb = p.BoundingBox;
            if (bb == null || bb.WidthM <= 0 || bb.HeightM <= 0) return null;
            double areaM2 = bb.WidthM * bb.HeightM;

            double massPerPersonKg = AnalysisReferenceData.GetParam("Live Loads", "assumed_load_per_person_kg");
            double referenceKnPerM2 = AnalysisReferenceData.GetParam("Live Loads", "reference_capacity_kn_per_m2");
            double knPerM2 = seats * massPerPersonKg * 9.81 / areaM2 / 1000.0;
            return (knPerM2, referenceKnPerM2, knPerM2 <= referenceKnPerM2);
        }

        private static FamilyParameter? Get(FamilyManager fm, string suffix) => fm.get_Parameter(Prefix + suffix);

        private static void TrySetText(FamilyManager fm, string suffix, string? value)
        {
            if (value == null) return;
            var p = Get(fm, suffix);
            if (p == null) return;
            try { fm.Set(p, value); } catch (Exception) { /* best-effort, same as SportifySharedParameters */ }
        }

        private static void TrySetLengthM(FamilyManager fm, string suffix, double? valueM)
        {
            if (valueM == null) return;
            var p = Get(fm, suffix);
            if (p == null) return;
            try { fm.Set(p, UnitUtils.ConvertToInternalUnits(valueM.Value, UnitTypeId.Meters)); } catch (Exception) { }
        }

        private static void TrySetAreaM2(FamilyManager fm, string suffix, double? valueM2)
        {
            if (valueM2 == null) return;
            var p = Get(fm, suffix);
            if (p == null) return;
            try { fm.Set(p, UnitUtils.ConvertToInternalUnits(valueM2.Value, UnitTypeId.SquareMeters)); } catch (Exception) { }
        }

        private static void TrySetNumber(FamilyManager fm, string suffix, double? value)
        {
            if (value == null) return;
            var p = Get(fm, suffix);
            if (p == null) return;
            try { fm.Set(p, value.Value); } catch (Exception) { }
        }

        private static void TrySetText(Element el, string suffix, string? value)
        {
            if (value == null) return;
            try
            {
                var p = el.LookupParameter(Prefix + suffix);
                if (p != null && !p.IsReadOnly) p.Set(value);
            }
            catch (Exception) { }
        }

        private static void TrySetLengthM(Element el, string suffix, double? valueM)
        {
            if (valueM == null) return;
            try
            {
                var p = el.LookupParameter(Prefix + suffix);
                if (p != null && !p.IsReadOnly) p.Set(UnitUtils.ConvertToInternalUnits(valueM.Value, UnitTypeId.Meters));
            }
            catch (Exception) { }
        }

        private static void TrySetNumber(Element el, string suffix, double? value)
        {
            if (value == null) return;
            try
            {
                var p = el.LookupParameter(Prefix + suffix);
                if (p != null && !p.IsReadOnly) p.Set(value.Value);
            }
            catch (Exception) { }
        }

        private static void TrySetYesNo(Element el, string suffix, bool value)
        {
            try
            {
                var p = el.LookupParameter(Prefix + suffix);
                if (p != null && !p.IsReadOnly) p.Set(value ? 1 : 0);
            }
            catch (Exception) { }
        }
    }
}
