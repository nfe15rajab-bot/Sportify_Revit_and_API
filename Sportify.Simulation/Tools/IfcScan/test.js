// The IFC pre-flight (scan.js) on small IFC files written here, so that what it says about a file is pinned: the roof it picks, the sentences it warns with, the units and strings it reads.
//   node Tools/IfcScan/test.js
const { scan, report, decodeString, splitArgs } = require("./scan.js");

let fails = 0;
const check = (name, ok, extra = "") => { if (!ok) fails++; console.log((ok ? "PASS  " : "FAIL  ") + name + (extra ? "  " + extra : "")); };

/** A small IFC: mm, Revit's default site, three storeys, a slab of 10 x 5 m and 200 mm per storey, columns, a repeated grid. Options change what it has. */
function ifc(opt = {}) {
  const L = [];
  let n = 0;
  const add = s => { n++; L.push("#" + n + "=" + s + ";"); return n; };
  L.push("ISO-10303-21;", "HEADER;", "FILE_DESCRIPTION(('ViewDefinition [CoordinationView]'),'2;1');", "FILE_NAME('t.ifc','2026-06-23T16:27:58+01:00',(''),(''),'ODA SDAI','Autodesk Revit 25.4 (DEU)','');", "FILE_SCHEMA(('" + (opt.schema || "IFC4") + "'));", "ENDSEC;", "DATA;");
  const unit = opt.unit === "metre" ? "IFCSIUNIT(*,.LENGTHUNIT.,$,.METRE.)" : "IFCSIUNIT(*,.LENGTHUNIT.,.MILLI.,.METRE.)";
  const k = opt.unit === "metre" ? 1 : 1000;
  add(unit);
  const origin = add("IFCCARTESIANPOINT((0.,0.,0.))");
  const axis0 = add("IFCAXIS2PLACEMENT3D(#" + origin + ",$,$)");
  const dirNorth = add(opt.turned ? "IFCDIRECTION((0.5,0.866))" : "IFCDIRECTION((6.123233995736766E-017,1.))");
  add("IFCGEOMETRICREPRESENTATIONCONTEXT($,'Model',3,0.01,#" + axis0 + ",#" + dirNorth + ")");
  add("IFCPROJECT('1abc',$,'Test',$,$,$,$,$,$)");
  const lat = opt.site === "berlin" ? "(52,31,2,100000)" : "(42,23,13,199999)", lon = opt.site === "berlin" ? "(13,23,42,500000)" : "(-71,-14,-31,-199999)";
  add("IFCSITE('2abc',$,'" + (opt.site === "berlin" ? "Goldbeck" : "Default") + "',$,$,$,$,$,.ELEMENT.," + lat + "," + lon + ",0.,$,$)");
  const storeys = [];
  const levels = opt.levels || [0, 3000, 6000];
  levels.forEach((z, i) => storeys.push({ id: add("IFCBUILDINGSTOREY('s" + i + "',$,'E" + i + "',$,$,$,$,$,.ELEMENT.," + (z / 1000 * k).toFixed(3) + ")"), z: z / 1000 * k }));
  const slabIds = [];
  const slabsOf = opt.noSlabs ? [] : storeys;
  for (const s of slabsOf) {
    const off = add("IFCCARTESIANPOINT((0.,0.," + s.z.toFixed(3) + "))");
    const ax = add("IFCAXIS2PLACEMENT3D(#" + off + ",$,$)");
    const place = add("IFCLOCALPLACEMENT($,#" + ax + ")");
    const prof = add("IFCRECTANGLEPROFILEDEF(.AREA.,$,$," + (10 * k) + "," + (5 * k) + ")");
    const ext = add("IFCEXTRUDEDAREASOLID(#" + prof + ",#" + axis0 + ",#" + add("IFCDIRECTION((0.,0.,1.))") + "," + (0.2 * k) + ")");
    const rep = add("IFCSHAPEREPRESENTATION($,'Body','SweptSolid',(#" + ext + "))");
    const shape = add("IFCPRODUCTDEFINITIONSHAPE($,$,(#" + rep + "))");
    const name = opt.germanName ? "Geschossdecke\\X\\E4:Decke\\X2\\00FC\\X0\\:" + (100 + s.z) : "Slab:Deck:" + (100 + s.z);
    slabIds.push(add("IFCSLAB('sl" + s.z + "',$,'" + name + "',$,$,#" + place + ",#" + shape + ",$,." + (opt.roofType && s === storeys[storeys.length - 1] ? "ROOF" : "FLOOR") + ".)"));
  }
  if (opt.roof) {
    const off = add("IFCCARTESIANPOINT((0.,0.," + (9 * k) + "))");
    const ax = add("IFCAXIS2PLACEMENT3D(#" + off + ",$,$)");
    const place = add("IFCLOCALPLACEMENT($,#" + ax + ")");
    add("IFCROOF('r1',$,'Main roof',$,$,#" + place + ",$,$,.FLAT_ROOF.)");
  }
  storeys.forEach((s, i) => { if (slabIds[i]) add("IFCRELCONTAINEDINSPATIALSTRUCTURE('c" + i + "',$,$,$,(#" + slabIds[i] + "),#" + s.id + ")"); });
  for (let i = 0; i < (opt.columns || 0); i++) add("IFCCOLUMN('col" + i + "',$,'Column " + i + "',$,$,$,$,$,.COLUMN.)");
  for (let g = 0; g < (opt.grids || 0); g++) {
    const axes = [];
    for (let a = 0; a < 3; a++) axes.push(add("IFCGRIDAXIS('" + a + "',$,.T.)"));
    const v = [];
    for (let a = 0; a < 2; a++) v.push(add("IFCGRIDAXIS('v" + a + "',$,.T.)"));
    add("IFCGRID('g" + g + "',$,'Default Grid',$,$,$,$,(" + axes.map(x => "#" + x).join(",") + "),(" + v.map(x => "#" + x).join(",") + "),$)");
  }
  L.push("ENDSEC;", "END-ISO-10303-21;");
  return L.join("\n");
}

