# StructuralCheck

Checks for the static structural load analysis (`Assets/Scripts/Simulation/Structure/StructuralLoadCore.cs`). Not part of the Unity project; Unity only imports `Assets/`.

Like the wind and rain models, the load model is plain C# with no Unity types: the Unity run films it, the Revit add-in computes the numbers with no Unity installed, and this tool runs it on a layout.

## Run the model and its checks

```
dotnet run --project Tools/StructuralCheck -c Release -- Assets/StreamingAssets/sample_layout_roofgarden.json
```

Prints the load totals, each piece's load, the utilisation of every bay against the deck capacity, the balance of the load, the advice; then checks what must hold whatever the constants:

- conservation: the bays, the columns and the summary all add up to what the pieces put on the roof (permanent, imposed, people);
- mirroring the whole layout flips the sign of the eccentricity and changes nothing else;
- twice the capacity halves the utilisation; a layout with no grid carries the same load on an assumed one, and says so;
- a slab put in the lightest bay adds its weight to that bay and no other;
- every recommended move or lightening, applied in order, leaves the balance and the busiest bay where its text says.

Add `--json` to print the whole report instead.

## Check it against an independent implementation

`oracle.js` re-implements the arithmetic from the specification in the header of `StructuralLoadCore.cs` (pieces onto 0.5 m cells by overlap, imposed load by highest intensity first, cells split over the bays by area, tributary column loads, the balance), reading the layout JSON itself. It takes only each piece's *intensities* (permanent and imposed load per m2, people, a tree's weight) from the C# report, so what it checks is the mapping from pieces to cells, bays, columns and balance:

```
dotnet run --project Tools/StructuralCheck -c Release -- <layout.json> --json > report.json
node Tools/StructuralCheck/oracle.js <layout.json> report.json
```

It must print `ORACLE MATCH`. After a Unity run (`BatchRunner.RunStructuralAnalysis`), `node Tools/WindCoreCheck/compare-unity.js Recordings/structure_results.json report.json` confirms Unity's reading of the layout gives the same report, value for value, advice included.

## What the numbers are and are not

A screening model, not a structural verification. Only the court load (category C4, 5.0 kN/m2, DIN EN 1991-1-1/NA Table 6.1DE) is taken from the standard; the other imposed loads, the people densities, the balance limits (5% marginal, 10% unbalanced) and the tree weights are the author's, named at the top of `StructureModel` and repeated in every report's assumptions. **The deck capacity is not in the layout**: until the structural engineer's figure is entered (Site tab in the web app, `structure.deck_capacity_kn_m2` in the export) a placeholder of 8 kN/m2 is used and the results say so.

## The assumptions register

`Assets/Scripts/Simulation/Structure/AnalysisAssumptions.cs` lists every value the structural analyses rest on that the layout cannot know (deck capacity, the deck's frequency, snow zone and altitude, the day's schedule, the comfort limits) and every fixed constant, each with where it comes from. The web app keeps a copy (`assumptions.js`); check they are the same list:

```
node Tools/StructuralCheck/assumptions-parity.js <path to the web app's assumptions.js>
```

`dotnet run --project Tools/StructuralCheck -- --assumptions-json` prints the C# side as JSON (what the script compares, and what regenerates the web copy).
