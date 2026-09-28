using System.Globalization;
using System.Text;
using Sportify.Simulation.Sun;

namespace SportfyRevit
{
    /// <summary>
    /// The kinetic units' real parts (blade, post, rail, mast, rod, roller housing, sail/curtain fabric), one row per
    /// distinct part (grouped by role, section and length — 19 identical blades is one row with Count 19, not 19
    /// rows), with the actual dimensions the mechanics just computed. CSV, same reasoning as ScheduleCsv/
    /// GenerateSchedulesCommand: no Revit parameters to group a real ViewSchedule by, and no new NuGet dependency for
    /// a real .xlsx writer.
    ///
    /// Material and the two "buy" columns are deliberately left blank rather than guessed: Sportify has no real
    /// supplier catalogue for aluminium extrusion or architectural fabric, and a wrong or fabricated link is worse
    /// than an empty one. The Material column IS filled in with a plain, honest per-role default (documented below,
    /// same "assumed" convention the rest of this app uses for a value nobody entered) so the sheet is usable as a
    /// starting point, not a source of true.
    /// </summary>
    internal static class KineticsBillOfMaterials
    {
        public const string Header = "Kind,Unit,BuildSize,PartRole,Count,LengthM,SectionMm,AreaM2,Material,BuySupplier,BuyLink,Notes";

        // BarPlan.Role's documented vocabulary (KineticUnitGeometry.cs): blade | fin | post | rail | rod | mast |
        // housing | bottombar | crank | piston | motor | track | carriage | curtainslat. Only the non-Detail,
        // non-CadOnly ones ever reach here (unit.Plan is already filtered) — in practice, for the three built kinds,
        // that is blade, post, rail, mast, rod and housing (a fence's roller housing; an actuator's own housing is
        // Detail and never appears here).
        static readonly Dictionary<string, string> RoleMaterial = new(StringComparer.OrdinalIgnoreCase)
        {
            ["blade"] = "Aluminium extrusion, anodised — assumed",
            ["fin"] = "Aluminium extrusion, anodised — assumed",
            ["post"] = "Aluminium extrusion, structural — assumed",
            ["rail"] = "Aluminium extrusion, structural — assumed",
            ["mast"] = "Aluminium tube — assumed",
            ["rod"] = "Steel rod, linkage — assumed",
            ["housing"] = "Steel tube, roller housing — assumed",
            ["track"] = "Aluminium ground track — assumed",
            ["carriage"] = "Steel carriage — assumed",
            ["bottombar"] = "Aluminium bottom bar — assumed",
        };
        static readonly Dictionary<string, string> SurfaceMaterial = new(StringComparer.OrdinalIgnoreCase)
        {
            ["sail"] = "PTFE-coated architectural fabric — assumed",
            ["curtain"] = "HDPE mesh net fabric — assumed",
        };
        static readonly HashSet<string> DetailRoles = new(StringComparer.OrdinalIgnoreCase) { "crank", "piston", "motor", "curtainslat" };

        static double QuadAreaM2(V3 a, V3 b, V3 c, V3 d) =>
            0.5 * (b - a).Cross(c - a).Length + 0.5 * (c - a).Cross(d - a).Length;

        /// <summary>The BOM's rows only (no header), so the caller can also report a row count without re-parsing the CSV text.</summary>
        internal static List<string[]> Rows(IEnumerable<KineticUnit> units)
        {
            var rows = new List<string[]>();
            foreach (var u in units)
            {
                var kindLabel = KineticKinds.Get(u.Host.Kind).Label;
                var unitLabel = u.Host.Name;
                var buildSize = u.Host.LengthM.ToString("0.0", CultureInfo.InvariantCulture) + " x " + u.Host.DepthM.ToString("0.0", CultureInfo.InvariantCulture) + " m";

                var barGroups = u.Plan.Bars
                    .Where(b => !DetailRoles.Contains(b.Role))
                    .GroupBy(b => (b.Role, SecU: Math.Round(b.SizeU * 1000), SecV: Math.Round(b.SizeV * 1000), LenM: Math.Round((b.P1 - b.P0).Length, 2)));
                foreach (var g in barGroups)
                {
                    var mat = RoleMaterial.TryGetValue(g.Key.Role, out var m) ? m : "Aluminium extrusion — assumed";
                    rows.Add(new[]
                    {
                        kindLabel, unitLabel, buildSize, RoleLabel(g.Key.Role), g.Count().ToString(),
                        g.Key.LenM.ToString("0.00", CultureInfo.InvariantCulture),
                        g.Key.SecU.ToString("0", CultureInfo.InvariantCulture) + " x " + g.Key.SecV.ToString("0", CultureInfo.InvariantCulture), "",
                        mat, "", "", "",
                    });
                }

                foreach (var g in u.Plan.Surfaces.GroupBy(f => f.Role))
                {
                    var areaM2 = g.Sum(f => QuadAreaM2(f.A, f.B, f.C, f.D));
                    var mat = SurfaceMaterial.TryGetValue(g.Key, out var m) ? m : "Fabric — assumed";
                    rows.Add(new[]
                    {
                        kindLabel, unitLabel, buildSize, RoleLabel(g.Key), g.Count().ToString(),
                        "", "", areaM2.ToString("0.00", CultureInfo.InvariantCulture),
                        mat, "", "", "",
                    });
                }
            }
            return rows;
        }

        internal static string RoleLabel(string role) => role switch
        {
            "blade" => "Blade", "fin" => "Fin", "post" => "Post", "rail" => "Rail", "mast" => "Mast", "rod" => "Rod",
            "housing" => "Roller housing", "track" => "Ground track", "carriage" => "Carriage", "bottombar" => "Bottom bar",
            "sail" => "Sail fabric", "curtain" => "Curtain fabric",
            _ => role.Length > 0 ? char.ToUpperInvariant(role[0]) + role[1..] : role,
        };

        public static string Build(IEnumerable<KineticUnit> units)
        {
            var sb = new StringBuilder();
            sb.AppendLine(Header);
            foreach (var row in Rows(units)) sb.AppendLine(string.Join(",", row.Select(ScheduleCsv.Field)));
            return sb.ToString();
        }
    }
}
