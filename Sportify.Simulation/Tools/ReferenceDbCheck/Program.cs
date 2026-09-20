using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Sportify.Api.Data;

// usage: ReferenceDbCheck                       the tests
//        ReferenceDbCheck --dry-run <reference.db>   what the guard would do to that file (on a copy)
// Runs ReferenceDbGuard (what Sportify.Api does at startup on reference.db) on throw-away database files: a fresh one, one made before the guard, one missing a column
// (the "no such column: p.CrownM" case), one with rows added through the Data tab, one held open by another program, and the three policies. Each must end with the
// old file kept somewhere when it was replaced, and left alone when it was not. What the catalog seeders add is added IN PLACE (CatalogSeeding: tables, columns, rows by
// key), so a file that only lacks that is brought up to date with everything entered kept, and a rebuild is only for what cannot be added that way.
// --dry-run <path to reference.db>: what the guard WOULD do to that file, tried on a copy (the file itself is not touched)
if (args.Length == 2 && args[0] == "--dry-run")
{
    var copyDir = Path.Combine(Path.GetTempPath(), "sportify-refdb-dry-" + Guid.NewGuid().ToString("N").Substring(0, 8));
    Directory.CreateDirectory(copyDir);
    var copy = Path.Combine(copyDir, "reference.db");
    foreach (var suffix in new[] { "", "-wal", "-shm" }) if (File.Exists(args[1] + suffix)) File.Copy(args[1] + suffix, copy + suffix);
    var dry = ReferenceDbGuard.Prepare(copy);
    Console.WriteLine("would: " + dry.Action + (dry.Reason.Length > 0 ? " (" + dry.Reason + ")" : ""));
    if (dry.RowsNotInNewCatalog > 0) { Console.WriteLine(dry.RowsNotInNewCatalog + " row(s) of the current file are not in the new catalog:"); foreach (var l in File.ReadAllLines(dry.RowsReportPath!).Skip(3).Take(20)) Console.WriteLine("   " + l); }
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    try { Directory.Delete(copyDir, true); } catch (IOException) { }
    return 0;
}

var fails = 0;
void Check(string name, bool ok, string extra = "") { Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name} {extra}".TrimEnd()); if (!ok) fails++; }

var root = Path.Combine(Path.GetTempPath(), "sportify-refdb-" + Guid.NewGuid().ToString("N").Substring(0, 8));
Directory.CreateDirectory(root);
string NewDir(string name) { var d = Path.Combine(root, name); Directory.CreateDirectory(d); return d; }

SqliteConnection Open(string path, bool pooled = false) { var c = new SqliteConnection($"Data Source={path};Pooling={pooled}"); c.Open(); return c; }
long Scalar(string path, string sql) { using var c = Open(path); using var cmd = c.CreateCommand(); cmd.CommandText = sql; return Convert.ToInt64(cmd.ExecuteScalar() ?? 0); }
void Run(string path, string sql) { using var c = Open(path); using var cmd = c.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery(); }
string Text(string path, string sql) { using var c = Open(path); using var cmd = c.CreateCommand(); cmd.CommandText = sql; return Convert.ToString(cmd.ExecuteScalar()) ?? ""; }
string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
string[] Backups(string dir) => Directory.GetFiles(dir, "reference.*.backup.db");

// ---- 1. a new one, then a second start
{
    var dir = NewDir("fresh"); var db = Path.Combine(dir, "reference.db");
    var a = ReferenceDbGuard.Prepare(db);
    Check("no file: created and seeded", a.Action == "created" && File.Exists(db) && Scalar(db, "SELECT count(*) FROM Sports") > 0 && Scalar(db, "SELECT count(*) FROM RoofAssemblies") > 0);
    Check("the seed digest is recorded in the file", Scalar(db, "SELECT count(*) FROM __SportifyMeta WHERE key = 'seed_digest'") == 1);
    Check("it holds what the whole catalog sequence seeds: sport options, furniture, prices",
          Scalar(db, "SELECT count(*) FROM SportOptions") > 0 && Scalar(db, "SELECT count(*) FROM FurnitureItems") > 0 && Scalar(db, "SELECT count(*) FROM Materials WHERE PriceValue IS NOT NULL") > 0);
    var before = Hash(db);
    var b = ReferenceDbGuard.Prepare(db);
    Check("started again: nothing to do, the file is not touched", b.Action == "ok" && Hash(db) == before && Backups(dir).Length == 0);
}

