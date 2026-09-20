# RoofCheck

Checks for the roof's shape (`Assets/Scripts/Simulation/Roof/RoofShape.cs`). None of this is part of the Unity project; Unity only imports `Assets/`.

Every analysis used to work on the roof's bounding rectangle. `RoofShape` is the roof as it really is: its outline (cleaned: repeated and collinear points dropped, one winding), its area and centroid, whether a point is on it, **how much of a cell is roof**, its edges with their outward normals, the distance to its outline, and polygon clipping (Sutherland-Hodgman, used to cut the roof into bays and a bay into cells). A roof whose outline is its bounding rectangle, or that has none, is `IsRectangle`, and every analysis then takes exactly the path it took before: their numbers do not change.

```
dotnet run --project Tools/RoofCheck -c Release
```

It checks, against hand-worked cases: rectangles (any winding, a repeated closing point, millimetres of noise), an L (area, centroid, containment, the notch's edges facing into the void, a segment across the notch leaving the roof), cell coverage (the cells' roof areas add up to the outline's area, for an L and for a rectangle turned 30 degrees), cleaning, clipping, and the wind's roof zones on an outline (EN 1991-1-4 zones F, G, H, I by the distance in from each stretch the wind blows across). It must end with `ALL ROOF CHECKS PASSED`.

## Where the roof's shape reaches

| Analysis | On a roof with an outline | Check |
|---|---|---|
| Structural loads | a cell carries only its roof part; bays are the roof cut by the grid lines (quadrilaterals for a skewed grid, an L-shaped corner bay); columns in the notch are dropped; the balance centre is the roof's centroid | `Tools/StructuralCheck` (invariants and `oracle.js`, which computes every area by scanline integration, not by clipping) |
| Dynamic | the same bays and cells; crowd entries on the outline; spans from a bay's area over its height and width | `Tools/DynamicCheck` |
| Wind and erosion | roof zones from each windward stretch of the outline | `Tools/WindCoreCheck` (`oracle.js` samples the line of sight instead of testing crossings) |
| Rain and percolation | the roof's own area for the runoff totals | `Tools/PercolationCheck` |
| Sun and shade | its own grid already followed the outline | `Tools/SunCheck` |
| Ball trajectories | **still the bounding rectangle**: a ball over the notch has not left the roof, and fences are proposed along the box's edges | |

A grid line square to the roof's edges (within 0.05 degrees) needs only where it crosses; a slanted one keeps its own two points. Lines of one direction that meet on the roof cannot cut it into strips (a brace at 19 degrees among horizontal grid lines is not a grid line): the line that meets the most is left out, and the report says so.
