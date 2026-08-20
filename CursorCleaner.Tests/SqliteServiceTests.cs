using CursorCleaner.Models;
using CursorCleaner.Services;
using Microsoft.Data.Sqlite;

namespace CursorCleaner.Tests;

[TestClass]
public sealed class SqliteServiceTests
{
    [TestMethod]
    public async Task Vacuum_ValidTemporaryDatabaseSucceedsAndCreatesBackup()
    {
        using var temp = new TemporaryDirectory();
        var root = Path.Combine(temp.Path, "approved");
        Directory.CreateDirectory(root);
        var database = Path.Combine(root, "test.db");
        await CreateDatabaseAsync(database);
        var service = CreateService(temp.Path);

        var result = await service.VacuumAsync(database, [root]);

        Assert.IsTrue(result.Succeeded, result.Error);
        Assert.IsNotNull(result.BackupPath);
        Assert.IsTrue(File.Exists(result.BackupPath));
        await AssertQuickCheckAsync(result.BackupPath);
        Assert.IsTrue(result.SizeAfter >= 0);
    }

    [TestMethod]
    public async Task Vacuum_CorruptDatabaseFailsWithoutMaintenance()
    {
        using var temp = new TemporaryDirectory();
        var root = Path.Combine(temp.Path, "approved");
        Directory.CreateDirectory(root);
        var database = Path.Combine(root, "corrupt.sqlite");
        await File.WriteAllTextAsync(database, "this is not a sqlite database");
        var original = await File.ReadAllBytesAsync(database);
        var service = CreateService(temp.Path);

        var result = await service.VacuumAsync(database, [root]);

        Assert.IsFalse(result.Succeeded);
        StringAssert.Contains(result.Error!, "damaged");
        Assert.IsNull(result.BackupPath);
        CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(database));
    }

    [TestMethod]
    public async Task Vacuum_OutsideApprovedRootIsRejected()
    {
        using var temp = new TemporaryDirectory();
        var approved = Path.Combine(temp.Path, "approved");
        Directory.CreateDirectory(approved);
        var database = Path.Combine(temp.Path, "outside.db");
        await CreateDatabaseAsync(database);
        var service = CreateService(temp.Path);

        var result = await service.VacuumAsync(database, [approved]);

        Assert.IsFalse(result.Succeeded);
        StringAssert.Contains(result.Error!, "outside");
    }

    [TestMethod]
    public async Task Vacuum_ExternalApprovedRootCannotExpandTrustedRoots()
    {
        using var temp = new TemporaryDirectory();
        var trusted = Path.Combine(temp.Path, "trusted");
        var external = Path.Combine(temp.Path, "external");
        Directory.CreateDirectory(trusted);
        Directory.CreateDirectory(external);
        var database = Path.Combine(external, "outside.db");
        await CreateDatabaseAsync(database);
        var service = CreateService(temp.Path, trusted);

        var result = await service.VacuumAsync(database, [external]);

        Assert.IsFalse(result.Succeeded);
        StringAssert.Contains(result.Error!, "trusted Cursor roots");
        Assert.IsNull(result.BackupPath);
        await AssertQuickCheckAsync(database);
    }

    private static SqliteService CreateService(string tempPath, string? trustedRoot = null)
    {
        var log = new LogService(Path.Combine(tempPath, "logs"));
        var root = trustedRoot ?? Path.Combine(tempPath, "approved");
        return new SqliteService(
            new NeverRunningProcessService(),
            new PathGuard([root]),
            new BackupService(log, Path.Combine(tempPath, "backups")),
            log);
    }

    private static async Task AssertQuickCheckAsync(string path)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        };
        await using var connection = new SqliteConnection(builder.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA quick_check;";
        Assert.AreEqual("ok", (string?)await command.ExecuteScalarAsync());
    }

    private static async Task CreateDatabaseAsync(string path)
    {
        var builder = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false };
        await using var connection = new SqliteConnection(builder.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE items (id INTEGER PRIMARY KEY, value TEXT);"
            + " INSERT INTO items(value) VALUES ('one'), ('two'), ('three');"
            + " DELETE FROM items WHERE id = 2;";
        await command.ExecuteNonQueryAsync();
    }

    private sealed class NeverRunningProcessService : IProcessService
    {
        public bool IsCursorRunning() => false;
    }
}