// ---------------------------------------------------------------------------------------------------------------------------------- strings
check("IFC strings: \\X\\E4 is a-umlaut, \\X2\\00FC\\X0\\ is u-umlaut, '' is an apostrophe", decodeString("Geschossdecke\\X\\E4 \\X2\\00FC00E4\\X0\\ it''s") === "Geschossdeckeä üä it's");
check("arguments are split at the top level only (a comma inside a string or a list stays)", JSON.stringify(splitArgs("'a,b',(1,2),#3,$")) === JSON.stringify(["'a,b'", "(1,2)", "#3", "$"]));

// ---------------------------------------------------------------------------------------------------------------------------------- a model without a roof
let r = scan(ifc({ columns: 3, grids: 3 }));
check("the file's schema, author and length unit are read", r.file.schema === "IFC4" && /Revit/.test(r.file.author) && r.lengthUnit === "millimetre" && r.project === "Test");
check("the storeys come in order of height, in metres, and know what they hold", r.storeys.map(s => s.name).join() === "E0,E1,E2" && r.storeys.map(s => s.elevationM).join() === "0,3,6" && r.storeys[2].holds.SLAB === 1);
check("slabs are read with their height, thickness and outline, highest first", r.slabs.length === 3 && r.slabs[0].elevationM === 6 && r.slabs[0].thicknessM === 0.2 && r.slabs[0].topM === 6.2 && r.slabs[0].outline.widthM === 10 && r.slabs[0].outline.depthM === 5 && r.slabs[0].outline.corners === 4);
check("with no IfcRoof the highest slab is the probable roof, and the other slabs of its size are counted", r.probableRoof.elevationM === 6 && /highest IfcSlab/.test(r.probableRoof.why) && r.probableRoof.otherSlabsAlike === 2);
check("it says the roof is a slab and to select the HIGHEST floor, and why", r.warnings.some(w => /no IfcRoof: the roof is a slab/.test(w) && /6 m/.test(w)) && r.warnings.some(w => /2 other slab/.test(w) && /HIGHEST floor/.test(w) && /wrong height/.test(w)));
check("Revit's default site is recognised and the warning says where the real location comes from", r.site.isRevitDefault && r.warnings.some(w => /default location/.test(w) && /Site tab/.test(w)));
check("a grid repeated once per storey is reported as repeated", r.structure.grids === 3 && r.warnings.some(w => /repeated 3 times/.test(w)));
check("columns are counted and the warning tells what to check when the push finds none", r.structure.columns === 3 && r.warnings.some(w => /3 columns/.test(w) && /DirectShape/.test(w)));

// ---------------------------------------------------------------------------------------------------------------------------------- variants
r = scan(ifc({ roof: true }));
check("an IfcRoof is the roof, and no warning says the roof is a slab", r.roofs.length === 1 && r.probableRoof.type === "ROOF" && /IfcRoof/.test(r.probableRoof.why) && !r.warnings.some(w => /roof is a slab/.test(w)));
r = scan(ifc({ roofType: true }));
check("a slab whose type is ROOF wins over a higher floor", r.probableRoof.predefined === "ROOF" && /type is ROOF/.test(r.probableRoof.why));
r = scan(ifc({ noSlabs: true }));
check("a file with neither a roof nor a slab says Sportify will not find one", r.probableRoof === null && r.warnings.some(w => /no IfcRoof and no IfcSlab/.test(w)));
r = scan(ifc({ site: "berlin" }));
check("a real site is not called the default, and its latitude and longitude are read from degrees, minutes and seconds", !r.site.isRevitDefault && Math.abs(r.site.latitude - 52.5173) < 0.001 && Math.abs(r.site.longitude - 13.3951) < 0.001 && !r.warnings.some(w => /default location/.test(w)));
r = scan(ifc({ unit: "metre" }));
check("a file in metres is read as metres", r.lengthUnit === "metre" && r.slabs[0].elevationM === 6 && r.slabs[0].outline.widthM === 10);
r = scan(ifc({ turned: true }));
check("a turned true north is reported", r.trueNorthVector[0] === 0.5 && r.warnings.some(w => /True north is turned/.test(w)));
r = scan(ifc({ germanName: true }));
check("names are decoded (umlauts)", r.slabs[0].name.startsWith("Geschossdeckeä:Deckeü"));
check("the report is text a person can read: it names the roof and the storeys", (() => { const t = report(scan(ifc({ columns: 1 }))); return /IFC PRE-FLIGHT/.test(t) && /STOREYS/.test(t) && /WHAT TO DO/.test(t) && /E2/.test(t); })());
check("an empty or broken file does not throw and says there is nothing to find", (() => { const x = scan("not an ifc"); return x.entities === 0 && x.probableRoof === null && x.warnings.length > 0; })());

console.log(fails === 0 ? "\nALL IFC SCAN CHECKS PASSED" : "\n" + fails + " CHECK(S) FAILED");
process.exit(fails ? 1 : 0);
