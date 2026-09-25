// The release: what an installer, a license page, a manifest and a version number say about Sportify must be the same thing everywhere. Nothing here needs Revit, Inno Setup or a
// build: it reads the files that make the release and compares them.
//
//   node Tools/ReleaseCheck/check.js [path of the web app (sportfify_goldbeck), for its Content-Security-Policy]
//
// Compared: VERSION (a Semantic Version) against what builds from it; the license text against what the team asked it to say; the author Revit shows (the manifest) against the
// installer's; the Sportify folder's subfolders in SportifyWorkspace.cs against the installer's, the user guide's and the release notes'; the worksets and phases sheet against the
// add-in's own lists; the port the installer waits for against the API's configuration, the add-in's allowed web origins and the web app's policy.
const fs = require("fs");
const path = require("path");

const repo = path.join(__dirname, "..", "..", "..");
const read = (...p) => fs.readFileSync(path.join(repo, ...p), "utf8");
const exists = (...p) => fs.existsSync(path.join(repo, ...p));
const webArg = process.argv[2];

let problems = 0;
const check = (ok, what, detail) => { if (!ok) { problems++; console.log("DIFFERENT " + what + (detail ? " (" + detail + ")" : "")); } };

// ---- the version
const version = read("VERSION").trim();
check(/^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$/.test(version), "VERSION is a Semantic Version (MAJOR.MINOR.PATCH, -beta.N while it is under testing)", version);
const props = read("Directory.Build.props");
check(props.includes("VERSION") && props.includes("<Version>"), "Directory.Build.props builds every project's version from the VERSION file");
const iss = read("Sportify.Setup", "Sportify.iss");
check(/OutputBaseFilename=Sportify-Setup-\{#AppVersion\}-Revit\{#RevitYear\}/.test(iss), "the installer is named Sportify-Setup-<version>-Revit<year>.exe");
const build = read("Sportify.Setup", "Build-Installer.ps1");
check(build.includes('"VERSION"') && build.includes("/DAppVersion="), "Build-Installer.ps1 takes the version from VERSION and hands it to Inno Setup");
const release = exists(".github", "workflows", "release.yml") ? read(".github", "workflows", "release.yml") : "";
check(release.length > 0, "the release workflow exists");
check(/tags:\s*\[?\s*["']v\*/.test(release), "the release workflow starts on a version tag (v*)");
check(release.includes("VERSION") && /tag/i.test(release), "the release workflow checks that the tag names the version in VERSION");
check(release.includes("Sportify-Setup") && !release.includes("Sportify_Revit_${{"), "the release publishes the Inno installer");

// ---- the license text: what the team asked it to say
const license = read("Sportify.Setup", "LICENSE_AGREEMENT.txt");
for (const phrase of ["TH OWL", "School of Architecture", "MID project", "architecture and", "engineering backgrounds", "GOLDBECK", "Claude (Anthropic)", "computational design team",
                      "Nada", "Moamen", "Sukriti", "Ali", "https://github.com/nfe15rajab-bot/Sportify_Revit_and_API", "https://github.com/nfe15rajab-bot/sportfify_goldbeck",
                      "open source", "educational purposes", "still under testing and deployment", "Digital Tools and Methods - Group of Sports and Gardens", "I accept the agreement"])
    check(license.includes(phrase), "the license agreement says: " + phrase);
check(/computational and facade design students/i.test(license), "the license agreement says the students are computational and facade design students");
check(iss.includes("LicenseFile=LICENSE_AGREEMENT.txt"), "the installer shows LICENSE_AGREEMENT.txt on its license page");
check(license.split(/\r?\n/).length < 80, "the license agreement is short (under 80 lines)", license.split(/\r?\n/).length + " lines");

// ---- the author Revit shows
const publisher = /#define Publisher "([^"]+)"/.exec(iss)?.[1];
const manifest = read("SportfyRevit", "SportfyRevit", "SportfyRevit.addin");
const vendor = /<VendorDescription>([^<]*)<\/VendorDescription>/.exec(manifest)?.[1];
check(publisher === "Digital Tools and Methods - Group of Sports and Gardens", "the installer's publisher is 'Digital Tools and Methods - Group of Sports and Gardens'", publisher);
check(vendor === publisher, "the add-in manifest's author (VendorDescription) is the installer's publisher", vendor + " / " + publisher);
check(iss.includes("<VendorDescription>{#Publisher}</VendorDescription>"), "the manifest the installer writes carries the same author");
check(/<AddInId>([0-9a-f-]+)<\/AddInId>/.exec(manifest)?.[1] === /<AddInId>([0-9a-f-]+)<\/AddInId>/.exec(iss)?.[1], "the installer writes the same AddInId as the manifest in the repository");
check(props.includes("Digital Tools and Methods - Group of Sports and Gardens"), "the assemblies carry the author too (Directory.Build.props)");

// ---- the Sportify folder
const workspace = read("SportfyRevit", "SportfyRevit", "SportifyWorkspace.cs");
const folders = [...workspace.matchAll(/new\("([a-z]+)", "([^"]+)", "/g)].map(m => m[2]);
const issFolders = /WorkspaceFolders = '([^']+)'/.exec(iss)?.[1]?.split(",") ?? [];
check(folders.length > 0 && JSON.stringify(folders) === JSON.stringify(issFolders), "the installer makes the Sportify folder's subfolders that SportifyWorkspace.cs names", folders.join(",") + " / " + issFolders.join(","));
const guide = read("Sportify.Setup", "docs", "USER_GUIDE.html");
for (const f of folders) check(guide.includes("<td>" + f + "</td>"), "the user guide lists the subfolder " + f);
check(guide.includes("{{VERSION}}"), "the user guide has its version placeholder");
check(exists("WORKSPACE.md") && folders.every(f => read("WORKSPACE.md").includes("`" + f + "`")), "WORKSPACE.md lists every subfolder");

// ---- the worksets and phases sheet
const sheet = JSON.parse(read("Sportify.Setup", "Library", "Worksets", "Sportify_Worksets_and_Phases.json"));
const worksetSource = read("SportfyRevit", "SportfyRevit", "SportifyWorksetSet.cs");
const worksets = [...worksetSource.matchAll(/internal const string \w+ = "([^"]+)";/g)].map(m => m[1]);
check(JSON.stringify(sheet.worksets.map(w => w.name).sort()) === JSON.stringify(worksets.sort()), "the sheet lists the worksets SportifyWorksetSet makes", sheet.worksets.map(w => w.name).join(",") + " / " + worksets.join(","));
const phaseSource = read("SportfyRevit", "SportfyRevit", "SportifyPhases.cs");
const phases = [...phaseSource.matchAll(/internal const string \w+ = "([^"]+)";/g)].map(m => m[1]);
check(JSON.stringify(sheet.phases.map(p => p.name)) === JSON.stringify(phases), "the sheet lists the phases SportifyPhases names, in order", sheet.phases.map(p => p.name).join(",") + " / " + phases.join(","));
const bim = read("SportfyRevit", "SportfyRevit", "BimRules.cs");
const importWorksets = /WorksetNames = \{ ([^}]*) \}/.exec(bim)?.[1]?.match(/\w+Workset/g)?.map(n => new RegExp('public const string ' + n + ' = "([^"]+)"').exec(bim)?.[1]) ?? [];
check(JSON.stringify(sheet.importWorksets.map(w => w.name)) === JSON.stringify(importWorksets), "the sheet lists the worksets an import makes (BimRules.WorksetNames)", sheet.importWorksets.map(w => w.name).join(",") + " / " + importWorksets.join(","));

// ---- the local API the installer starts and the web app it opens
const api = read("Sportify.Api", "Sportify.Api", "appsettings.json");
const apiPort = /"Urls":\s*"http:\/\/localhost:(\d+)"/.exec(api)?.[1];
const issPort = /#define ApiPort "(\d+)"/.exec(iss)?.[1];
check(apiPort && apiPort === issPort, "the installer waits for the API on the port the API listens on", apiPort + " / " + issPort);
const guard = read("SportfyRevit", "SportfyRevit", "LocalRequestGuard.cs");
check(guard.includes("http://localhost:" + apiPort) && guard.includes("http://127.0.0.1:" + apiPort), "the add-in accepts the web app when the API serves it (localhost:" + apiPort + ")");
check(read("Sportify.Api", "Sportify.Api", "Program.cs").includes("UseStaticFiles"), "the API serves the web app it is installed beside");
for (const ps of ["Open-Sportify-WebApp.ps1", "Stop-Sportify.ps1", "Open-Sportify-InRevit.ps1"]) check(exists("Sportify.Setup", "tools", ps) && iss.includes(ps), "the installer ships and uses " + ps);
check(read("Sportify.Setup", "tools", "Open-Sportify-WebApp.ps1").includes("localhost:" + apiPort), "the web app launcher opens localhost:" + apiPort);
if (webArg && fs.existsSync(path.join(webArg, "index.html"))) {
    const html = fs.readFileSync(path.join(webArg, "index.html"), "utf8");
    check(html.includes("http://localhost:" + apiPort), "the web app's Content-Security-Policy allows its own API on " + apiPort);
}

console.log(problems === 0 ? "RELEASE OK: version " + version + ", the license, the author, the Sportify folder (" + folders.length + " subfolders), the worksets and phases, the API port and the installer agree" : problems + " DIFFERENCE(S) in what makes the release");
process.exit(problems === 0 ? 0 : 1);
