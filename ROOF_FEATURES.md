# Roof features: what Revit sends about the roof

A base for the combining rules and live sync. **Push to Sportify** (a drop-down in the ribbon, see below) sends the roof's outline, height and
structural grid, and everything else the model knows about the roof that decides where things may stand:

| Block | What it is | Where it comes from in Revit |
|---|---|---|
| `openings` | holes in the roof's top face (skylights, shafts, rooflights) | the inner loops of the top face |
| `entries` | stairs, lifts ("cores"), doors and ramps by which people reach the roof | stairs and ramps whose top is at the roof, lifts by family name, doors standing on the roof; what is selected in Revit is taken instead when something of that kind is selected |
| `edges` | each straight stretch of the roof's outline: `parapet`, `railing`, `partial` or `open` | low walls and railings standing along the outline |
| `obstacles` | walls standing on the roof that cast shade (a stair house, a plant-room wall, a parapet): a line, a height and a thickness | every wall 0.2 to 15 m high on the roof; the sun and shade analysis casts shadows from them |
| `equipment` | plant standing on the roof (air handlers, chillers, switchboards): footprint, height, weight when the family has one | mechanical and electrical equipment whose base is at the roof's top face and that rises above it |
| `drains` | roof drains, overflows, scuppers | family name (Revit has no roof-drain category) |
| `slab` | the build-up of the pushed roof or floor: layers, total and structural thickness | the element type's compound structure |
| `levels` | every level, its elevation and its height above ground; the level the roof sits on is flagged | the project's levels |

and, in `roof.structure` next to the grid lines and columns:

| Block | What it is | Where it comes from in Revit |
|---|---|---|
| `beams` | beams under (or in) the slab: axis in the plan, section width and depth, top elevation | structural framing whose top is within 2.5 m below the roof's top face |
| `walls` | walls whose top meets the roof slab, marked bearing or not | walls that reach the roof from at least 1 m below it (a parapet standing ON the roof is not one); Revit's own structural usage says bearing |

## The "Push to Sportify" drop-down

The ribbon's push button is a drop-down. **Everything** pushes all of the above; each other item pushes one part (the roof's outline and size
always go with it: they fix the plan's frame):

| Item | Scope name (`pushed_scope`) | Carries |
|---|---|---|
| Everything | all | all eight |
| Roof outline and size | `roof` | outline, size, height above ground, origin, turn |
| Structure: grid, columns, beams, walls | `structure` | `roof.structure` |
| Entries: stairs, lifts, doors, ramps | `entries` | `features.entries` |
| Openings | `openings` | `features.openings` |
| Edge and walls on the roof | `edge` | `features.edges`, `features.obstacles` |
| Drains | `drains` | `features.drains` |
| Equipment on the roof | `equipment` | `features.equipment` |
| Slab build-up and levels | `slab_levels` | `features.slab`, `features.levels` |

Which roof: the roof or floor selected in the model; failing that, for a partial push, the roof pushed last; failing that, Revit asks. A push
of one part is laid onto the roof pushed before it (`RoofPushMerge`: same origin, size and turn) so the web app always receives the roof whole and
needs no merging of its own; a push of a different roof starts again. `roof.pushed_scope` lists what the roof has, and the app's Site tab says what
is still missing.

The **slab's structural thickness** (`features.slab.structural_thickness_m`, the layers whose function is structure) is used by the dynamic analysis:
the deck's depth in the resonance estimate is that thickness instead of the span/25 rule (a thickness outside 0.10 to 1.50 m is ignored as a modelling
slip). Beams under it are not added to the depth. **Equipment** shades the roof like a wall (the sun analysis), and its weight is carried in the export;
it is not yet added to the structural loads.

## The plan's frame (a roof turned against the model)

A building is rarely drawn square to the model's X and Y. The plan follows the roof: `rotation_deg` (in `roof` and `roof_context`) is how far the
plan's x axis is turned from the model's X axis (counter-clockwise), found from the outline's edges (the smallest bounding rectangle whose sides most
of the outline runs along; a round roof has none). `length_m` / `width_m` are the outline's box **in the roof's own axes**, `origin_x_m` / `origin_y_m`
its minimum corner in the model, and everything below (outline, grid, columns, beams, walls, features) is in those axes. For a roof square to the model
`rotation_deg` is 0 and everything is as before. The Revit import turns placements, zones, entry points, paths and the roof and setback outlines back
by it (`RoofFrame`, `SportifyLayoutBuilder.PlanToWorldFt`), and a placed family is turned by the frame's angle minus the piece's own rotation.

## Where it travels

```
Revit  --Push Roof to Sportify-->  roof.features                       (RoofBoundaryServer, localhost:5679/roof-boundary)
web app (revitBridge.js)        ->  combineState.roofFeatures          (roofFeatures.js: drawn on the Combine canvas, Site tab section)
export / session save           ->  roof_context.features              (the same block, in the Combine export)
Revit import / analyses         <-  SportifyLayout.RoofContext.Features (RoofFeaturesDto)
```

A push replaces the features (a different roof has different ones). A roof typed in by hand has none, and the block is absent.

## Conventions

- Metres. Roof-local plan coordinates, the same as placements, entry points, circulation paths and the structure:
  origin at the minimum corner of the roof's bounding box, **x right, y DOWN from the roof's top edge**
  (canvas `y = roof width - (model Y - minimum Y)`). The outer `source_boundary_polygon` alone stays in Revit's convention (y up).
- Elevations are in the project's own coordinates; `above_ground_m` is relative to the ground the roof height was measured from
  (`roof_context.height_source` says which: topography or a ground-floor level).