// ---- 2. the incident: a column the code has and the file lacks
{
    var dir = NewDir("column"); var db = Path.Combine(dir, "reference.db");
    ReferenceDbGuard.Prepare(db);
    var plants = Scalar(db, "SELECT count(*) FROM Plants");
    Run(db, "ALTER TABLE Plants DROP COLUMN CrownM");
    var r = ReferenceDbGuard.Prepare(db);
    Check("a file without Plants.CrownM is rebuilt, and the report says which column", r.Action == "rebuilt" && r.Reason.Contains("Plants.CrownM"), r.Reason);
    Check("the new file has the column and every seeded plant", Scalar(db, "SELECT count(*) FROM pragma_table_info('Plants') WHERE name = 'CrownM'") == 1 && Scalar(db, "SELECT count(*) FROM Plants") == plants && plants > 0);
    Check("the old file is kept beside it, still without the column", r.BackupPath != null && File.Exists(r.BackupPath) && Backups(dir).Length == 1);
    SqliteConnection.ClearAllPools();
}

// ---- 3. a table the code has and the file lacks
{
    var dir = NewDir("table"); var db = Path.Combine(dir, "reference.db");
    ReferenceDbGuard.Prepare(db);
    Run(db, "DROP TABLE RoofAssemblyLayers"); Run(db, "DROP TABLE RoofAssemblies");
    var r = ReferenceDbGuard.Prepare(db);
    Check("a file without RoofAssemblies is rebuilt", r.Action == "rebuilt" && r.Reason.Contains("RoofAssemblies") && Scalar(db, "SELECT count(*) FROM RoofAssemblies") > 0, r.Reason);
}

// ---- 4. rows added through the Data tab are the user's: no rebuild because of them, and none lost when a rebuild comes
{
    var dir = NewDir("userrows"); var db = Path.Combine(dir, "reference.db");
    ReferenceDbGuard.Prepare(db);
    Run(db, "INSERT INTO Providers (Name, Country, Specialty, Website, Category) VALUES ('Test Provider AG', 'DE', 'Hand made', 'https://example.org', 'floor')");
    var same = ReferenceDbGuard.Prepare(db);
    Check("a row added by hand does not make the file 'out of date'", same.Action == "ok" && Scalar(db, "SELECT count(*) FROM Providers WHERE Name = 'Test Provider AG'") == 1, same.Reason);

    // as if the seed had changed since: the current catalog has a row of the base seed that this file lacks. That cannot be added in place (the base seeder only seeds an empty
    // database), so it is what a rebuild is for. A digest that merely differs is not enough: see the "in place" cases below.
    Run(db, "DELETE FROM Plants WHERE rowid = (SELECT min(rowid) FROM Plants)");
    Run(db, "UPDATE __SportifyMeta SET value = 'not the digest of this build' WHERE key = 'seed_digest'");
    var r = ReferenceDbGuard.Prepare(db);
    Check("when the seed has changed the file is rebuilt", r.Action == "rebuilt" && r.Reason.Contains("seed"), r.Reason);
    Check("the row added by hand is not in the new file", Scalar(db, "SELECT count(*) FROM Providers WHERE Name = 'Test Provider AG'") == 0);
    Check("it is in the backup, and named in the list of rows that are not in the new catalog",
          r.BackupPath != null && Scalar(r.BackupPath, "SELECT count(*) FROM Providers WHERE Name = 'Test Provider AG'") == 1 && r.RowsNotInNewCatalog == 1 && r.RowsReportPath != null && File.ReadAllText(r.RowsReportPath).Contains("Test Provider AG"));
}

// ---- 5. a file made before the guard (no digest)
{
    var dir = NewDir("legacy"); var db = Path.Combine(dir, "reference.db");
    ReferenceDbGuard.Prepare(db);
    Run(db, "DROP TABLE __SportifyMeta");
    var ok = ReferenceDbGuard.Prepare(db);
    Check("an older file that has everything is accepted and its digest recorded", ok.Action == "adopted" && Backups(dir).Length == 0 && Scalar(db, "SELECT count(*) FROM __SportifyMeta") == 1, ok.Reason);

    Run(db, "DROP TABLE __SportifyMeta");
    Run(db, "DELETE FROM Plants WHERE rowid = (SELECT min(rowid) FROM Plants)");
    var behind = ReferenceDbGuard.Prepare(db);
    Check("an older file that lacks a seed row is rebuilt", behind.Action == "rebuilt" && behind.Reason.Contains("seed rows"), behind.Reason);
}

