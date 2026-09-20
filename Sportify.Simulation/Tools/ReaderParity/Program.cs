using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Sportify.Simulation;
using Sportify.Simulation.Dynamics;
using Sportify.Simulation.Structure;
using Sportify.Simulation.Sun;
using Sportify.Simulation.Water;
using Sportify.Simulation.Wind;
using AddinDynamic = SportfyRevit.DynamicLayoutAdapter;
using AddinStructure = SportfyRevit.StructureLayoutAdapter;
using AddinSun = SportfyRevit.SunLayoutAdapter;
using AddinWind = SportfyRevit.WindLayoutAdapter;
using UnityDynamic = Sportify.Simulation.Dynamics.DynamicLayoutAdapter;
using UnityStructure = Sportify.Simulation.Structure.StructureLayoutAdapter;
using UnitySun = Sportify.Simulation.Sun.SunLayoutAdapter;
using UnityWind = Sportify.Simulation.Wind.WindLayoutAdapter;

// usage: ReaderParity [--fixtures <dir>] [--only <substring>]
//
// 1. READER PARITY: for every fixture layout and every analysis, the Unity project's reader (run under an emulation of JsonUtility, both with a missing array
//    read as null and as empty) and the add-in's reader must produce the same inputs and the same report.
// 2. THE EMULATION IS HONEST: for every real Unity result in fixtures/golden-unity, the emulated Unity path must give the numbers Unity wrote.
// 3. NO SILENT DROPS: every field the Unity classes read must exist, by name and kind, in the add-in's DTOs (Tools/ReaderParity/drift-allowlist.txt lists the
//    few that are deliberately Unity-only).
var fixtures = Arg("--fixtures") ?? Path.Combine(FindRoot(), "Tools", "fixtures");
var only = Arg("--only");
var fails = 0;
var checks = 0;
void Report(bool ok, string what, string extra = "") { checks++; if (!ok) fails++; if (!ok || Environment.GetEnvironmentVariable("READER_PARITY_VERBOSE") == "1") Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {what} {extra}"); }

var jsonOptions = new JsonSerializerOptions { IncludeFields = true, MaxDepth = 128 };
JsonNode? Node(object? o) => o == null ? null : JsonNode.Parse(JsonSerializer.Serialize(o, o.GetType(), jsonOptions));

Outcome Run<TIn>(Func<TIn> read, Func<TIn, object> analyse)
{
    try
    {
        var inputs = read();
        var report = analyse(inputs);
        return new Outcome(Node(inputs), Node(report), null);
    }
    catch (Exception ex) { return new Outcome(null, null, ex.GetType().Name); }
}

// ---- the five analyses, each with the Unity reader and the add-in reader
var analyses = new (string Name, Func<GoldbeckPayload, Outcome> Unity, Func<SportfyRevit.SportifyLayout, Outcome> Addin)[]
{
    ("structure",
        p => Run(() => UnityStructure.ToInputs(p), i => StructureModel.Analyse(i)),
        l => Run(() => AddinStructure.ToInputs(l), i => StructureModel.Analyse(i))),
    ("dynamic",
        p => Run(() => UnityDynamic.ToInputs(p), i => DynamicModel.Analyse(i)),
        l => Run(() => AddinDynamic.ToInputs(l), i => DynamicModel.Analyse(i))),
    ("wind",
        p => Run(() => UnityWind.ToInputs(p), i => WindModel.Analyse(i)),
        l => Run(() => AddinWind.ToInputs(l), i => WindModel.Analyse(i))),
    ("percolation",
        p => Run(() => UnityWind.ToInputs(p), i => PercolationModel.Analyse(new WaterInputs { RoofLength = i.RoofLength, RoofWidth = i.RoofWidth, RoofAreaM2 = i.Shape.Area, Zones = i.Zones })),
        l => Run(() => AddinWind.ToInputs(l), i => PercolationModel.Analyse(new WaterInputs { RoofLength = i.RoofLength, RoofWidth = i.RoofWidth, RoofAreaM2 = i.Shape.Area, Zones = i.Zones }))),
    ("sun",
        p => Run(() => UnitySun.ToInputs(p), i => SunModel.Analyse(i)),
        l => Run(() => AddinSun.ToInputs(l), i => SunModel.Analyse(i))),
};

// ---- 1. reader parity on every fixture
var files = Directory.GetFiles(Path.Combine(fixtures, "layouts"), "*.json", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal).ToList();
if (only != null) files = files.Where(f => f.Contains(only)).ToList();
Console.WriteLine($"===== reader parity: {files.Count} layouts x {analyses.Length} analyses, the Unity reader with arrays missing as null and as empty =====");
var errorAgreements = 0;
foreach (var file in files)
{
    var rel = Path.GetRelativePath(Path.Combine(fixtures, "layouts"), file).Replace('\\', '/');
    var json = File.ReadAllText(file);
    var layout = JsonSerializer.Deserialize<SportfyRevit.SportifyLayout>(json)!;
    foreach (var a in analyses)
    {
        var addin = a.Addin(layout);
        foreach (var mode in new[] { UnityJson.ArrayMode.Null, UnityJson.ArrayMode.Empty })
        {
            var unity = a.Unity(UnityJson.FromJson<GoldbeckPayload>(json, mode));
            var label = $"{rel} / {a.Name} / arrays {(mode == UnityJson.ArrayMode.Null ? "null" : "empty")}";
            if (unity.Error != null || addin.Error != null)
            {
                Report(unity.Error != null && addin.Error != null, label + ": both readers must refuse the same layout", $"(Unity: {unity.Error ?? "read it"}, add-in: {addin.Error ?? "read it"})");
                if (unity.Error != null && addin.Error != null) errorAgreements++;
                continue;
            }
            var diffs = new List<string>();
            Diff(unity.Inputs, addin.Inputs, "inputs", diffs, 1e-5, strictKeys: true);
            Diff(unity.Report, addin.Report, "report", diffs, 1e-5, strictKeys: true);
            Report(diffs.Count == 0, label, diffs.Count == 0 ? "" : "\n      " + string.Join("\n      ", diffs.Take(6)) + (diffs.Count > 6 ? $"\n      ... {diffs.Count - 6} more" : ""));
        }
    }
}
Console.WriteLine($"   ({errorAgreements} readings where both readers refused the layout, as they should)");

// ---- 2. the emulation against real Unity output
var goldenDir = Path.Combine(fixtures, "golden-unity");
var goldens = Directory.Exists(goldenDir) ? Directory.GetFiles(goldenDir, "*.json").Where(f => !Path.GetFileName(f).StartsWith("collision")).OrderBy(f => f).ToList() : new List<string>();
if (only != null) goldens = goldens.Where(f => f.Contains(only)).ToList();
Console.WriteLine($"\n===== the emulation against real Unity results: {goldens.Count} runs =====");
foreach (var g in goldens)
{
    var name = Path.GetFileNameWithoutExtension(g);                 // <analysis>__<group>-<layout>
    var parts = name.Split("__");
    var analysis = parts[0] == "structure" ? "structure" : parts[0];
    var groupAndLayout = parts[1];
    var dash = groupAndLayout.IndexOf('-');
    var layoutPath = Path.Combine(fixtures, "layouts", groupAndLayout[..dash], groupAndLayout[(dash + 1)..] + ".json");
    var entry = analyses.FirstOrDefault(a => a.Name == analysis);
    if (entry.Name == null || !File.Exists(layoutPath)) { Report(false, name + ": no analysis or layout for this golden file"); continue; }
    var realAnalysis = JsonNode.Parse(File.ReadAllText(g))?["analysis"];
    foreach (var mode in new[] { UnityJson.ArrayMode.Null, UnityJson.ArrayMode.Empty })
    {
        var emulated = entry.Unity(UnityJson.FromJson<GoldbeckPayload>(File.ReadAllText(layoutPath), mode));
        var diffs = new List<string>();
        if (emulated.Report == null) diffs.Add("the emulated Unity reader gave no report: " + emulated.Error);
        else Diff(emulated.Report, realAnalysis, "analysis", diffs, 1e-4, strictKeys: false);
        Report(diffs.Count == 0, $"{name} (arrays {(mode == UnityJson.ArrayMode.Null ? "null" : "empty")}) equals what Unity wrote", diffs.Count == 0 ? "" : "\n      " + string.Join("\n      ", diffs.Take(6)));
    }
}

// ---- 3. nothing Unity reads may be missing from the add-in's DTOs
Console.WriteLine("\n===== Unity's layout classes against the add-in's DTOs =====");
var allow = LoadAllowlist(Path.Combine(AppContext.BaseDirectory, "drift-allowlist.txt"), Path.Combine(FindRoot(), "Tools", "ReaderParity", "drift-allowlist.txt"));
var missing = new List<string>();
Walk(typeof(GoldbeckPayload), typeof(SportfyRevit.SportifyLayout), "", missing, new HashSet<(Type, Type)>());
var unexpected = missing.Where(m => !allow.Contains(m)).ToList();
var stale = allow.Where(a => !missing.Contains(a)).ToList();
Report(unexpected.Count == 0, "every field Unity reads exists in the add-in's DTOs, by name and kind", unexpected.Count == 0 ? $"({missing.Count} deliberate Unity-only fields in the allowlist)" : "\n      " + string.Join("\n      ", unexpected));
Report(stale.Count == 0, "the allowlist has no entry that is no longer needed", stale.Count == 0 ? "" : "\n      " + string.Join("\n      ", stale));

Console.WriteLine(fails == 0 ? $"\nALL READER-PARITY CHECKS PASSED ({checks} checks)" : $"\n{fails} OF {checks} CHECK(S) FAILED");
return fails == 0 ? 0 : 1;

// ---------------------------------------------------------------------------------------------------------------------------------- helpers

string? Arg(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

static string FindRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "Tools", "fixtures"))) dir = dir.Parent;
    return dir?.FullName ?? throw new InvalidOperationException("Could not find Tools/fixtures above " + AppContext.BaseDirectory);
}