- Everything is best-effort. What could not be read, or was approximated, is in `notes`. Drains and parapets are recognised by
  **name and height**, not by a Revit class: check them against the model.

## The JSON

```jsonc
"roof_context": {
  "length_m": 40, "width_m": 20, /* ... */
  "features": {
    "source": "revit",
    "notes": ["..."],
    "openings": [ { "id": "opening_1", "polygon_m": [ { "x_m": 10, "y_m": 15 } /* ... */ ], "area_m2": 4, "x_m": 10, "y_m": 13, "width_m": 2, "height_m": 2 } ],
    "entries":  [ { "id": "stair_1", "kind": "stair" /* | "core" | "door" | "ramp" */, "name": "Stair 1", "x_m": 10, "y_m": 10, "width_m": 1.2, "on_roof": true, "source_element_id": 501 } ],
    "edges":    [ { "index": 0, "start_m": { "x_m": 0, "y_m": 20 }, "end_m": { "x_m": 40, "y_m": 20 }, "length_m": 40,
                    "kind": "parapet" /* | "railing" | "partial" | "open" */, "height_m": 0.6, "thickness_m": 0.25,
                    "parapet_coverage": 1, "railing_coverage": 0 } ],
    "equipment": [ { "id": "mechanical_1", "kind": "mechanical" /* | "electrical" */, "name": "Air handler", "x_m": 10, "y_m": 5, "width_m": 4, "depth_m": 2.5, "height_m": 1.8, "weight_kn": 2.9, "source_element_id": 7001 } ],
    "obstacles": [ { "id": "wall_1", "name": "Stair house N", "start_m": { "x_m": 30, "y_m": 6 }, "end_m": { "x_m": 36, "y_m": 6 }, "height_m": 3, "thickness_m": 0.3 } ],
    "drains":   [ { "id": "drain_1", "kind": "drain" /* | "overflow" | "scupper" */, "name": "Roof drain", "x_m": 5, "y_m": 5, "source_element_id": 801 } ],
    "slab":     { "type_name": "Flat roof 300", "thickness_m": 0.42, "structural_thickness_m": 0.25,
                  "layers": [ { "function": "Structure", "material": "Concrete C30/37", "thickness_m": 0.25 } ] },
    "levels":   [ { "name": "OG 3", "elevation_m": 11.5, "above_ground_m": 11.2, "is_roof_level": true } ]
  }
}
```

- `edges[].kind`: `parapet` or `railing` when at least half of the edge is covered by it; `partial` when 10 to 50% is covered; else `open`
  (nothing stops a fall or a stray ball). `height_m` is above the roof's top face.
- `obstacles[]` are straight wall centre lines (a curved wall becomes short straight stretches); `height_m` is above the roof's top face. Walls 2 m or lower along the outline are also in `edges` (as parapets): the same wall shades the roof and closes its edge. Anything taller than 15 m is left out (it is a building, not part of the roof).
- `entries[].on_roof` is false for a stair house or door just beyond the roof's edge (up to 3 m).
- `edges[].index` follows the outline's vertex order; `start_m` / `end_m` are already in roof-local coordinates.

## Where the code is

| | |
|---|---|
| `Sportify.Simulation/Assets/Scripts/Simulation/Sun/SunShadeCore.cs` | the reader of `obstacles`: their shadows (the sun and shade analysis) |
| `SportfyRevit/RoofFeatureCollector.cs` | the Revit calls (bounding-box filtered; each family of things read on its own, failures become notes) |
| `SportfyRevit/RoofFrame.cs`, `RoofPushScope.cs` | the plan's frame (turn, origin, both ways); the scopes and `RoofPushMerge` |
| `SportfyRevit/StructureCollector.cs` | the Revit calls for the grid, columns, beams and bearing walls |
| `SportfyRevit/PushRoofBoundaryCommand.cs`, `PushRoofCommands.cs` | `PushRoofCommandBase` (the push) and the nine commands of the drop-down |
| `SportfyRevit/RoofFeaturesGeometry.cs` | Revit-free: the records the collector hands over, the conversion to roof-local coordinates, edge classification, the DTOs |
| `SportfyRevit/PushRoofBoundaryCommand.cs` | `TryCollectFeatures`: adds `features` to the push and tells the designer what was found |
| `Sportify.Simulation/Tools/AddinCheck` | tests of the geometry (canvas flip, dedup, edge coverage, L-shaped outline, JSON round trip) |
| web: `roofFeatures.js` | reads, draws and exports the block; "Use them as entry points" turns stairs, lifts and doors into the layout's entry points |

**Not yet tested in a live Revit session:** the collectors (features, structure, equipment), the push commands, the ribbon drop-down and the import's turn (the geometry it feeds is tested with synthetic records, and the calls compile
against the Revit 2025 API). Try it on a model with a parapet, a skylight, a stair house and a roof drain and read the dialog it shows
after the push.

## Ideas for the rules that use it

- keep pieces out of `openings` (and a clearance around them); an opening is a hard no-go;
- a court beside an `open` or `partial` edge needs a fence (the ball analysis already finds where);
- `entries` with `on_roof: true` are the real arrival points: a route from each to every piece is the fire-safety and accessibility test;
- `obstacles`: shade equipment is never placed across a wall as high as the equipment itself (a low wall is passed over), and a tall wall is why a garden beside it can get too little sun;
- `drains`: keep them clear, and a build-up over a drain needs an inspection chamber;
- `slab.structural_thickness_m` could replace the span/25 depth the dynamic analysis estimates the deck's frequency from.