// ---- 5b. what the catalog seeders add is added in place: an older file is brought up to date, and what was entered stays
{
    var dir = NewDir("inplace"); var db = Path.Combine(dir, "reference.db");
    ReferenceDbGuard.Prepare(db);
    var options = Scalar(db, "SELECT count(*) FROM SportOptions"); var furniture = Scalar(db, "SELECT count(*) FROM FurnitureItems");
    var priced = Scalar(db, "SELECT count(*) FROM Materials WHERE PriceValue IS NOT NULL");
    const string Finishes = "'timber_deck_pedestals', 'gravel_ballast', 'resin_bound_paving'";
    var finishes = Scalar(db, $"SELECT count(*) FROM RoofAssemblies WHERE Key IN ({Finishes})");
    var finishLayers = Scalar(db, $"SELECT count(*) FROM RoofAssemblyLayers WHERE RoofAssemblyId IN (SELECT Id FROM RoofAssemblies WHERE Key IN ({Finishes}))");
    var pricedLayers = Scalar(db, "SELECT count(*) FROM RoofAssemblyLayers WHERE PriceValue IS NOT NULL");
    Run(db, "INSERT INTO Providers (Name, Country, Specialty, Website, Category) VALUES ('Test Provider AG', 'DE', 'Hand made', 'https://example.org', 'floor')");

    // a file as it was before the sport options, the furniture, the roof finishes and the prices existed: no such tables, no price column anywhere, none of the finishes.
    // (The finishes matter: putting them back inserts layers, and an INSERT that names a price column the file does not have yet fails.)
    Run(db, "DROP TABLE SportOptions"); Run(db, "DROP TABLE FurnitureItems");
    Run(db, $"DELETE FROM RoofAssemblyLayers WHERE RoofAssemblyId IN (SELECT Id FROM RoofAssemblies WHERE Key IN ({Finishes}))");
    Run(db, $"DELETE FROM RoofAssemblies WHERE Key IN ({Finishes})");
    foreach (var table in new[] { "Materials", "Plants", "RoofAssemblyLayers" })
        foreach (var column in new[] { "PriceValue", "PriceUnit", "PriceSource", "PriceIsQuoted", "CostGroupDin276" }) Run(db, $"ALTER TABLE {table} DROP COLUMN {column}");
    var r = ReferenceDbGuard.Prepare(db);
    Check("a file that lacks only what the seeders add is not rebuilt: no backup, the guard says it is fine", r.Action != "rebuilt" && Backups(dir).Length == 0, r.Action + " " + r.Reason);
    Check("the tables, the columns and the rows they add are back",
          Scalar(db, "SELECT count(*) FROM SportOptions") == options && Scalar(db, "SELECT count(*) FROM FurnitureItems") == furniture
          && Scalar(db, "SELECT count(*) FROM pragma_table_info('Materials') WHERE name = 'PriceValue'") == 1
          && Scalar(db, "SELECT count(*) FROM Materials WHERE PriceValue IS NOT NULL") == priced && priced > 0
          && finishes == 3 && Scalar(db, $"SELECT count(*) FROM RoofAssemblies WHERE Key IN ({Finishes})") == finishes
          && Scalar(db, $"SELECT count(*) FROM RoofAssemblyLayers WHERE RoofAssemblyId IN (SELECT Id FROM RoofAssemblies WHERE Key IN ({Finishes}))") == finishLayers
          && Scalar(db, "SELECT count(*) FROM RoofAssemblyLayers WHERE PriceValue IS NOT NULL") == pricedLayers && pricedLayers > 0);
    Check("and the row that was entered by hand is still there", Scalar(db, "SELECT count(*) FROM Providers WHERE Name = 'Test Provider AG'") == 1);
    SqliteConnection.ClearAllPools();
}

