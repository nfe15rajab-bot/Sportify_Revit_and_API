// Variants of the structural variants (and the bundled sample) for the dynamic checks: schedules, snow, given frequency.
const fs = require("fs");
const path = require("path");
const src = path.join(__dirname, "..", "layouts", "struct");
const out = path.join(__dirname, "..", "layouts", "dyn");
fs.mkdirSync(out, { recursive: true });
const load = n => JSON.parse(fs.readFileSync(src + "/" + n + ".json", "utf8"));
const write = (n, l) => fs.writeFileSync(out + "/" + n + ".json", JSON.stringify(l, null, 2));

write("d1-sample", load("v1-sample"));
{ const l = load("v1-sample"); l.site_conditions.day_schedule = "event_day"; l.site_conditions.snow_zone = "3"; l.site_conditions.altitude_m = 600; l.site_conditions.altitude_set = true; l.structure.natural_frequency_hz = 7.5; write("d2-event-zone3-given-frequency", l); }
{ const l = load("v1-sample"); l.site_conditions.day_schedule = "community_day"; l.site_conditions.snow_zone = "1a"; write("d3-community-zone1a", l); }
{ const l = load("v5-courts-right"); l.placements.find(p => p.category === "field").parameters.field.capacity.seats = 300; l.site_conditions.day_schedule = "event_day"; write("d4-courts-stands-event", l); }
write("d5-no-structure-activity-path", load("v6-activity-path"));
write("d6-courts-only", load("v7-courts-only"));
{ const l = load("v1-sample"); l.site_conditions = { wind_zone: null, north_deg: null, north_set: false }; l.roof_context.height_above_ground_m = 0; write("d7-no-site-data", l); }
// the same flags for altitude and orientation as the sun fixtures I and J: a value with its flag off (or no flag) is "not given" to Unity's reader, so to the add-in's
{ const l = load("v1-sample"); l.site_conditions.altitude_m = 600; l.site_conditions.altitude_set = false; l.site_conditions.north_deg = 0; l.site_conditions.north_set = false; write("d8-flags-off", l); }
{ const l = load("v1-sample"); l.site_conditions.altitude_m = 600; delete l.site_conditions.altitude_set; l.site_conditions.north_deg = 90; delete l.site_conditions.north_set; write("d9-values-no-flags", l); }
console.log("written to", out);
