# WindCoreCheck

Checks for the wind and erosion analysis (`Assets/Scripts/Simulation/Wind/WindAnalysisCore.cs`). None of this is part of the Unity project; Unity only imports `Assets/`.

The model is plain C# with no Unity types, so three programs compile the same file: the Unity run (which films it), the Revit add-in (which computes the numbers with no Unity installed) and this tool.

## Run the model on a layout

```
dotnet run --project Tools/WindCoreCheck -c Release -- Assets/StreamingAssets/sample_layout_roofgarden.json > report.json
```

Add `--inputs` to print what the layout reader made of the file (zones, build-up layers, plants) instead of the report.

## Check it against an independent implementation

`oracle.js` re-implements the model in about 150 lines from its specification, reading the layout JSON itself, and compares zones and trees with the C# report:

```
node Tools/WindCoreCheck/oracle.js Assets/StreamingAssets/sample_layout_roofgarden.json report.json
```

It must print `ORACLE MATCH`. Run it after changing any constant or formula in the core, and change the oracle deliberately too if the model itself changed: the point is that a slip in one is not repeated in the other.

## Check Unity's run against the add-in's

After a Unity run (`BatchRunner.RunWindAnalysis`) has written `Recordings/wind_results.json`:

```
node Tools/WindCoreCheck/compare-unity.js Recordings/wind_results.json report.json
```

Unity reads the layout with `JsonUtility` and the add-in with `System.Text.Json`; this confirms the two readers agree on every number.

## The sample layout

`make-sample.js` writes `Assets/StreamingAssets/sample_layout_roofgarden.json` in the shape the web app's Combine tab exports (zones, build-ups, plants). One of its four build-ups, "Deep tree bed (800 mm)", is not a manufacturer system: it stands for what a designer adds in the Catalogue tab's build-up editor for a raised tree bed.
