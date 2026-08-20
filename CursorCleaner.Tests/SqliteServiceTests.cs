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

    [TestMethod]
    public async Task DeleteChatRecords_RemovesSelectedComposerAndKeepsOthers()
    {
        using var temp = new TemporaryDirectory();
        var root = Path.Combine(temp.Path, "approved");
        Directory.CreateDirectory(root);
        var database = Path.Combine(root, "state.vscdb");
        await SqliteChatFixtures.CreateStateDatabaseAsync(database);
        var service = CreateService(temp.Path);

        var result = await service.DeleteChatRecordsAsync(
            [SqliteChatFixtures.KeepId],
            [database],
            [root]);

        Assert.IsTrue(result.Succeeded, result.Error);
        Assert.IsFalse(result.Blocked);
        Assert.IsTrue(result.DeletedRows > 0);
        Assert.IsNotNull(result.Databases[0].BackupPath);
        Assert.IsTrue(File.Exists(result.Databases[0].BackupPath));

        await using var connection = Open(database);
        Assert.AreEqual(0L, await CountAsync(connection, $"SELECT COUNT(*) FROM composerHeaders WHERE composerId = '{SqliteChatFixtures.KeepId}';"));
        Assert.AreEqual(1L, await CountAsync(connection, $"SELECT COUNT(*) FROM composerHeaders WHERE composerId = '{SqliteChatFixtures.ExtraId}';"));
        Assert.AreEqual(1L, await CountAsync(connection, "SELECT COUNT(*) FROM composerHeaders WHERE composerId = 'empty-state-draft';"));
        Assert.AreEqual(0L, await CountAsync(connection, $"SELECT COUNT(*) FROM cursorDiskKV WHERE key = 'composerData:{SqliteChatFixtures.KeepId}';"));
        Assert.AreEqual(1L, await CountAsync(connection, $"SELECT COUNT(*) FROM cursorDiskKV WHERE key = 'composerData:{SqliteChatFixtures.ExtraId}';"));
        Assert.AreEqual(0L, await CountAsync(connection, $"SELECT COUNT(*) FROM cursorDiskKV WHERE key LIKE 'bubbleId:{SqliteChatFixtures.KeepId}:%';"));
        Assert.AreEqual(1L, await CountAsync(connection, $"SELECT COUNT(*) FROM cursorDiskKV WHERE key LIKE 'bubbleId:{SqliteChatFixtures.ExtraId}:%';"));
        Assert.AreEqual(1L, await CountAsync(connection, "SELECT COUNT(*) FROM cursorDiskKV WHERE key = 'agentKv:blob:keep';"));
        Assert.AreEqual(1L, await CountAsync(connection, "SELECT COUNT(*) FROM ItemTable WHERE key = 'workbench.view.search.state';"));
        var composerData = (string?)await ScalarAsync(connection, "SELECT value FROM ItemTable WHERE key = 'composer.composerData';");
        StringAssert.Contains(composerData!, SqliteChatFixtures.ExtraId);
        Assert.IsFalse(composerData!.Contains(SqliteChatFixtures.KeepId, StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task DeleteChatRecords_RemovesConversationSearchRows()
    {
        using var temp = new TemporaryDirectory();
        var root = Path.Combine(temp.Path, "approved");
        Directory.CreateDirectory(root);
        var database = Path.Combine(root, "conversation-search.db");
        await SqliteChatFixtures.CreateSearchDatabaseAsync(database);
        var service = CreateService(temp.Path);

        var result = await service.DeleteChatRecordsAsync(
            [SqliteChatFixtures.KeepId],
            [database],
            [root]);

        Assert.IsTrue(result.Succeeded, result.Error);
        await using var connection = Open(database);
        Assert.AreEqual(0L, await CountAsync(connection, $"SELECT COUNT(*) FROM conversations WHERE id = '{SqliteChatFixtures.KeepId}';"));
        Assert.AreEqual(1L, await CountAsync(connection, $"SELECT COUNT(*) FROM conversations WHERE id = '{SqliteChatFixtures.ExtraId}';"));
        Assert.AreEqual(0L, await CountAsync(connection, $"SELECT COUNT(*) FROM conversation_search_candidates WHERE id = '{SqliteChatFixtures.KeepId}';"));
        Assert.AreEqual(0L, await CountAsync(connection, "SELECT COUNT(*) FROM conversation_fts WHERE title = 'Greeting conversation';"));
        Assert.AreEqual(1L, await CountAsync(connection, "SELECT COUNT(*) FROM conversation_fts WHERE title = 'Capabilities inquiry';"));
    }

    [TestMethod]
    public async Task DeleteChatRecords_UnknownSchemaDoesNotWrite()
    {
        using var temp = new TemporaryDirectory();
        var root = Path.Combine(temp.Path, "approved");
        Directory.CreateDirectory(root);
        var database = Path.Combine(root, "unknown.db");
        await SqliteChatFixtures.CreateUnknownSchemaAsync(database);
        var original = await File.ReadAllBytesAsync(database);
        var service = CreateService(temp.Path);

        var result = await service.DeleteChatRecordsAsync(
            [SqliteChatFixtures.KeepId],
            [database],
            [root]);

        Assert.IsFalse(result.Succeeded);
        StringAssert.Contains(result.Error!, "recognized");
        CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(database));
        Assert.IsNull(result.Databases[0].BackupPath);
    }

    [TestMethod]
    public async Task DeleteChatRecords_CursorRunningDoesNotWrite()
    {
        using var temp = new TemporaryDirectory();
        var root = Path.Combine(temp.Path, "approved");
        Directory.CreateDirectory(root);
        var database = Path.Combine(root, "state.vscdb");
        await SqliteChatFixtures.CreateStateDatabaseAsync(database);
        var original = await File.ReadAllBytesAsync(database);
        var log = new LogService(Path.Combine(temp.Path, "logs"));
        var service = new SqliteService(
            new AlwaysRunningProcessService(),
            new PathGuard([root]),
            new BackupService(log, Path.Combine(temp.Path, "backups")),
            log);

        var result = await service.DeleteChatRecordsAsync(
            [SqliteChatFixtures.KeepId],
            [database],
            [root]);

        Assert.IsFalse(result.Succeeded);
        Assert.IsTrue(result.Blocked);
        CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(database));
    }

    [TestMethod]
    public async Task DeleteChatRecords_NonUuidIdsDoNotWrite()
    {
        using var temp = new TemporaryDirectory();
        var root = Path.Combine(temp.Path, "approved");
        Directory.CreateDirectory(root);
        var database = Path.Combine(root, "state.vscdb");
        await SqliteChatFixtures.CreateStateDatabaseAsync(database);
        var original = await File.ReadAllBytesAsync(database);
        var service = CreateService(temp.Path);

        var result = await service.DeleteChatRecordsAsync(
            ["one", "empty-state-draft"],
            [database],
            [root]);

        Assert.IsTrue(result.Succeeded);
        StringAssert.Contains(result.Error!, "没有可匹配的会话 ID");
        CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(database));
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

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }

    private static async Task<object?> ScalarAsync(SqliteConnection connection, string sql, string? id = null)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        if (id is not null)
        {
            command.Parameters.AddWithValue("@id", id);
        }

        return await command.ExecuteScalarAsync();
    }

    private static async Task<long> CountAsync(SqliteConnection connection, string sql, string? id = null) =>
        Convert.ToInt64(await ScalarAsync(connection, sql, id));

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

    private sealed class AlwaysRunningProcessService : IProcessService
    {
        public bool IsCursorRunning() => true;
    }
}
