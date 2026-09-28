// An IFC pre-flight: what is in an IFC file and what Sportify will make of it, read from the text of the file alone (no Revit, no IFC library).
//
//   node Tools/IfcScan/scan.js <model.ifc> [--json]
//
// An IFC is a text file of numbered entities (STEP). This reads the ones that matter for a roof of sports and garden: the schema and the program that wrote it, the length unit, the
// storeys and what each holds, the slabs and roofs (their outline, thickness and height), the columns, beams and grids, the site's coordinates and true north, and says in plain
// sentences what to do about what it finds. It cannot say what Revit will make of each element (a Floor, a Roof, a DirectShape in some category): that needs Revit.
//
// The rules of thumb behind the warnings were found on the Goldbeck parking garage model (thow_IFC_Model.ifc): no IfcRoof at all, the roof is the highest of eleven near-identical
// IfcSlab, the five grids are repeated, the site is Revit's default location.
const fs = require("fs");

// ---------------------------------------------------------------------------------------------------------------------------------- reading STEP
function decodeString(s) {
  if (s == null) return "";
  return s
    .replace(/\\X2\\((?:[0-9A-Fa-f]{4})+)\\X0\\/g, (_, hex) => hex.match(/.{4}/g).map(h => String.fromCharCode(parseInt(h, 16))).join(""))
    .replace(/\\X\\([0-9A-Fa-f]{2})/g, (_, hex) => String.fromCharCode(parseInt(hex, 16)))
    .replace(/''/g, "'");
}

/** The top-level arguments of one entity, as raw text. */
function splitArgs(args) {
  const out = []; let depth = 0, cur = "", inStr = false;
  for (let i = 0; i < args.length; i++) {
    const c = args[i];
    if (inStr) { cur += c; if (c === "'" && args[i + 1] === "'") { cur += "'"; i++; } else if (c === "'") inStr = false; continue; }
    if (c === "'") { inStr = true; cur += c; continue; }
    if (c === "(") depth++;
    if (c === ")") depth--;
    if (c === "," && depth === 0) { out.push(cur.trim()); cur = ""; continue; }
    cur += c;
  }
  out.push(cur.trim());
  return out;
}

