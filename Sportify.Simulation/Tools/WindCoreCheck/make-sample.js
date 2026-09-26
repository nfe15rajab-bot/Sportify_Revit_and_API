// Generates the bundled garden-rich sample layout in the same export shape the web app's Combine tab produces (v1.3+).
const fs = require("fs");
const path = process.argv[2] || require("path").join(__dirname, "..", "..", "Assets", "StreamingAssets", "sample_layout_roofgarden.json");

const layer = (order, name, fn, mm, src) => ({ order, name, function: fn, thickness_m: mm / 1000, thickness_source: src });

const assemblies = [
  {
    key: "zinco_roof_garden", provider: "ZinCo", provider_country: "Germany", system_name: "Roof Garden", category: "intensive",
    revit_type_name: "Sportify - ZinCo Roof Garden", build_up_mm: 318, saturated_kg_m2: 425, water_storage_l_m2: 143,
    source_url: "https://zinco-usa.com/systems/roof-garden", total_thickness_m: 0.418,
    layers: [
      layer(0, "Plant layer per plant list", "vegetation", 100, "typical"),
      layer(1, "Growing media Zincoblend I", "substrate", 250, "published"),
      layer(2, "Filter Sheet SF", "filter", 2, "typical"),
      layer(3, "Floradrain FD 60 neo, filled with Zincoblend M", "drainage", 60, "typical"),
      layer(4, "Protection Mat ISM 50", "protection", 5, "typical"),
      layer(5, "Root Barrier WSB 100-PO", "root_barrier", 1, "typical"),
    ],
  },
  {
    key: "zinco_sloped_sedum", provider: "ZinCo", provider_country: "Germany", system_name: "Sloped Sedum", category: "extensive",
    revit_type_name: "Sportify - ZinCo Sloped Sedum", build_up_mm: 127, saturated_kg_m2: 171, water_storage_l_m2: 57,
    source_url: "https://zinco-usa.com/systems/sloped-sedum", total_thickness_m: 0.175,
    layers: [
      layer(0, "Plant community Sloped Sedum", "vegetation", 20, "typical"),
      layer(1, "Growing media Zincoblend E", "substrate", 110, "published"),
      layer(2, "Georaster Elements", "drainage", 40, "typical"),
      layer(3, "Protection Mat WSM 150", "protection", 5, "typical"),
    ],
  },
  {
    key: "optigruen_nature_roof", provider: "Optigruen", provider_country: "Germany", system_name: "Naturdach", category: "extensive",
    revit_type_name: "Sportify - Optigruen Naturdach", build_up_mm: null, saturated_kg_m2: null, water_storage_l_m2: null,
    source_url: "https://www.optigruen.de/systemloesungen", total_thickness_m: 0.152,
    layers: [
      layer(0, "Seed mix / plug planting", "vegetation", 20, "typical"),
      layer(1, "Extensive substrate", "substrate", 100, "typical"),
      layer(2, "Filter fleece 105", "filter", 2, "typical"),
      layer(3, "Drainage element FKD 25", "drainage", 25, "typical"),
      layer(4, "Protection mat RMS 500", "protection", 5, "typical"),
    ],
  },
  {
    // Not a manufacturer system: the kind of build-up a designer adds in the Catalogue tab's editor for a raised tree bed.
    key: "sample_deep_tree_bed", provider: "Sample project", provider_country: "Germany", system_name: "Deep tree bed (800 mm)", category: "intensive",
    revit_type_name: "Sportify - Sample project Deep tree bed (800 mm)", build_up_mm: null, saturated_kg_m2: null, water_storage_l_m2: null,
    source_url: null, total_thickness_m: 0.968,
    layers: [
      layer(0, "Plant layer per plant list", "vegetation", 100, "typical"),
      layer(1, "Intensive tree substrate", "substrate", 800, "typical"),
      layer(2, "Filter sheet", "filter", 2, "typical"),
      layer(3, "Drainage board", "drainage", 60, "typical"),
      layer(4, "Protection mat", "protection", 5, "typical"),
      layer(5, "Root barrier", "root_barrier", 1, "typical"),
    ],
  },
];

