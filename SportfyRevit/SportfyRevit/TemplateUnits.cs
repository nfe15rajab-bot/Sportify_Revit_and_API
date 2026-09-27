using Autodesk.Revit.DB;

namespace SportfyRevit
{
    /// <summary>
    /// The German norms are metric: plans in meters, areas in m², volumes in m³, angles in degrees. A project that starts imperial (feet and inches, from an imperial Revit template)
    /// is converted when the Sportify template is applied: its units are set to those of Revit's German BIM template, spec by spec, so the result is exactly what a project from the German template
    /// has. (Only the display units change; the model's geometry is stored in Revit's internal feet whatever the units, so nothing moves or is rescaled.) A project that is metric already is left alone.
    /// </summary>
    internal static class TemplateUnits
    {
        public static bool IsImperial(Document doc)
        {
            try { return UnitRules.IsImperialUnitId(doc.GetUnits().GetFormatOptions(SpecTypeId.Length).GetUnitTypeId().TypeId); }
            catch (Exception) { return false; }
        }

        /// <summary>The length unit of a document, for the messages ("meters", "feet and fractional inches").</summary>
        public static string LengthUnitLabel(Document doc)
        {
            try { return LabelUtils.GetLabelForUnit(doc.GetUnits().GetFormatOptions(SpecTypeId.Length).GetUnitTypeId()); }
            catch (Exception) { return "?"; }
        }

        /// <summary>Converts an imperial project to the German template's units. Call inside a transaction. Returns true if it was converted.</summary>
        public static bool EnsureMetric(Document doc, List<string> made, List<string> notes)
        {
            if (!IsImperial(doc)) return false;
            var before = LengthUnitLabel(doc);

            // what the German template has for every measurable spec
            var wanted = new List<(ForgeTypeId Spec, ForgeTypeId Unit, ForgeTypeId Symbol, double Accuracy)>();
            var path = TemplateSource.GermanTemplatePath(doc.Application);
            if (path != null)
            {
                Document? source = null;
                try
                {
                    source = doc.Application.OpenDocumentFile(path);
                    var units = source.GetUnits();
                    foreach (var spec in UnitUtils.GetAllMeasurableSpecs())
                    {
                        var fo = units.GetFormatOptions(spec);
                        if (fo.UseDefault) continue;
                        wanted.Add((spec, fo.GetUnitTypeId(), fo.GetSymbolTypeId(), fo.Accuracy));
                    }
                }
                catch (Exception ex) { notes.Add("The German template's units could not be read: " + ex.Message.Split('\n')[0]); }
                finally { try { source?.Close(false); } catch (Exception) { /* already closed */ } }
            }
            else notes.Add("Revit's German BIM template is not installed: the units are set to plain metric (meters, m², m³, degrees) instead.");

            var target = doc.GetUnits();
            int set = 0;
            if (wanted.Count > 0)
            {
                foreach (var (spec, unit, symbol, accuracy) in wanted)
                {
                    try { target.SetFormatOptions(spec, new FormatOptions(unit, symbol) { Accuracy = accuracy }); set++; }
                    catch (Exception) { /* a spec Revit will not take here: the others still convert */ }
                }
            }
            else
            {
                foreach (var (spec, unit) in new[] { (SpecTypeId.Length, UnitTypeId.Meters), (SpecTypeId.Area, UnitTypeId.SquareMeters), (SpecTypeId.Volume, UnitTypeId.CubicMeters), (SpecTypeId.Angle, UnitTypeId.Degrees) })
                {
                    try { target.SetFormatOptions(spec, new FormatOptions(unit)); set++; }
                    catch (Exception) { }
                }
            }
            doc.SetUnits(target);
            var after = LengthUnitLabel(doc);
            made.Add($"Units: {before} -> {after} (and {set - 1} other unit setting(s)), converted from imperial to the German template's");
            SportifyLog.Info("templates", $"units converted: length {before} -> {after}, {set} setting(s) set");
            return true;
        }
    }
}
