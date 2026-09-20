using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace SportfyRevit
{
    /// <summary>
    /// What a push from Revit carries. The roof's outline and size always go (they fix the plan's frame, see RoofFrame); the rest is chosen
    /// from the "Push to Sportify" drop-down in the ribbon, so a designer who only changed the stairs does not have to walk the whole model
    /// again, and a slow read (a big model's equipment) can be left out.
    /// </summary>
    [Flags]
    internal enum RoofPushScope
    {
        None = 0,
        /// <summary>The roof's outline, size, height above ground and origin: always pushed.</summary>
        Roof = 1,
        /// <summary>The structural grid and columns, the beams under the roof and the bearing walls that hold it up.</summary>
        Structure = 2,
        /// <summary>Stairs, lifts and doors by which people reach the roof.</summary>
        Entries = 4,
        /// <summary>Holes in the roof's top face: skylights, shafts, rooflights.</summary>
        Openings = 8,
        /// <summary>Parapets and railings along the edge, and the walls standing on the roof (which shade it).</summary>
        Edge = 16,
        /// <summary>Roof drains, overflows and scuppers.</summary>
        Drains = 32,
        /// <summary>Plant and equipment standing on the roof (mechanical, electrical): their footprint, height and weight.</summary>
        Equipment = 64,
        /// <summary>The build-up of the slab (its structural thickness is what the resonance estimate needs) and the levels.</summary>
        SlabLevels = 128,
        All = Roof | Structure | Entries | Openings | Edge | Drains | Equipment | SlabLevels,
    }

    internal static class RoofPushScopes
    {
        static readonly (RoofPushScope Scope, string Name, string Text)[] Parts =
        {
            (RoofPushScope.Roof, "roof", "roof outline and size"),
            (RoofPushScope.Structure, "structure", "structure (grid, columns, beams, bearing walls)"),
            (RoofPushScope.Entries, "entries", "entries (stairs, lifts, doors)"),
            (RoofPushScope.Openings, "openings", "openings"),
            (RoofPushScope.Edge, "edge", "edge and walls on the roof (parapets, railings, shading walls)"),
            (RoofPushScope.Drains, "drains", "drains"),
            (RoofPushScope.Equipment, "equipment", "equipment on the roof"),
            (RoofPushScope.SlabLevels, "slab_levels", "slab build-up and levels"),
        };

        /// <summary>The machine names of what is in the scope, in a fixed order (they travel in the payload as "pushed_scope").</summary>
        public static List<string> Names(RoofPushScope scope) => Parts.Where(p => scope.HasFlag(p.Scope)).Select(p => p.Name).ToList();

        public static RoofPushScope FromNames(IEnumerable<string?>? names)
        {
            var scope = RoofPushScope.None;
            foreach (var n in names ?? Enumerable.Empty<string?>())
                foreach (var p in Parts)
                    if (string.Equals(p.Name, n, StringComparison.OrdinalIgnoreCase)) scope |= p.Scope;
            return scope;
        }

        /// <summary>The scope in words for a dialog: "roof outline and size, entries (stairs, lifts, doors)".</summary>
        public static string Describe(RoofPushScope scope) => string.Join(", ", Parts.Where(p => scope.HasFlag(p.Scope)).Select(p => p.Text));

        public static bool IsEverything(RoofPushScope scope) => (scope & RoofPushScope.All) == RoofPushScope.All;
    }

    /// <summary>
    /// Puts a push that carries only some of the model's data onto the roof pushed before it, so the web app always receives the roof as a
    /// whole and needs no merging of its own. Free of Revit types (tested in Sportify.Simulation/Tools/AddinCheck).
    ///
    /// A push of the SAME roof (same origin, size and turn) keeps what earlier pushes brought and replaces only what this one carries; a push
    /// of a DIFFERENT roof starts again, because the structure and features of one roof mean nothing on another. "pushed_scope" says what the
    /// roof now has, for the app to show and for the designer to see what is still missing.
    /// </summary>
    internal static class RoofPushMerge
    {
        const double FrameToleranceM = 0.02;
        const double FrameToleranceDeg = 0.01;

        /// <summary>The keys of the roof that always come from the newest push: its outline, size, height and origin.</summary>
        static readonly string[] RoofKeys =
        {
            "length_m", "width_m", "rotation_deg", "boundary_m", "origin_x_m", "origin_y_m", "origin_z_m", "source_element_name", "height_above_ground_m", "height_source",
        };

        /// <summary>features keys by the scope that brings them.</summary>
        static readonly (RoofPushScope Scope, string[] Keys)[] FeatureKeys =
        {
            (RoofPushScope.Entries, new[] { "entries" }),
            (RoofPushScope.Openings, new[] { "openings" }),
            (RoofPushScope.Edge, new[] { "edges", "obstacles" }),
            (RoofPushScope.Drains, new[] { "drains" }),
            (RoofPushScope.Equipment, new[] { "equipment" }),
            (RoofPushScope.SlabLevels, new[] { "slab", "levels" }),
        };

        /// <param name="existingJson">The payload the server holds (the roof pushed before), or null.</param>
        /// <param name="newJson">The payload of this push: the roof's own keys and the parts of the scope.</param>
        /// <param name="sameRoof">Whether the earlier push was of the same roof (its other parts were kept).</param>
        public static string Merge(string? existingJson, string newJson, RoofPushScope scope, out bool sameRoof)
        {
            sameRoof = false;
            var fresh = JsonNode.Parse(newJson)?.AsObject();
            var newRoof = fresh?["roof"]?.AsObject();
            if (fresh == null || newRoof == null) return newJson;

            var oldRoof = TryParse(existingJson)?["roof"] as JsonObject;
            if (RoofPushScopes.IsEverything(scope) || oldRoof == null || !SameFrame(oldRoof, newRoof))
            {
                newRoof["pushed_scope"] = ToArray(RoofPushScopes.Names(scope | RoofPushScope.Roof));
                return fresh.ToJsonString();
            }
            sameRoof = true;

            var merged = (JsonObject)oldRoof.DeepClone();
            foreach (var key in RoofKeys)
            {
                if (newRoof.ContainsKey(key)) merged[key] = newRoof[key]?.DeepClone();
                else merged.Remove(key);
            }

            if (scope.HasFlag(RoofPushScope.Structure))
            {
                if (newRoof["structure"] != null) merged["structure"] = newRoof["structure"]!.DeepClone();
                else merged.Remove("structure");
            }

            var features = merged["features"] as JsonObject ?? new JsonObject { ["source"] = "revit" };
            var newFeatures = newRoof["features"] as JsonObject;
            foreach (var (part, keys) in FeatureKeys)
            {
                if (!scope.HasFlag(part)) continue;
                foreach (var key in keys)
                {
                    if (newFeatures != null && newFeatures[key] != null) features[key] = newFeatures[key]!.DeepClone();
                    else features.Remove(key);
                }
            }
            // what could not be read stays said until that part is read again: notes are the union
            var notes = new List<string>();
            foreach (var n in (features["notes"] as JsonArray) ?? new JsonArray()) if (n != null) notes.Add(n.ToString());
            foreach (var n in (newFeatures?["notes"] as JsonArray) ?? new JsonArray()) if (n != null && !notes.Contains(n.ToString())) notes.Add(n.ToString());
            features["notes"] = ToArray(notes);
            merged["features"] = features;

            var pushed = RoofPushScopes.FromNames((merged["pushed_scope"] as JsonArray)?.Select(n => n?.ToString())) | scope | RoofPushScope.Roof;
            merged["pushed_scope"] = ToArray(RoofPushScopes.Names(pushed));

            fresh["roof"] = merged;
            return fresh.ToJsonString();
        }

        static JsonObject? TryParse(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try { return JsonNode.Parse(json)?.AsObject(); } catch (Exception) { return null; }
        }

        static JsonArray ToArray(IEnumerable<string> items)
        {
            var a = new JsonArray();
            foreach (var i in items) a.Add(i);
            return a;
        }

        static bool SameFrame(JsonObject a, JsonObject b)
        {
            double N(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<double>(out var d) ? d : 0;
            return Math.Abs(N(a, "origin_x_m") - N(b, "origin_x_m")) <= FrameToleranceM && Math.Abs(N(a, "origin_y_m") - N(b, "origin_y_m")) <= FrameToleranceM
                && Math.Abs(N(a, "length_m") - N(b, "length_m")) <= FrameToleranceM && Math.Abs(N(a, "width_m") - N(b, "width_m")) <= FrameToleranceM
                && Math.Abs(N(a, "rotation_deg") - N(b, "rotation_deg")) <= FrameToleranceDeg;
        }
    }
}