// compares two report trees: numbers by relative tolerance, null and "" alike (JsonUtility writes a null string as ""), arrays element by element
static void Diff(JsonNode? a, JsonNode? b, string path, List<string> diffs, double tol, bool strictKeys)
{
    static bool Blank(JsonNode? n) => n == null || (n is JsonValue v && v.TryGetValue<string>(out var s) && s == "");
    if (Blank(a) && Blank(b)) return;
    if (a is JsonValue av && b is JsonValue bv)
    {
        if (av.TryGetValue<double>(out var x) && bv.TryGetValue<double>(out var y))
        {
            if (Math.Abs(x - y) > tol * Math.Max(1.0, Math.Max(Math.Abs(x), Math.Abs(y)))) diffs.Add($"{path}: {x.ToString(CultureInfo.InvariantCulture)} vs {y.ToString(CultureInfo.InvariantCulture)}");
            return;
        }
        if (av.ToJsonString() != bv.ToJsonString()) diffs.Add($"{path}: {av.ToJsonString()} vs {bv.ToJsonString()}");
        return;
    }
    if (a is JsonArray aa && b is JsonArray ba)
    {
        if (aa.Count != ba.Count) { diffs.Add($"{path}: {aa.Count} elements vs {ba.Count}"); return; }
        for (var i = 0; i < aa.Count; i++) Diff(aa[i], ba[i], $"{path}[{i}]", diffs, tol, strictKeys);
        return;
    }
    if (a is JsonObject ao && b is JsonObject bo)
    {
        foreach (var kv in ao)
        {
            if (!bo.ContainsKey(kv.Key)) { if (strictKeys && !Blank(kv.Value)) diffs.Add($"{path}.{kv.Key}: only on the left"); continue; }
            Diff(kv.Value, bo[kv.Key], $"{path}.{kv.Key}", diffs, tol, strictKeys);
        }
        if (strictKeys) foreach (var kv in bo) if (!ao.ContainsKey(kv.Key) && !Blank(kv.Value)) diffs.Add($"{path}.{kv.Key}: only on the right");
        return;
    }
    if (Blank(a) || Blank(b))
    {
        // one side has nothing where the other has an empty array or a zero: the same thing to a reader
        static bool Empty(JsonNode? n) => Blank(n) || (n is JsonArray arr && arr.Count == 0);
        if (Empty(a) && Empty(b)) return;
    }
    diffs.Add($"{path}: {a?.ToJsonString() ?? "null"} vs {b?.ToJsonString() ?? "null"}");
}