// ---- 5c. the same for a file the build before them made: its recorded digest is the old one, and that alone is not a reason to rebuild
{
    var dir = NewDir("olddigest"); var db = Path.Combine(dir, "reference.db");
    ReferenceDbGuard.Prepare(db);
    var current = Text(db, "SELECT value FROM __SportifyMeta WHERE key = 'seed_digest'");
    Run(db, "INSERT INTO Providers (Name, Country, Specialty, Website, Category) VALUES ('Test Provider AG', 'DE', 'Hand made', 'https://example.org', 'floor')");
    Run(db, "DROP TABLE FurnitureItems");
    Run(db, "UPDATE __SportifyMeta SET value = 'the digest the build before the furniture catalogue wrote' WHERE key = 'seed_digest'");
    var r = ReferenceDbGuard.Prepare(db);
    Check("a differing digest is accepted when the file has every row of the current catalog once the seeders have run", r.Action == "adopted" && Backups(dir).Length == 0, r.Action + " " + r.Reason);
    Check("the digest is now this build's, and the hand-entered row is kept",
          Text(db, "SELECT value FROM __SportifyMeta WHERE key = 'seed_digest'") == current && Scalar(db, "SELECT count(*) FROM Providers WHERE Name = 'Test Provider AG'") == 1 && Scalar(db, "SELECT count(*) FROM FurnitureItems") > 0);
    SqliteConnection.ClearAllPools();
    var before = Hash(db);
    var again = ReferenceDbGuard.Prepare(db);
    Check("started again: nothing to do, the file is not touched", again.Action == "ok" && Hash(db) == before && Backups(dir).Length == 0, again.Action + " " + again.Reason);
}

// ---- 6. the other policies leave the file alone
{
    var dir = NewDir("policies"); var db = Path.Combine(dir, "reference.db");
    ReferenceDbGuard.Prepare(db);
    Run(db, "ALTER TABLE Plants DROP COLUMN CrownM");
    SqliteConnection.ClearAllPools();
    var before = Hash(db);
    Exception? thrown = null;
    try { ReferenceDbGuard.Prepare(db, null, DriftPolicy.Fail); } catch (InvalidOperationException ex) { thrown = ex; }
    Check("Fail: refuses to start and says what differs", thrown != null && thrown.Message.Contains("Plants.CrownM"));
    var ignored = ReferenceDbGuard.Prepare(db, null, DriftPolicy.Ignore);
    Check("Ignore: carries on with the file as it is", ignored.Action == "ignored");
    Check("neither touched the file or made a backup", Hash(db) == before && Backups(dir).Length == 0);
}

// ---- 7. the file is in use (another API running)
{
    var dir = NewDir("inuse"); var db = Path.Combine(dir, "reference.db");
    ReferenceDbGuard.Prepare(db);
    Run(db, "ALTER TABLE Plants DROP COLUMN CrownM");
    SqliteConnection.ClearAllPools();
    Exception? thrown = null;
    using (var held = Open(db))
    {
        using var cmd = held.CreateCommand(); cmd.CommandText = "SELECT count(*) FROM Plants"; cmd.ExecuteScalar();
        try { ReferenceDbGuard.Prepare(db); } catch (Exception ex) { thrown = ex; }
    }
    Check("a file another program holds open is not half-replaced: it says so", thrown is InvalidOperationException && thrown.Message.Contains("in use"), thrown?.Message ?? "no exception");
    SqliteConnection.ClearAllPools();
    var later = ReferenceDbGuard.Prepare(db);
    Check("and once it is free the rebuild goes through", later.Action == "rebuilt" && Scalar(db, "SELECT count(*) FROM pragma_table_info('Plants') WHERE name = 'CrownM'") == 1 && Backups(dir).Length == 1, later.Reason);
}

// ---- 8. something that is not a database
{
    var dir = NewDir("garbage"); var db = Path.Combine(dir, "reference.db");
    File.WriteAllText(db, "this is not a sqlite file at all, just text that is long enough to look like something");
    var r = ReferenceDbGuard.Prepare(db);
    Check("a file that is not a database is kept as a backup and replaced", r.Action == "rebuilt" && Backups(dir).Length == 1 && Scalar(db, "SELECT count(*) FROM Sports") > 0, r.Reason);
}

// ---- 9. the model the API uses reads the rebuilt file
{
    var dir = NewDir("ef"); var db = Path.Combine(dir, "reference.db");
    ReferenceDbGuard.Prepare(db);
    Run(db, "ALTER TABLE Plants DROP COLUMN CrownM");
    ReferenceDbGuard.Prepare(db);
    var options = new DbContextOptionsBuilder<ReferenceDbContext>().UseSqlite($"Data Source={db};Pooling=False").Options;
    using var ctx = new ReferenceDbContext(options);
    Check("the API's own context can query plants and roof build-ups from it (the two calls that failed with the old file)", ctx.Plants.Count() > 0 && ctx.RoofAssemblies.Include(a => a.Layers).Count() > 0);
}

SqliteConnection.ClearAllPools();
try { Directory.Delete(root, true); } catch (IOException) { /* a temp folder: leave it */ }
Console.WriteLine(fails == 0 ? "\nALL REFERENCE-DB CHECKS PASSED" : $"\n{fails} CHECK(S) FAILED");
return fails == 0 ? 0 : 1;
