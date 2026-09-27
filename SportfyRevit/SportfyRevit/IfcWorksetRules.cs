using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SportfyRevit
{
    /// <summary>The table that says which workset an IFC class goes on: the workset for a class (or "IfcClass:PREDEFINED"), what everything else gets, and where the roof goes.</summary>
    internal sealed record IfcWorksetTable(string Fallback, string Roof, IReadOnlyDictionary<string, string> Map);

    /// <summary>
    /// "Classified as per worksets" for a model that came from an IFC (the Goldbeck model): every element the IFC import made is put on a workset by its IFC class, from a table a person can edit.
    /// This file is what needs no Revit, so that Tools/AddinCheck can test it: reading the class out of what Revit's IFC import writes on an element ("Export to IFC As" = IfcColumnType on the
    /// instance, or "Export Type to IFC As" on its type), the class of an element that has none (from its Revit category), the table, and the file the table lives in
    /// (%APPDATA%\Sportify\ifc-worksets.json, written with the defaults the first time so that it can be edited). IfcWorksetService is the part that touches the project.
    ///
    /// The default names are Sportify's own choice and await review; the table is the way to change them. They start with "IFC " so that they never meet Sportify's own worksets (Sports, Gardens, Combine,
    /// "Sportify ..."). The roof is named by the person (the selection when the command runs), never guessed: a building has slabs at every level and the IFC does not say which one is the roof.
    /// </summary>
    internal static class IfcWorksetRules
    {
        /// <summary>The workset of the existing roof (SportifyWorksetSet.ExistingRoofBase: AddinCheck compares the two).</summary>
        public const string RoofWorkset = "Sportify Existing Roof Base";
        public const string FallbackWorkset = "IFC Other";

        /// <summary>The defaults, in the order the file lists them.</summary>
        public static IfcWorksetTable Defaults => new(FallbackWorkset, RoofWorkset, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["IfcColumn"] = "IFC Structure",
            ["IfcBeam"] = "IFC Structure",
            ["IfcMember"] = "IFC Structure",
            ["IfcFooting"] = "IFC Structure",
            ["IfcPile"] = "IFC Structure",
            ["IfcSlab"] = "IFC Floors",
            ["IfcWall"] = "IFC Walls",
            ["IfcCurtainWall"] = "IFC Walls",
            ["IfcRailing"] = "IFC Railings",
            ["IfcRamp"] = "IFC Ramps and Stairs",
            ["IfcRampFlight"] = "IFC Ramps and Stairs",
            ["IfcStair"] = "IFC Ramps and Stairs",
            ["IfcStairFlight"] = "IFC Ramps and Stairs",
            ["IfcFurniture"] = "IFC Furniture",
            ["IfcFurnishingElement"] = "IFC Furniture",
            ["IfcDoor"] = "IFC Doors and Windows",
            ["IfcWindow"] = "IFC Doors and Windows",
            ["IfcCovering"] = "IFC Finishes",
            ["IfcPipeSegment"] = "IFC Pipes and Services",
            ["IfcPipeFitting"] = "IFC Pipes and Services",
            ["IfcFlowSegment"] = "IFC Pipes and Services",
            ["IfcFlowFitting"] = "IFC Pipes and Services",
            ["IfcFlowTerminal"] = "IFC Pipes and Services",
            ["IfcDistributionElement"] = "IFC Pipes and Services",
        });

        public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Sportify", "ifc-worksets.json");

        // ---------------------------------------------------------------------------------------------------------------- reading the class

        /// <summary>
        /// "IfcColumnType" is the IFC class of the TYPE, which is what Revit's IFC settings call "Export to IFC As": the element is an IfcColumn. So a trailing "Type" goes, and so does "StandardCase"
        /// (IfcWallStandardCase is an IfcWall). Empty, "By Type", "Default" and "Not Exported" say nothing: null.
        /// </summary>
        public static string? NormalizeClass(string? raw)
        {
            var s = (raw ?? "").Trim();
            if (s.Length == 0) return null;
            if (s.Equals("By Type", StringComparison.OrdinalIgnoreCase) || s.Equals("Default", StringComparison.OrdinalIgnoreCase) || s.Equals("<Default>", StringComparison.OrdinalIgnoreCase)
                || s.Equals("Not Exported", StringComparison.OrdinalIgnoreCase) || s.Equals("Yes", StringComparison.OrdinalIgnoreCase) || s.Equals("No", StringComparison.OrdinalIgnoreCase)) return null;
            if (!s.StartsWith("Ifc", StringComparison.OrdinalIgnoreCase)) return null;
            if (s.EndsWith("Type", StringComparison.OrdinalIgnoreCase) && s.Length > 7) s = s[..^4];
            if (s.EndsWith("StandardCase", StringComparison.OrdinalIgnoreCase) && s.Length > 15) s = s[..^12];
            return "Ifc" + s[3..];
        }

        /// <summary>The predefined type ("BEAM", "BRACE", "GUARDRAIL"), upper case; null for none, NOTDEFINED and USERDEFINED (they refine nothing).</summary>
        public static string? NormalizePredefined(string? raw)
        {
            var s = (raw ?? "").Trim().ToUpperInvariant();
            return s.Length == 0 || s == "NOTDEFINED" || s == "USERDEFINED" || s == "BY TYPE" || s == "DEFAULT" ? null : s;
        }

        /// <summary>
        /// The class of an element from what is written on it: first its own "Export to IFC As", then its type's "Export Type to IFC As", then, when neither says, the class of its Revit category
        /// (an IFC import puts a column in Structural Columns). <paramref name="instance"/> and <paramref name="type"/> read a parameter by name (null when it is not there).
        /// </summary>
        public static string? ClassOf(Func<string, string?> instance, Func<string, string?> type, string? builtInCategoryName)
        {
            return NormalizeClass(instance("Export to IFC As")) ?? NormalizeClass(type("Export Type to IFC As")) ?? ClassFromCategory(builtInCategoryName);
        }

        public static string? PredefinedOf(Func<string, string?> instance, Func<string, string?> type) =>
            NormalizePredefined(instance("IFC Predefined Type")) ?? NormalizePredefined(type("Type IFC Predefined Type"));

        /// <summary>The class an IFC import gives an element of this Revit category (the name of the BuiltInCategory, "OST_StructuralColumns"); null for a category it does not know.</summary>
        public static string? ClassFromCategory(string? builtInCategoryName) => (builtInCategoryName ?? "") switch
        {
            "OST_StructuralColumns" or "OST_Columns" => "IfcColumn",
            "OST_StructuralFraming" => "IfcBeam",
            "OST_StructuralFoundation" => "IfcFooting",
            "OST_Floors" => "IfcSlab",
            "OST_Walls" => "IfcWall",
            "OST_CurtainWallPanels" or "OST_CurtainWallMullions" => "IfcCurtainWall",
            "OST_Railings" => "IfcRailing",
            "OST_Ramps" => "IfcRamp",
            "OST_Stairs" => "IfcStair",
            "OST_Furniture" or "OST_FurnitureSystems" => "IfcFurniture",
            "OST_Doors" => "IfcDoor",
            "OST_Windows" => "IfcWindow",
            "OST_Ceilings" or "OST_Roofs" => "IfcCovering",
            "OST_PipeCurves" or "OST_PipeFitting" or "OST_PipeSegments" => "IfcPipeSegment",
            _ => null,
        };

        // ---------------------------------------------------------------------------------------------------------------- the table

        /// <summary>
        /// The workset for an element: the roof workset for a roof the person named, else the table's entry for "class:PREDEFINED", else for the class, else the fallback (a class the table does not name).
        /// An element that has no IFC class at all (the IFC's grid axes come in as plain model lines) gets null: it says nothing about what it is, so it stays where it is.
        /// </summary>
        public static string? WorksetFor(IfcWorksetTable table, string? ifcClass, string? predefined, bool isRoof)
        {
            if (isRoof) return table.Roof;
            if (ifcClass == null) return null;
            if (predefined != null && table.Map.TryGetValue(ifcClass + ":" + predefined, out var refined) && !string.IsNullOrWhiteSpace(refined)) return refined.Trim();
            if (table.Map.TryGetValue(ifcClass, out var w) && !string.IsNullOrWhiteSpace(w)) return w.Trim();
            return table.Fallback;
        }

        /// <summary>The table as the file holds it, with a note on top that says how to edit it.</summary>
        public static string ToJson(IfcWorksetTable table)
        {
            var map = new JsonObject();
            foreach (var kv in table.Map) map[kv.Key] = kv.Value;
            var root = new JsonObject
            {
                ["_about"] = "Which workset an IFC class goes on (Sportify > BIM & Documentation > Phasing & Worksets > IFC Worksets by Class). Edit the names on the right; a key may be a class (\"IfcColumn\") or a class with its predefined type (\"IfcMember:BRACE\", which wins). "
                             + "\"fallback\" is for a class the table does not name, \"roof\" for the roof you select when you run the command. An element with no IFC class at all is left where it is. Delete this file to get the defaults back.",
                ["fallback"] = table.Fallback,
                ["roof"] = table.Roof,
                ["map"] = map,
            };
            return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        }

        /// <summary>The table from a file: the defaults for a file that is missing or unreadable, and for any part of it that is not there (a file without "map" has the default map).</summary>
        public static IfcWorksetTable Parse(string? json)
        {
            var d = Defaults;
            try
            {
                if (JsonNode.Parse(json ?? "") is not JsonObject root) return d;
                string Name(string key, string dflt) => root[key] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s.Trim() : dflt;
                var map = d.Map;
                if (root["map"] is JsonObject m)
                {
                    var own = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var kv in m)
                        if (kv.Value is JsonValue v && v.TryGetValue<string>(out var w) && !string.IsNullOrWhiteSpace(w) && !string.IsNullOrWhiteSpace(kv.Key)) own[kv.Key.Trim()] = w.Trim();
                    map = own;
                }
                return new IfcWorksetTable(Name("fallback", d.Fallback), Name("roof", d.Roof), map);
            }
            catch (Exception) { return d; }
        }

        public static IfcWorksetTable Load(string path)
        {
            try { return File.Exists(path) ? Parse(File.ReadAllText(path)) : Defaults; }
            catch (Exception) { return Defaults; }
        }

        /// <summary>Writes the default table where a person can find and edit it, unless a file is there already. True when it wrote one.</summary>
        public static bool EnsureFile(string path)
        {
            try
            {
                if (File.Exists(path)) return false;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, ToJson(Defaults));
                return true;
            }
            catch (Exception) { return false; }
        }

        /// <summary>The workset names a table can put an element on: what its map, its fallback and its roof say.</summary>
        public static IReadOnlyList<string> WorksetNames(IfcWorksetTable table) =>
            table.Map.Values.Append(table.Fallback).Append(table.Roof).Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        /// <summary>The counts as a few lines a dialog can show, largest first: "IFC Structure: 479".</summary>
        public static string Lines(IEnumerable<KeyValuePair<string, int>> counts) =>
            string.Join("\n", counts.Where(c => c.Value > 0).OrderByDescending(c => c.Value).ThenBy(c => c.Key, StringComparer.OrdinalIgnoreCase).Select(c => "  " + c.Key + ": " + c.Value));
    }
}
