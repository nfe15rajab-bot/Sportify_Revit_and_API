using System.Globalization;
using Sportify.Simulation.Sun;

namespace SportfyRevit
{
    /// <summary>
    /// One unit's real parts, one row per distinct part (grouped by role, section and length — 19 identical
    /// blades is one row with Count 19, not 19 rows), with the actual dimensions the mechanics just computed —
    /// structure, the moving blade/panel/curtain, and the mechanism's own hardware (crank, actuator piston,
    /// drive motor) alike. Revit's BIM model never places that hardware (KineticsBuild.Build() keeps it
    /// CAD/video-only), but a machinist building the physical unit still needs it listed, so this reads from
    /// each unit's unfiltered reference state instead. Feeds KineticsManufacturingPdfBuilder, one call per unit.
    ///
    /// Material is filled in with a plain, honest per-role default (documented below, same "assumed" convention
    /// the rest of this app uses for a value nobody entered) rather than guessed real supplier data: Sportify has
    /// no real catalogue for aluminium extrusion, architectural fabric or a drive motor's exact model, and a
    /// wrong or fabricated spec is worse than an honestly-labelled placeholder — confirm every "assumed" entry
    /// before ordering.
    /// </summary>
    internal static class KineticsBillOfMaterials
    {
        internal readonly record struct ManufacturingRow(string Part, string Count, string LengthM, string SectionMm, string AreaM2, string Material);

        // BarPlan.Role's documented vocabulary (KineticUnitGeometry.cs): blade | fin | post | rail | rod | mast |
        // housing | bottombar | crank | piston | motor | track | carriage | curtainslat | pvpanel.
        static readonly Dictionary<string, string> RoleMaterial = new(StringComparer.OrdinalIgnoreCase)
        {
            ["blade"] = "Aluminium extrusion, anodised — assumed",
            ["fin"] = "Aluminium extrusion, anodised — assumed",
            ["post"] = "Aluminium extrusion, structural — assumed",
            ["rail"] = "Aluminium extrusion, structural — assumed",
            ["mast"] = "Aluminium tube — assumed",
            ["rod"] = "Steel push-rod, linkage — assumed",
            ["housing"] = "Steel tube, roller housing — assumed",
            ["track"] = "Aluminium ground track — assumed",
            ["carriage"] = "Steel carriage — assumed",
            ["bottombar"] = "Aluminium bottom bar — assumed",
            ["curtainslat"] = "Aluminium slat, roller curtain — assumed",
            ["pvpanel"] = "PV module, glass-glass — assumed",
            ["crank"] = "Steel crank arm, machined — assumed",
            ["piston"] = "Steel actuator rod, machined — assumed",
            ["motor"] = "Electric drive motor, off-the-shelf — assumed",
        };
        static readonly Dictionary<string, string> SurfaceMaterial = new(StringComparer.OrdinalIgnoreCase)
        {
            ["sail"] = "PTFE-coated architectural fabric — assumed",
            ["acousticpanel"] = "Absorptive acoustic panel, composite/mineral-wool core — assumed",
            ["greenpanel"] = "Trellis frame with climbing foliage — assumed",
        };
        // Display order only, structure to actuator — not the physical build sequence.
        static readonly string[] RoleOrder = { "post", "rail", "mast", "track", "housing", "bottombar", "blade", "fin", "pvpanel", "curtainslat", "carriage", "rod", "crank", "piston", "motor" };
        static int RoleOrderIndex(string role) { var i = Array.IndexOf(RoleOrder, role); return i >= 0 ? i : RoleOrder.Length; }

        static double QuadAreaM2(V3 a, V3 b, V3 c, V3 d) =>
            0.5 * (b - a).Cross(c - a).Length + 0.5 * (c - a).Cross(d - a).Length;

        /// <summary>
        /// Every real part of one unit — sourced from its first actuation state (StatePlans[0], the CAD model's
        /// own unfiltered plan; KineticsBuild.Build() only ever filters unit.Plan, the BIM-facing one, never
        /// StatePlans) — so a crank or a drive motor is listed even though Revit itself never places one. The
        /// flat "curtain" surface a net-curtain kind's geometry still carries is excluded: its curtainslat bars
        /// already list that same curtain as real individual slats, and listing both would double it.
        /// </summary>
        internal static List<ManufacturingRow> RowsFull(KineticUnit unit)
        {
            var plan = unit.StatePlans.Count > 0 ? unit.StatePlans[0] : unit.Plan;
            var rows = new List<ManufacturingRow>();

            var barGroups = plan.Bars
                .GroupBy(b => (b.Role, SecU: Math.Round(b.SizeU * 1000), SecV: Math.Round(b.SizeV * 1000), LenM: Math.Round((b.P1 - b.P0).Length, 2)))
                .OrderBy(g => RoleOrderIndex(g.Key.Role)).ThenBy(g => g.Key.LenM);
            foreach (var g in barGroups)
            {
                var mat = RoleMaterial.TryGetValue(g.Key.Role, out var m) ? m : "Aluminium extrusion — assumed";
                rows.Add(new ManufacturingRow(RoleLabel(g.Key.Role), g.Count().ToString(CultureInfo.InvariantCulture),
                    g.Key.LenM.ToString("0.00", CultureInfo.InvariantCulture),
                    g.Key.SecU.ToString("0", CultureInfo.InvariantCulture) + " x " + g.Key.SecV.ToString("0", CultureInfo.InvariantCulture),
                    "", mat));
            }
            foreach (var g in plan.Surfaces.Where(s => s.Role != "curtain").GroupBy(f => f.Role))
            {
                var areaM2 = g.Sum(f => QuadAreaM2(f.A, f.B, f.C, f.D));
                var mat = SurfaceMaterial.TryGetValue(g.Key, out var m) ? m : "Fabric — assumed";
                rows.Add(new ManufacturingRow(RoleLabel(g.Key), g.Count().ToString(CultureInfo.InvariantCulture), "", "", areaM2.ToString("0.00", CultureInfo.InvariantCulture), mat));
            }
            return rows;
        }

        internal static string RoleLabel(string role) => role switch
        {
            "blade" => "Blade", "fin" => "Fin", "post" => "Post", "rail" => "Rail", "mast" => "Mast", "rod" => "Push-rod",
            "housing" => "Roller housing", "track" => "Ground track", "carriage" => "Carriage", "bottombar" => "Bottom bar",
            "sail" => "Sail fabric", "curtainslat" => "Curtain slat", "pvpanel" => "PV panel",
            "acousticpanel" => "Acoustic panel", "greenpanel" => "Green screen panel",
            "crank" => "Crank arm", "piston" => "Actuator piston", "motor" => "Drive motor",
            _ => role.Length > 0 ? char.ToUpperInvariant(role[0]) + role[1..] : role,
        };
    }
}
