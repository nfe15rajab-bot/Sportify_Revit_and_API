# Roof features: what Revit sends about the roof

A base for the combining rules and live sync. **Push Roof to Sportify** already sent the roof's outline, height and structural grid.
It now also sends everything else the model knows about the roof that decides where things may stand:

| Block | What it is | Where it comes from in Revit |
|---|---|---|
| `openings` | holes in the roof's top face (skylights, shafts, rooflights) | the inner loops of the top face |
| `entries` | stairs, lifts ("cores") and doors by which people reach the roof | stairs whose top is at the roof, lifts by family name, doors standing on the roof |
| `edges` | each straight stretch of the roof's outline: `parapet`, `railing`, `partial` or `open` | low walls and railings standing along the outline |
| `drains` | roof drains, overflows, scuppers | family name (Revit has no roof-drain category) |
| `slab` | the build-up of the pushed roof or floor: layers, total and structural thickness | the element type's compound structure |
| `levels` | every level, its elevation and its height above ground; the level the roof sits on is flagged | the project's levels |

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
    "entries":  [ { "id": "stair_1", "kind": "stair" /* | "core" | "door" */, "name": "Stair 1", "x_m": 10, "y_m": 10, "width_m": 1.2, "on_roof": true, "source_element_id": 501 } ],
    "edges":    [ { "index": 0, "start_m": { "x_m": 0, "y_m": 20 }, "end_m": { "x_m": 40, "y_m": 20 }, "length_m": 40,
                    "kind": "parapet" /* | "railing" | "partial" | "open" */, "height_m": 0.6, "thickness_m": 0.25,
                    "parapet_coverage": 1, "railing_coverage": 0 } ],
    "drains":   [ { "id": "drain_1", "kind": "drain" /* | "overflow" | "scupper" */, "name": "Roof drain", "x_m": 5, "y_m": 5, "source_element_id": 801 } ],
    "slab":     { "type_name": "Flat roof 300", "thickness_m": 0.42, "structural_thickness_m": 0.25,
                  "layers": [ { "function": "Structure", "material": "Concrete C30/37", "thickness_m": 0.25 } ] },
    "levels":   [ { "name": "OG 3", "elevation_m": 11.5, "above_ground_m": 11.2, "is_roof_level": true } ]
  }
}
```

- `edges[].kind`: `parapet` or `railing` when at least half of the edge is covered by it; `partial` when 10 to 50% is covered; else `open`
  (nothing stops a fall or a stray ball). `height_m` is above the roof's top face.
- `entries[].on_roof` is false for a stair house or door just beyond the roof's edge (up to 3 m).
- `edges[].index` follows the outline's vertex order; `start_m` / `end_m` are already in roof-local coordinates.

## Where the code is

| | |
|---|---|
| `SportfyRevit/RoofFeatureCollector.cs` | the Revit calls (bounding-box filtered; each family of things read on its own, failures become notes) |
| `SportfyRevit/RoofFeaturesGeometry.cs` | Revit-free: the records the collector hands over, the conversion to roof-local coordinates, edge classification, the DTOs |
| `SportfyRevit/PushRoofBoundaryCommand.cs` | `TryCollectFeatures`: adds `features` to the push and tells the designer what was found |
| `Sportify.Simulation/Tools/AddinCheck` | tests of the geometry (canvas flip, dedup, edge coverage, L-shaped outline, JSON round trip) |
| web: `roofFeatures.js` | reads, draws and exports the block; "Use them as entry points" turns stairs, lifts and doors into the layout's entry points |

**Not yet tested in a live Revit session:** the collector (the geometry it feeds is tested with synthetic records, and the calls compile
against the Revit 2025 API). Try it on a model with a parapet, a skylight, a stair house and a roof drain and read the dialog it shows
after the push.

## Ideas for the rules that use it

- keep pieces out of `openings` (and a clearance around them); an opening is a hard no-go;
- a court beside an `open` or `partial` edge needs a fence (the ball analysis already finds where);
- `entries` with `on_roof: true` are the real arrival points: a route from each to every piece is the fire-safety and accessibility test;
- `drains`: keep them clear, and a build-up over a drain needs an inspection chamber;
- `slab.structural_thickness_m` could replace the span/25 depth the dynamic analysis estimates the deck's frequency from.
