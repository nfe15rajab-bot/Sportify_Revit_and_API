using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Sportify.Api.Data;

// usage: ReferenceDbCheck                       the tests
//        ReferenceDbCheck --dry-run <reference.db>   what the guard would do to that file (on a copy)
// Runs ReferenceDbGuard (what Sportify.Api does at startup on reference.db) on throw-away database files: a fresh one, one made before the guard, one missing a column
// (the "no such column: p.CrownM" case), one with rows added through the Data tab, one held open by another program, and the three policies. Each must end with the
// old file kept somewhere when it was replaced, and left alone when it was not.
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
string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
string[] Backups(string dir) => Directory.GetFiles(dir, "reference.*.backup.db");

// ---- 1. a new one, then a second start
{
    var dir = NewDir("fresh"); var db = Path.Combine(dir, "reference.db");
    var a = ReferenceDbGuard.Prepare(db);
    Check("no file: created and seeded", a.Action == "created" && File.Exists(db) && Scalar(db, "SELECT count(*) FROM Sports") > 0 && Scalar(db, "SELECT count(*) FROM RoofAssemblies") > 0);
    Check("the seed digest is recorded in the file", Scalar(db, "SELECT count(*) FROM __SportifyMeta WHERE key = 'seed_digest'") == 1);
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

    Run(db, "UPDATE __SportifyMeta SET value = 'not the digest of this build' WHERE key = 'seed_digest'");     // as if the seed had changed since
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
