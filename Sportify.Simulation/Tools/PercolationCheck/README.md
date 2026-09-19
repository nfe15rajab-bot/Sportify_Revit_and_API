# PercolationCheck

Checks for the rain and percolation analysis (`Assets/Scripts/Simulation/Water/PercolationCore.cs`). Not part of the Unity project; Unity only imports `Assets/`.

Like the wind model, the percolation model is plain C# with no Unity types: the Unity run films it, the Revit add-in computes the numbers with no Unity installed, and this tool runs it on a layout.

## Run the model and its physics checks

```
dotnet run --project Tools/PercolationCheck -c Release -- Assets/StreamingAssets/sample_layout_roofgarden.json
```

Prints each zone's retention for the three rain events, the roof totals and the advice, then checks what must hold whatever the constants:

- rain in = runoff out + what the column holds (mass balance, to 1e-6 mm, four intensities, every zone);
- a longer or bigger storm of the same kind is never retained better;
- more substrate never runs off more;
- the roof totals agree with their own volumes;
- a paved zone keeps almost nothing, and a thin system is flagged and given a fix.

Add `--json` to print the whole report instead.

## Check it against an independent implementation

`oracle.js` re-implements the column model from its specification (the header of `PercolationCore.cs`), reading the layout JSON itself, and compares runoff, peak, first runoff, fill and saturation for every zone and event:

```
dotnet run --project Tools/PercolationCheck -c Release -- <layout.json> --json > report.json
node Tools/PercolationCheck/oracle.js <layout.json> report.json
```

It must print `ORACLE MATCH`. After a Unity run (`BatchRunner.RunPercolationAnalysis`), `node Tools/WindCoreCheck/compare-unity.js Recordings/percolation_results.json report.json` confirms Unity's reading of the layout gives the same report, value for value.

## What the numbers are and are not

A screening model with three GENERIC rain events (steady 10 mm/h for 3 h, heavy shower 40 mm/h for 30 min, cloudburst 108 mm/h for 10 min), each on a wet roof. They are not the site's design rainfall (KOSTRA-DWD): that would come from the site location the way the wind zone does. Where the provider prints a system's water storage the substrate is calibrated to it (ZinCo does; the constants for the rest are the author's own and named at the top of `PercolationModel`).