const zone = (n, x, y, w, h, key) => ({
  id: "zone_sample_" + n, kind: "green_roof", label: "Green roof",
  bounding_box: { top_left_x_m: x, top_left_y_m: y, width_m: w, height_m: h },
  area_m2: Math.round(w * h * 100) / 100, assembly_key: key,
});

const zones = [
  zone(1, 1.0, 1.0, 20.0, 19.0, "zinco_sloped_sedum"),
  zone(2, 22.0, 1.0, 30.0, 5.5, "zinco_roof_garden"),
  zone(3, 22.0, 14.5, 30.0, 5.5, "optigruen_nature_roof"),
  zone(4, 54.0, 1.0, 12.6, 19.0, "sample_deep_tree_bed"),
];

const court = (n, x) => ({
  id: "sample_court_" + n, category: "field", label: "Badminton (standard)",
  insertion_point: { center_x_m: x + 6.7, center_y_m: 10.5 },
  bounding_box: { top_left_x_m: x, top_left_y_m: 7.45, width_m: 13.4, height_m: 6.1 },
  transform: { rotation_deg: 0 },
  parameters: {
    version: "1.0", generator: "Sportify", quality_key: "BADMINTON_STANDARD_MEDIUM",
    field: { sport: "badminton", variant: "standard", norm: "BWF / DIN 18032",
             dimensions: { length_m: 13.4, width_m: 6.1, runoff_m: 2, min_height_m: 9 }, capacity: { seats: 0, side_stands: false } },
    materials: { floor_surface: "Sports vinyl (2-layer)", line_marking: "Adhesive tape", gradin_type: "Steel coated", quality_level: "medium", reference_material: null, reference_provider: null },
    layers: ["field_boundary", "center_line", "run_off_zone"],
  },
});

const species = {
  carpinus: { species_key: "carpinus_japonica", botanical_name: "Carpinus japonica", common_name: "Japanese hornbeam", form: "tree", height_m: 11, height_range: "8-15 m", min_substrate_mm: 800, source: "Van den Berk", source_url: "https://www.vdberk.com/trees/carpinus-japonica/", dimensions_published: true },
  pinus:    { species_key: "pinus_parviflora_glauca", botanical_name: "Pinus parviflora Glauca", common_name: "Japanese white pine", form: "tree", height_m: 9, height_range: "6-12 m", min_substrate_mm: 800, source: "Van den Berk", source_url: "https://www.vdberk.com/trees/pinus-parviflora-glauca/", dimensions_published: true },
  cornus:   { species_key: "cornus_mas", botanical_name: "Cornus mas", common_name: "Cornelian cherry", form: "tree", height_m: 6, height_range: "5-6 m", min_substrate_mm: 800, source: "Van den Berk", source_url: "https://www.vdberk.com/trees/cornus-mas/", dimensions_published: true },
  pyrus:    { species_key: "pyrus_salicifolia_pendula", botanical_name: "Pyrus salicifolia Pendula", common_name: "Weeping willow-leaved pear", form: "tree", height_m: 6, height_range: "5-6 m", min_substrate_mm: 800, source: "Van den Berk", source_url: "https://www.vdberk.com/trees/pyrus-salicifolia-pendula/", dimensions_published: true },
  lavender: { species_key: "lavandula_angustifolia", botanical_name: "Lavandula angustifolia", common_name: "Lavender", form: "shrub", height_m: 0.6, height_range: "0.4-0.8 m", min_substrate_mm: 200, source: "Horticultural norm", source_url: null, dimensions_published: false },
  fescue:   { species_key: "festuca_glauca", botanical_name: "Festuca glauca", common_name: "Blue fescue", form: "grass", height_m: 0.3, height_range: "0.2-0.4 m", min_substrate_mm: 150, source: "Horticultural norm", source_url: null, dimensions_published: false },
  sedum:    { species_key: "sedum_mix", botanical_name: "Sedum mix", common_name: "Stonecrop mat", form: "groundcover", height_m: 0.15, height_range: "0.05-0.2 m", min_substrate_mm: 60, source: "Horticultural norm", source_url: null, dimensions_published: false },
};
const crowns = { carpinus: 7, pinus: 8, cornus: 5.5, pyrus: 5.5, lavender: 0.8, fescue: 0.4, sedum: 1.0 };

