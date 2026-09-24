using System.Text.Json.Serialization;

namespace Sportify.Mechanical;

/// <summary>
/// What the add-in gives for one kinetic unit (KineticsRender.cs, add-in side): its bars and membranes in the unit's OWN frame (x along the unit, y outward or across its depth,
/// z up, metres) at every state, bar i of one state being bar i of the next, and the design inputs the model is built from. The same file drives the Unity video.
/// </summary>
internal sealed class RenderRequest
{
    [JsonPropertyName("pieceName")] public string PieceName { get; set; } = "";
    [JsonPropertyName("kind")] public string Kind { get; set; } = "overhead";
    [JsonPropertyName("kindLabel")] public string KindLabel { get; set; } = "";
    [JsonPropertyName("lengthM")] public double LengthM { get; set; }
    [JsonPropertyName("depthM")] public double DepthM { get; set; }
    [JsonPropertyName("heightM")] public double HeightM { get; set; }
    [JsonPropertyName("chordM")] public double ChordM { get; set; }
    [JsonPropertyName("thicknessM")] public double ThicknessM { get; set; }
    [JsonPropertyName("count")] public int Count { get; set; }
    [JsonPropertyName("pitchM")] public double PitchM { get; set; }
    [JsonPropertyName("bays")] public int Bays { get; set; }
    [JsonPropertyName("states")] public List<RenderState> States { get; set; } = new();
    [JsonPropertyName("mechanicsLines")] public List<string> MechanicsLines { get; set; } = new();
    [JsonPropertyName("inputs")] public List<RenderInput> Inputs { get; set; } = new();

    public double Input(string key, double fallback) => Inputs.FirstOrDefault(i => i.Key == key)?.Value ?? fallback;
}

internal sealed class RenderInput
{
    [JsonPropertyName("key")] public string Key { get; set; } = "";
    [JsonPropertyName("value")] public double Value { get; set; }
}

internal sealed class RenderState
{
    [JsonPropertyName("label")] public string Label { get; set; } = "";
    [JsonPropertyName("solarTimeH")] public double SolarTimeH { get; set; }
    [JsonPropertyName("sunElevationDeg")] public double SunElevationDeg { get; set; }
    [JsonPropertyName("sunX")] public double SunX { get; set; }
    [JsonPropertyName("sunY")] public double SunY { get; set; }
    [JsonPropertyName("openDeg")] public double OpenDeg { get; set; }
    [JsonPropertyName("sunStoppedPercent")] public double SunStoppedPercent { get; set; }
    [JsonPropertyName("windTorqueOperatingNm")] public double WindTorqueOperatingNm { get; set; }
    [JsonPropertyName("bars")] public List<RenderBar> Bars { get; set; } = new();
    [JsonPropertyName("surfaces")] public List<RenderSurface> Surfaces { get; set; } = new();
}

internal sealed class RenderBar
{
    [JsonPropertyName("role")] public string Role { get; set; } = "";
    [JsonPropertyName("dynamic")] public bool Dynamic { get; set; }
    [JsonPropertyName("detail")] public bool Detail { get; set; }
    [JsonPropertyName("cadOnly")] public bool CadOnly { get; set; }
    [JsonPropertyName("p0")] public double[] P0 { get; set; } = new double[3];
    [JsonPropertyName("p1")] public double[] P1 { get; set; } = new double[3];
    [JsonPropertyName("u")] public double[] U { get; set; } = new double[3];
    [JsonPropertyName("sizeU")] public double SizeU { get; set; }
    [JsonPropertyName("sizeV")] public double SizeV { get; set; }
}

internal sealed class RenderSurface
{
    [JsonPropertyName("role")] public string Role { get; set; } = "";
    [JsonPropertyName("dynamic")] public bool Dynamic { get; set; }
    [JsonPropertyName("a")] public double[] A { get; set; } = new double[3];
    [JsonPropertyName("b")] public double[] B { get; set; } = new double[3];
    [JsonPropertyName("c")] public double[] C { get; set; } = new double[3];
    [JsonPropertyName("d")] public double[] D { get; set; } = new double[3];
}