const text = s => decodeString((s || "").replace(/^'|'$/g, ""));
const ref = s => { const m = /^#(\d+)$/.exec(s || ""); return m ? +m[1] : null; };
const refs = s => (s || "").match(/#\d+/g)?.map(x => +x.slice(1)) || [];
const numbers = s => (s || "").match(/-?\d+\.?\d*(?:E[-+]?\d+)?/gi)?.map(Number) || [];
const enumName = s => (s || "").replace(/\./g, "");

function parse(source) {
  const data = source.indexOf("DATA;");
  const header = source.slice(0, data < 0 ? 0 : data);
  const body = data < 0 ? "" : source.slice(data + 5, source.lastIndexOf("ENDSEC;"));
  const ents = new Map(), counts = new Map();
  for (const chunk of body.split(/;\s*\n/)) {
    const m = /^\s*#(\d+)\s*=\s*([A-Z0-9_]+)\s*\(([\s\S]*)\)\s*;?\s*$/.exec(chunk.trim());
    if (!m) continue;
    ents.set(+m[1], { id: +m[1], type: m[2], args: m[3] });
    counts.set(m[2], (counts.get(m[2]) || 0) + 1);
  }
  return { header, ents, counts };
}

// ---------------------------------------------------------------------------------------------------------------------------------- the scan
const DEFAULT_SITES = [{ name: "Revit's default project location (near Boston)", lat: 42.387, lon: -71.242 }];

function dms(list) {
  const n = numbers(list);
  if (!n.length) return null;
  const sign = n.find(x => x !== 0) < 0 ? -1 : 1;
  return sign * (Math.abs(n[0] || 0) + Math.abs(n[1] || 0) / 60 + Math.abs(n[2] || 0) / 3600 + Math.abs(n[3] || 0) / 3.6e9);
}

function lengthScale(ents) {
  for (const e of ents.values()) {
    if (e.type !== "IFCSIUNIT" || !/\.LENGTHUNIT\./.test(e.args)) continue;
    const p = splitArgs(e.args);
    const prefix = enumName(p[2]);
    const f = { MILLI: 0.001, CENTI: 0.01, DECI: 0.1, KILO: 1000, "$": 1, "": 1 }[prefix];
    return { scale: f == null ? 1 : f, label: (prefix && prefix !== "$" ? prefix.toLowerCase() : "") + "metre" };
  }
  for (const e of ents.values()) {
    if (e.type !== "IFCCONVERSIONBASEDUNIT" || !/LENGTHUNIT/.test(e.args)) continue;
    const name = text(splitArgs(e.args)[2]).toLowerCase();
    if (name.includes("foot") || name.includes("feet")) return { scale: 0.3048, label: "foot" };
    if (name.includes("inch")) return { scale: 0.0254, label: "inch" };
  }
  return { scale: 1, label: "metre (not stated)" };
}

function scan(source) {
  const { header, ents, counts } = parse(source);
  const get = id => ents.get(id);
  const of = t => [...ents.values()].filter(e => e.type === t);
  const unit = lengthScale(ents);
  const M = v => Math.round(v * unit.scale * 1000) / 1000;      // a length of the file, in metres

  const file = {
    schema: /FILE_SCHEMA\(\('?([^')]*)/.exec(header)?.[1] || null,
    author: text(/FILE_NAME\((?:'[^']*'),(?:'[^']*'),(?:\([^)]*\)),(?:\([^)]*\)),(?:'[^']*'),('[^']*')/.exec(header)?.[1] || "") || null,
    description: (/FILE_DESCRIPTION\(\(([^;]*)\),'/.exec(header)?.[1] || "").replace(/','/g, "; ").replace(/^'|'$/g, "") || null,
  };
  const project = of("IFCPROJECT")[0];
  const projectName = project ? text(splitArgs(project.args)[2]) : null;

  // placement: the translation of an element in the world (rotations do not change a height)
  function placementOf(id, depth = 0) {
    const e = get(id);
    if (!e || e.type !== "IFCLOCALPLACEMENT" || depth > 30) return [0, 0, 0];
    const p = splitArgs(e.args);
    const parent = ref(p[0]) ? placementOf(ref(p[0]), depth + 1) : [0, 0, 0];
    const axis = get(ref(p[1]));
    const off = axis && axis.type === "IFCAXIS2PLACEMENT3D" ? numbers(get(ref(splitArgs(axis.args)[0]))?.args) : [0, 0, 0];
    return [parent[0] + (off[0] || 0), parent[1] + (off[1] || 0), parent[2] + (off[2] || 0)];
  }
  function extrusions(id, out = [], depth = 0) {
    const e = get(id);
    if (!e || depth > 8) return out;
    if (e.type === "IFCEXTRUDEDAREASOLID") { out.push(e); return out; }
    for (const r of refs(e.args)) if (r !== id) extrusions(r, out, depth + 1);
    return out;
  }
  function profile(profileId) {
    const e = get(profileId);
    if (!e) return null;
    if (e.type === "IFCRECTANGLEPROFILEDEF") { const p = splitArgs(e.args); return { w: +p[3], h: +p[4], corners: 4 }; }
    const pts = [];
    const walk = (id, depth = 0) => { const x = get(id); if (!x || depth > 6) return; if (x.type === "IFCCARTESIANPOINT") pts.push(numbers(x.args)); else refs(x.args).forEach(r => walk(r, depth + 1)); };
    walk(profileId);
    if (!pts.length) return null;
    const xs = pts.map(p => p[0]), ys = pts.map(p => p[1]);
    const distinct = new Set(pts.map(p => p[0] + "," + p[1])).size;
    return { w: Math.max(...xs) - Math.min(...xs), h: Math.max(...ys) - Math.min(...ys), corners: distinct };
  }

  // the storeys and what each holds
  const storeys = of("IFCBUILDINGSTOREY").map(e => ({ id: e.id, name: text(splitArgs(e.args)[2]), elevationM: M(+splitArgs(e.args)[9] || 0), holds: {} }));
  const storeyOf = new Map();
  for (const r of of("IFCRELCONTAINEDINSPATIALSTRUCTURE")) {
    const p = splitArgs(r.args);
    const storey = storeys.find(s => s.id === ref(p[5]));
    for (const id of refs(p[4])) { const t = get(id)?.type; if (!t) continue; storeyOf.set(id, storey || null); if (storey) storey.holds[t.replace(/^IFC/, "")] = (storey.holds[t.replace(/^IFC/, "")] || 0) + 1; }
  }
  storeys.sort((a, b) => a.elevationM - b.elevationM);

  function element(e) {
    const p = splitArgs(e.args);
    const z = placementOf(ref(p[5]))[2];
    const solid = extrusions(ref(p[6]))[0];
    const args = solid ? splitArgs(solid.args) : null;
    const prof = args ? profile(ref(args[0])) : null;
    const depth = args ? +args[3] : null;
    return {
      id: e.id, type: e.type.replace(/^IFC/, ""), name: text(p[2]), predefined: enumName(p[p.length - 1]) || null, storey: storeyOf.get(e.id)?.name || null,
      elevationM: M(z), thicknessM: depth == null ? null : M(depth), topM: depth == null ? null : M(z + depth),
      outline: prof ? { widthM: M(prof.w), depthM: M(prof.h), corners: prof.corners } : null,
    };
  }
  const roofs = of("IFCROOF").map(element);
  const slabs = of("IFCSLAB").map(element).sort((a, b) => b.elevationM - a.elevationM);
  const coverings = of("IFCCOVERING").map(element);

  // the probable roof: an IfcRoof, else a slab said to be a roof, else the highest slab
  let probable = null, why = "";
  if (roofs.length) { probable = roofs.sort((a, b) => b.elevationM - a.elevationM)[0]; why = "the highest IfcRoof"; }
  else if (slabs.some(s => s.predefined === "ROOF")) { probable = slabs.find(s => s.predefined === "ROOF"); why = "the highest IfcSlab whose type is ROOF"; }
  else if (slabs.length) { probable = slabs[0]; why = "the highest IfcSlab (the file has no IfcRoof)"; }
  const alike = probable && probable.outline ? slabs.filter(s => s.id !== probable.id && s.outline && Math.abs(s.outline.widthM * s.outline.depthM - probable.outline.widthM * probable.outline.depthM) < 0.02 * probable.outline.widthM * probable.outline.depthM).length : 0;

  // the site, true north, the map conversion
  const site = of("IFCSITE")[0];
  const sp = site ? splitArgs(site.args) : null;
  const lat = sp ? dms(sp[9]) : null, lon = sp ? dms(sp[10]) : null;
  const ctx = of("IFCGEOMETRICREPRESENTATIONCONTEXT").find(c => /Model/.test(c.args)) || of("IFCGEOMETRICREPRESENTATIONCONTEXT")[0];
  const north = ctx ? numbers(get(ref(splitArgs(ctx.args)[5]))?.args) : null;
  const mapConversion = of("IFCMAPCONVERSION")[0];
  const defaultSite = lat != null && lon != null ? DEFAULT_SITES.find(d => Math.abs(d.lat - lat) < 0.01 && Math.abs(d.lon - lon) < 0.01) || null : null;

  const grids = of("IFCGRID");
  const gridKeys = grids.map(g => { const p = splitArgs(g.args); return refs(p[7]).length + "x" + refs(p[8]).length; });
  const columnStoreys = storeys.filter(s => s.holds.COLUMN).map(s => s.name + " (" + s.holds.COLUMN + ")");
  const structure = { columnStoreys, columns: counts.get("IFCCOLUMN") || 0, beams: counts.get("IFCBEAM") || 0, members: counts.get("IFCMEMBER") || 0, walls: (counts.get("IFCWALL") || 0) + (counts.get("IFCWALLSTANDARDCASE") || 0), railings: counts.get("IFCRAILING") || 0, ramps: counts.get("IFCRAMP") || 0, furniture: counts.get("IFCFURNITURE") || 0, proxies: counts.get("IFCBUILDINGELEMENTPROXY") || 0, grids: grids.length, gridAxes: gridKeys };

  const result = {
    file, project: projectName, lengthUnit: unit.label, entities: ents.size, storeys, roofs, slabs, coverings, probableRoof: probable ? { ...probable, why, otherSlabsAlike: alike } : null,
    site: sp ? { name: text(sp[2]), latitude: lat, longitude: lon, elevationM: M(+sp[11] || 0), isRevitDefault: !!defaultSite } : null,
    trueNorthVector: north && north.length >= 2 ? north.slice(0, 2) : null, mapConversion: mapConversion ? mapConversion.args.slice(0, 200) : null,
    structure, topStorey: storeys.length ? storeys.filter(s => s.name && Object.keys(s.holds).length).slice(-1)[0] || null : null,
  };
  result.warnings = warnings(result);
  return result;
}

function warnings(r) {
  const w = [];
  if (!r.slabs.length && !r.roofs.length) w.push("The file has no IfcRoof and no IfcSlab: Sportify will not find a roof. Model the roof as a roof or a floor (slab) and export again.");
  if (r.roofs.length === 0 && r.slabs.length) {
    const p = r.probableRoof;
    w.push("There is no IfcRoof: the roof is a slab. In Revit it arrives as a Floor (or a DirectShape in the Floors category), and Push to Sportify takes floors as well as roofs. The probable roof is '" + p.name.replace(/:\d+$/, "") + "' at " + p.elevationM + " m (top " + (p.topM ?? "?") + " m)"
           + (p.storey ? ", storey " + p.storey : "") + (p.outline ? ", " + p.outline.widthM + " x " + p.outline.depthM + " m with " + p.outline.corners + " corners" : "") + ".");
    if (p.otherSlabsAlike > 0) w.push(p.otherSlabsAlike + " other slab(s) have the same size as that one, one per deck: select the HIGHEST floor before Push to Sportify (a lower deck gives a roof of the right size at the wrong height: the sun, the wind and the height above ground would be wrong).");
  }
  if (r.site && r.site.isRevitDefault) w.push("The file's site is Revit's default location (near Boston): the model carries no real location. Set the address in the web app's Site tab (Sportify's sun, wind zone and orientation come from there) and, if the Revit project should match, use Set Sun + Location.");
  if (r.site && r.site.latitude == null) w.push("The file gives no site latitude and longitude: set the address in the web app's Site tab.");
  if (r.trueNorthVector && (Math.abs(r.trueNorthVector[0]) > 1e-6)) w.push("True north is turned in this file (direction " + r.trueNorthVector.map(x => Math.round(x * 1000) / 1000).join(", ") + "): the roof frame follows the model, so check the orientation in the app's Site tab.");
  if (r.structure.grids > 1 && new Set(r.structure.gridAxes).size === 1) w.push("The grid is repeated " + r.structure.grids + " times (" + r.structure.gridAxes[0] + " axes each, one per storey): Revit will hold each line " + r.structure.grids + " times, so Sportify reads the structural grid from overlapping duplicates. Check the bays in the Structure inputs tab.");
  if (r.structure.columns > 0) w.push(r.structure.columns + " columns and " + r.structure.beams + " beams: Sportify reads structural columns only when Revit makes them family instances of the Structural Columns category; an IFC import may make them DirectShapes, and then the push finds none: look at the Structure inputs tab after the push, and enter the columns or the deck capacity by hand if so." + (r.structure.columnStoreys.length ? " The columns are held by the storey(s) " + r.structure.columnStoreys.join(", ") + "." : ""));
  if (r.lengthUnit !== "metre" && r.lengthUnit !== "millimetre") w.push("The length unit is " + r.lengthUnit + ": check that dimensions come out right in Revit.");
  return w;
}

// ---------------------------------------------------------------------------------------------------------------------------------- the words
function report(r) {
  const L = [];
  L.push("IFC PRE-FLIGHT" + (r.project ? "  " + r.project : ""));
  L.push("schema " + r.file.schema + "; written by " + (r.file.author || "?") + "; " + r.entities + " entities; length unit " + r.lengthUnit);
  if (r.file.description) L.push("description: " + r.file.description);
  if (r.site) L.push("site '" + r.site.name + "': lat " + (r.site.latitude == null ? "-" : r.site.latitude.toFixed(4)) + ", lon " + (r.site.longitude == null ? "-" : r.site.longitude.toFixed(4)) + ", elevation " + r.site.elevationM + " m" + (r.site.isRevitDefault ? "  (Revit's default location)" : ""));
  L.push("true north vector " + (r.trueNorthVector ? r.trueNorthVector.map(x => Math.round(x * 1e6) / 1e6).join(", ") : "-") + "; map conversion " + (r.mapConversion ? "yes" : "none"));
  L.push("");
  L.push("STOREYS (elevation m, what they hold)");
  for (const s of r.storeys) L.push("  " + String(s.elevationM).padStart(8) + "  " + s.name.padEnd(30) + " " + Object.entries(s.holds).sort((a, b) => b[1] - a[1]).map(([t, n]) => t + " " + n).join(", "));
  L.push("");
  L.push("ROOFS " + r.roofs.length + ", SLABS " + r.slabs.length + ", COVERINGS " + r.coverings.length);
  for (const s of r.slabs) L.push("  slab z=" + s.elevationM + " m  thickness " + s.thicknessM + " m  " + (s.outline ? s.outline.widthM + " x " + s.outline.depthM + " m, " + s.outline.corners + " corners" : "outline not read") + "  " + s.predefined + "  '" + s.name.replace(/:\d+$/, "") + "'  storey " + s.storey);
  for (const s of r.roofs) L.push("  roof z=" + s.elevationM + " m  " + (s.outline ? s.outline.widthM + " x " + s.outline.depthM + " m" : "") + "  '" + s.name + "'");
  L.push("");
  L.push("STRUCTURE: " + Object.entries(r.structure).filter(([k]) => k !== "gridAxes" && k !== "columnStoreys").map(([k, v]) => k + " " + v).join(", "));
  L.push("");
  L.push("WHAT TO DO");
  r.warnings.forEach((w, i) => L.push("  " + (i + 1) + ". " + w));
  if (!r.warnings.length) L.push("  Nothing stands out.");
  return L.join("\n");
}

module.exports = { scan, report, parse, decodeString, splitArgs };

if (require.main === module) {
  const args = process.argv.slice(2);
  const file = args.find(a => !a.startsWith("--"));
  if (!file) { console.error("usage: node scan.js <model.ifc> [--json]"); process.exit(2); }
  const result = scan(fs.readFileSync(file, "latin1"));
  console.log(args.includes("--json") ? JSON.stringify(result, null, 1) : report(result));
}