let n = 0;
const plant = (kind, cx, cy) => {
  const sp = species[kind];
  const crown = crowns[kind];
  n++;
  return {
    id: "sample_plant_" + n, category: "vegetation", label: sp.common_name,
    insertion_point: { center_x_m: cx, center_y_m: cy },
    bounding_box: { top_left_x_m: Math.round((cx - crown / 2) * 100) / 100, top_left_y_m: Math.round((cy - crown / 2) * 100) / 100, width_m: crown, height_m: crown },
    transform: { rotation_deg: 0 },
    parameters: {
      version: "1.0", generator: "Sportify-Vegetation",
      vegetation: { ...sp, crown_m: crown, revit_family_name: "Sportify - " + sp.botanical_name },
    },
  };
};

const plants = [
  plant("carpinus", 60.3, 10.5),
  plant("pinus", 57.5, 5.5),
  plant("cornus", 58.0, 16.0),
  plant("pyrus", 63.5, 4.5),
  plant("cornus", 36.0, 3.5),
  plant("lavender", 26.0, 3.5), plant("lavender", 30.0, 3.5), plant("lavender", 44.0, 3.5),
  plant("fescue", 34.0, 4.8), plant("fescue", 48.0, 2.5), plant("fescue", 45.0, 18.0),
  plant("sedum", 6.0, 5.0), plant("sedum", 12.0, 12.0), plant("sedum", 17.0, 16.0), plant("sedum", 35.0, 17.0),
];

// Structural grid of the sample: lines 1-9 across the length (x), A-C across the width (y).
const gridLines = [];
for (let i = 0; i < 9; i++) { const x = Math.round(i * 8.45 * 100) / 100; gridLines.push({ name: String(i + 1), start_m: { x_m: x, y_m: 0 }, end_m: { x_m: x, y_m: 21.0 } }); }
["A", "B", "C"].forEach((name, j) => { const y = j * 10.5; gridLines.push({ name, start_m: { x_m: 0, y_m: y }, end_m: { x_m: 67.6, y_m: y } }); });
const columns = [];
for (let i = 0; i < 9; i++) for (let j = 0; j < 3; j++) columns.push({ label: "C" + (i + 1) + "-" + "ABC"[j], x_m: Math.round(i * 8.45 * 100) / 100, y_m: j * 10.5 });

const layout = {
  version: "1.3", generator: "Sportify-Combine",
  roof_context: { length_m: 67.6, width_m: 21.0, source_boundary_polygon: null, world_origin_x_m: 0, world_origin_y_m: 0, world_origin_z_m: 11.0, height_above_ground_m: 11.0, height_source: "ground floor level \"EG\"" },
  design_rules: { clearance_m: 1.0, boundary_setback_m: 1.5, circulation_width_m: 1.0, min_entry_points: 4, quiet_buffer_m: 3.0 },
  entry_points: [
    { x_m: 33.8, y_m: 0, edge: "top" }, { x_m: 33.8, y_m: 21.0, edge: "bottom" },
    { x_m: 0, y_m: 10.5, edge: "left" }, { x_m: 67.6, y_m: 10.5, edge: "right" },
  ],
  circulation_paths: [],
  site_location: { latitude_deg: 53.5511, longitude_deg: 9.9937, place_name: "Hamburg", date: "2026-09-19", time: "12:00",
                   address_parts: { country_code: "de", "ISO3166-2-lvl4": "DE-HH", city: "Hamburg" } },
  site_conditions: { wind_zone: 2, wind_zone_manual: false, wind_zone_confidence: "kreis",
                     wind_zone_source: "Hamburg (the whole state) (DIBt list, 2022-06-02)", north_deg: 20, north_set: true },
  assemblies, zones,
  placements: [court(1, 24.0), court(2, 38.4), ...plants],
  // The structural grid the way the Revit push sends it (canvas coordinates, y down): nine grid lines every 8.45 m along the length,
  // three across, and a column at every crossing. No deck capacity: the structural analysis then uses its placeholder and says so.
  structure: { source: "revit", deck_capacity_kn_m2: null, grid_lines: gridLines, columns },
};

fs.writeFileSync(path, JSON.stringify(layout, null, 2) + "\n");
console.log("wrote", path, "zones", zones.length, "plants", plants.length);