static string JsonName(PropertyInfo p) => p.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? p.Name;

static Type Unwrap(Type t)
{
    t = Nullable.GetUnderlyingType(t) ?? t;
    if (t.IsArray) return Unwrap(t.GetElementType()!);
    if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>)) return Unwrap(t.GetGenericArguments()[0]);
    return t;
}

static string KindOf(Type t)
{
    if (t.IsArray || (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>))) return "array";
    var u = Unwrap(t);
    if (u == typeof(string)) return "string";
    if (u == typeof(bool)) return "bool";
    if (u.IsPrimitive || u == typeof(decimal)) return "number";
    return "object";
}

static void Walk(Type unity, Type dto, string path, List<string> missing, HashSet<(Type, Type)> seen)
{
    if (!seen.Add((unity, dto))) return;
    var props = dto.GetProperties(BindingFlags.Public | BindingFlags.Instance);
    foreach (var f in unity.GetFields(BindingFlags.Public | BindingFlags.Instance))
    {
        var p = props.FirstOrDefault(x => JsonName(x) == f.Name);
        var here = path + f.Name;
        if (p == null) { missing.Add(here + "  (missing in the DTO)"); continue; }
        var uk = KindOf(f.FieldType);
        var dk = KindOf(p.PropertyType);
        if (uk != dk) { missing.Add($"{here}  (Unity reads a {uk}, the DTO has a {dk})"); continue; }
        var ut = Unwrap(f.FieldType);
        var dt = Unwrap(p.PropertyType);
        if (KindOf(ut) == "object" && KindOf(dt) == "object") Walk(ut, dt, here + ".", missing, seen);
    }
}

static HashSet<string> LoadAllowlist(params string[] candidates)
{
    var set = new HashSet<string>();
    var file = candidates.FirstOrDefault(File.Exists);
    if (file == null) return set;
    foreach (var raw in File.ReadAllLines(file))
    {
        var line = raw.Split('#')[0].TrimEnd();
        if (line.Length > 0) set.Add(line.Trim());
    }
    return set;
}

sealed record Outcome(JsonNode? Inputs, JsonNode? Report, string? Error);
