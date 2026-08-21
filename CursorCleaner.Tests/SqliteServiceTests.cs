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
        Assert.AreEqual(BackupService.CurrentFileName, Path.GetFileName(result.BackupPath));
        await AssertQuickCheckAsync(result.BackupPath);
        Assert.IsTrue(result.SizeAfter >= 0);
    }

    [TestMethod]
    public async Task Vacuum_ReportsCheckBackupAndVacuumProgress()
    {
        using var temp = new TemporaryDirectory();
        var root = Path.Combine(temp.Path, "approved");
        Directory.CreateDirectory(root);
        var database = Path.Combine(root, "test.db");
        await CreateDatabaseAsync(database);
        var service = CreateService(temp.Path);
        var reports = new List<SqliteProgress>();

        var result = await service.VacuumAsync(database, [root], new Progress<SqliteProgress>(reports.Add));

        Assert.IsTrue(result.Succeeded, result.Error);
        CollectionAssert.Contains(reports.Select(item => item.Stage).ToArray(), SqliteProgressStage.Checking);
        CollectionAssert.Contains(reports.Select(item => item.Stage).ToArray(), SqliteProgressStage.BackingUp);
        CollectionAssert.Contains(reports.Select(item => item.Stage).ToArray(), SqliteProgressStage.Vacuuming);
        CollectionAssert.Contains(reports.Select(item => item.Stage).ToArray(), SqliteProgressStage.Completed);
        Assert.IsTrue(reports.Any(item => item.Stage == SqliteProgressStage.BackingUp && item.Percent == 100));
        StringAssert.Contains(reports.First(item => item.Stage == SqliteProgressStage.BackingUp && item.Percent == 100).Message, "正在在线备份 100%");
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

        var reports = new List<SqliteProgress>();
        var result = await service.DeleteChatRecordsAsync(
            [SqliteChatFixtures.KeepId],
            [database],
            [root],
            new Progress<SqliteProgress>(reports.Add));

        Assert.IsTrue(result.Succeeded, result.Error);
        Assert.IsFalse(result.Blocked);
        CollectionAssert.Contains(reports.Select(item => item.Stage).ToArray(), SqliteProgressStage.Checking);
        CollectionAssert.Contains(reports.Select(item => item.Stage).ToArray(), SqliteProgressStage.BackingUp);
        CollectionAssert.Contains(reports.Select(item => item.Stage).ToArray(), SqliteProgressStage.DeletingRows);
        CollectionAssert.Contains(reports.Select(item => item.Stage).ToArray(), SqliteProgressStage.Completed);
        Assert.IsTrue(result.DeletedRows > 0);
        Assert.IsNotNull(result.Databases[0].BackupPath);
        Assert.IsTrue(File.Exists(result.Databases[0].BackupPath));
        Assert.AreEqual(BackupService.CurrentFileName, Path.GetFileName(result.Databases[0].BackupPath));

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
    public async Task DeleteChatRecords_SecondDeleteReplacesRollingBackup()
    {
        using var temp = new TemporaryDirectory();
        var root = Path.Combine(temp.Path, "approved");
        Directory.CreateDirectory(root);
        var database = Path.Combine(root, "state.vscdb");
        await SqliteChatFixtures.CreateStateDatabaseAsync(database);
        var backupRoot = Path.Combine(temp.Path, "backups");
        var service = CreateService(temp.Path);

        var first = await service.DeleteChatRecordsAsync([SqliteChatFixtures.KeepId], [database], [root]);
        Assert.IsTrue(first.Succeeded, first.Error);
        var firstBackup = first.Databases[0].BackupPath!;
        Assert.AreEqual(BackupService.CurrentFileName, Path.GetFileName(firstBackup));
        var firstBytes = await File.ReadAllBytesAsync(firstBackup);

        var second = await service.DeleteChatRecordsAsync([SqliteChatFixtures.ExtraId], [database], [root]);
        Assert.IsTrue(second.Succeeded, second.Error);
        var secondBackup = second.Databases[0].BackupPath!;
        Assert.AreEqual(firstBackup, secondBackup);
        Assert.IsTrue(File.Exists(secondBackup));
        CollectionAssert.AreNotEqual(firstBytes, await File.ReadAllBytesAsync(secondBackup));
        Assert.AreEqual(1, Directory.GetFiles(Path.GetDirectoryName(secondBackup)!, "*", SearchOption.AllDirectories).Count(path => Path.GetFileName(path) == BackupService.CurrentFileName));
        Assert.IsFalse(Directory.GetFiles(Path.GetDirectoryName(secondBackup)!).Any(path => BackupService.IsStagingFileName(Path.GetFileName(path))));
        Assert.AreEqual(1, Directory.GetDirectories(Path.Combine(backupRoot, BackupService.SqliteFolderName)).Length);
    }

    [TestMethod]
    public async Task DeleteChatRecords_UnmatchedIdsSkipBackup()
    {
        using var temp = new TemporaryDirectory();
        var root = Path.Combine(temp.Path, "approved");
        Directory.CreateDirectory(root);
        var database = Path.Combine(root, "state.vscdb");
        await SqliteChatFixtures.CreateStateDatabaseAsync(database);
        var original = await File.ReadAllBytesAsync(database);
        var service = CreateService(temp.Path);
        var missingId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";

        var result = await service.DeleteChatRecordsAsync([missingId], [database], [root]);

        Assert.IsTrue(result.Succeeded, result.Error);
        Assert.AreEqual(0, result.DeletedRows);
        Assert.IsNull(result.Databases[0].BackupPath);
        CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(database));
        Assert.IsFalse(Directory.Exists(Path.Combine(temp.Path, "backups", BackupService.SqliteFolderName))
            && Directory.GetDirectories(Path.Combine(temp.Path, "backups", BackupService.SqliteFolderName)).Length > 0);
    }

    [TestMethod]
    public async Task DeleteChatRecords_CancelAfterFirstDatabaseKeepsPartialResults()
    {
        using var temp = new TemporaryDirectory();
        var firstRoot = Path.Combine(temp.Path, "approved", "cursor");
        var secondRoot = Path.Combine(temp.Path, "approved", "insiders");
        Directory.CreateDirectory(firstRoot);
        Directory.CreateDirectory(secondRoot);
        var firstDatabase = Path.Combine(firstRoot, "state.vscdb");
        var secondDatabase = Path.Combine(secondRoot, "state.vscdb");
        await SqliteChatFixtures.CreateStateDatabaseAsync(firstDatabase);
        await SqliteChatFixtures.CreateStateDatabaseAsync(secondDatabase);
        var log = new LogService(Path.Combine(temp.Path, "logs"));
        var service = new SqliteService(
            new NeverRunningProcessService(),
            new PathGuard([firstRoot, secondRoot]),
            new BackupService(log, Path.Combine(temp.Path, "backups")),
            log);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await service.DeleteChatRecordsAsync(
            [SqliteChatFixtures.KeepId],
            [firstDatabase, secondDatabase],
            [firstRoot, secondRoot],
            progress: null,
            cancellationToken: cts.Token);

        Assert.IsTrue(result.Cancelled);
        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(0, result.Databases.Count);
        Assert.IsTrue(File.Exists(firstDatabase));
    }

    [TestMethod]
    public async Task Vacuum_InsufficientSourceVolumeSpaceDoesNotWrite()
    {
        using var temp = new TemporaryDirectory();
        var root = Path.Combine(temp.Path, "approved");
        Directory.CreateDirectory(root);
        var database = Path.Combine(root, "test.db");
        await CreateDatabaseAsync(database);
        var original = await File.ReadAllBytesAsync(database);
        var log = new LogService(Path.Combine(temp.Path, "logs"));
        // Enough space only when probing backup root; source volume reports 0.
        var backupRoot = Path.Combine(temp.Path, "backups");
        var service = new SqliteService(
            new NeverRunningProcessService(),
            new PathGuard([root]),
            new BackupService(log, backupRoot, path =>
            {
                var normalized = Path.GetFullPath(path);
                return normalized.StartsWith(Path.GetFullPath(backupRoot), StringComparison.OrdinalIgnoreCase)
                    ? long.MaxValue
                    : 0;
            }),
            log);

        var result = await service.VacuumAsync(database, [root]);

        Assert.IsFalse(result.Succeeded);
        StringAssert.Contains(result.Error!, "空间不足");
        Assert.IsNotNull(result.SpaceFailure);
        Assert.AreEqual(SqliteSpaceFailureStage.InitialCheck, result.SpaceFailure!.Stage);
        Assert.IsFalse(result.SpaceFailure.BackupWasKept);
        Assert.IsNull(result.BackupPath);
        CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(database));
    }

    [TestMethod]
    public async Task Vacuum_SpaceDropsAfterBackupKeepsVerifiedBackupAndSkipsVacuum()
    {
        using var temp = new TemporaryDirectory();
        var root = Path.Combine(temp.Path, "approved");
        Directory.CreateDirectory(root);
        var database = Path.Combine(root, "test.db");
        await CreateDatabaseAsync(database);
        var original = await File.ReadAllBytesAsync(database);
        var log = new LogService(Path.Combine(temp.Path, "logs"));
        var backupRoot = Path.Combine(temp.Path, "backups");
        var sqliteRoot = Path.Combine(backupRoot, BackupService.SqliteFolderName);
        var simulateDropAfterBackup = true;
        var backup = new BackupService(log, backupRoot, _ =>
        {
            if (!simulateDropAfterBackup)
            {
                return long.MaxValue;
            }

            var currentExists = Directory.Exists(sqliteRoot)
                && Directory.EnumerateFiles(sqliteRoot, BackupService.CurrentFileName, SearchOption.AllDirectories).Any();
            return currentExists ? 0 : long.MaxValue;
        });
        var service = new SqliteService(
            new NeverRunningProcessService(),
            new PathGuard([root]),
            backup,
            log);

        var result = await service.VacuumAsync(database, [root]);

        Assert.IsFalse(result.Succeeded);
        Assert.IsNotNull(result.SpaceFailure);
        Assert.AreEqual(SqliteSpaceFailureStage.BeforeVacuum, result.SpaceFailure!.Stage);
        Assert.IsTrue(result.SpaceFailure.BackupWasKept);
        StringAssert.Contains(result.Error!, "已保留");
        Assert.IsNotNull(result.BackupPath);
        Assert.AreEqual(BackupService.CurrentFileName, Path.GetFileName(result.BackupPath));
        Assert.IsTrue(File.Exists(result.BackupPath));
        CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(database));
        await AssertQuickCheckAsync(database);

        Volatile.Write(ref simulateDropAfterBackup, false);
        var retry = await service.VacuumAsync(database, [root]);

        Assert.IsTrue(retry.Succeeded, retry.Error);
    }

    [TestMethod]
    public async Task DeleteChatRecords_UnknownSchemaSkipsWithoutBlocking()
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

        // Workspace state.vscdb files are not chat stores; skipping them must not
        // fail the batch or block session file deletion.
        Assert.IsTrue(result.Succeeded, result.Error);
        StringAssert.Contains(result.Databases[0].Error!, "not a Cursor chat store");
        Assert.AreEqual(0, result.DeletedRows);
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
    public async Task DeleteChatRecords_SameNameDatabasesKeepSeparateBackups()
    {
        using var temp = new TemporaryDirectory();
        var firstRoot = Path.Combine(temp.Path, "approved", "cursor");
        var secondRoot = Path.Combine(temp.Path, "approved", "insiders");
        Directory.CreateDirectory(firstRoot);
        Directory.CreateDirectory(secondRoot);
        var firstDatabase = Path.Combine(firstRoot, "state.vscdb");
        var secondDatabase = Path.Combine(secondRoot, "state.vscdb");
        await SqliteChatFixtures.CreateStateDatabaseAsync(firstDatabase);
        await SqliteChatFixtures.CreateStateDatabaseAsync(secondDatabase);
        var log = new LogService(Path.Combine(temp.Path, "logs"));
        var service = new SqliteService(
            new NeverRunningProcessService(),
            new PathGuard([firstRoot, secondRoot]),
            new BackupService(log, Path.Combine(temp.Path, "backups")),
            log);

        var result = await service.DeleteChatRecordsAsync(
            [SqliteChatFixtures.KeepId],
            [firstDatabase, secondDatabase],
            [firstRoot, secondRoot]);

        Assert.IsTrue(result.Succeeded, result.Error);
        Assert.AreEqual(2, result.Databases.Count);
        Assert.AreNotEqual(result.Databases[0].BackupPath, result.Databases[1].BackupPath);
        Assert.IsTrue(File.Exists(result.Databases[0].BackupPath));
        Assert.IsTrue(File.Exists(result.Databases[1].BackupPath));
    }

    [TestMethod]
    public async Task DeleteChatRecords_InsufficientSpaceDoesNotWrite()
    {
        using var temp = new TemporaryDirectory();
        var root = Path.Combine(temp.Path, "approved");
        Directory.CreateDirectory(root);
        var database = Path.Combine(root, "state.vscdb");
        await SqliteChatFixtures.CreateStateDatabaseAsync(database);
        var original = await File.ReadAllBytesAsync(database);
        var log = new LogService(Path.Combine(temp.Path, "logs"));
        var service = new SqliteService(
            new NeverRunningProcessService(),
            new PathGuard([root]),
            new BackupService(log, Path.Combine(temp.Path, "backups"), _ => 0),
            log);

        var result = await service.DeleteChatRecordsAsync([SqliteChatFixtures.KeepId], [database], [root]);

        Assert.IsFalse(result.Succeeded);
        StringAssert.Contains(result.Error!, "空间不足");
        Assert.IsNull(result.Databases[0].BackupPath);
        CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(database));
    }

    [TestMethod]
    public async Task DeleteChatRecords_NoMatchSkipsSpaceWrites()
    {
        using var temp = new TemporaryDirectory();
        var root = Path.Combine(temp.Path, "approved");
        Directory.CreateDirectory(root);
        var database = Path.Combine(root, "state.vscdb");
        await SqliteChatFixtures.CreateStateDatabaseAsync(database);
        var original = await File.ReadAllBytesAsync(database);
        var log = new LogService(Path.Combine(temp.Path, "logs"));
        var service = new SqliteService(
            new NeverRunningProcessService(),
            new PathGuard([root]),
            new BackupService(log, Path.Combine(temp.Path, "backups"), _ => 0),
            log);

        var result = await service.DeleteChatRecordsAsync(
            ["aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"],
            [database],
            [root]);

        Assert.IsTrue(result.Succeeded, result.Error);
        Assert.IsNull(result.Databases[0].BackupPath);
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

    [TestMethod]
    public async Task AnalyzeUsage_ChatDatabaseReportsPrefixesAndChatBytes()
    {
        using var temp = new TemporaryDirectory();
        var root = Path.Combine(temp.Path, "approved");
        Directory.CreateDirectory(root);
        var database = Path.Combine(root, "state.vscdb");
        await SqliteChatFixtures.CreateStateDatabaseAsync(database);
        var original = await File.ReadAllBytesAsync(database);
        var service = CreateService(temp.Path);

        var report = await service.AnalyzeUsageAsync(database, [root]);

        Assert.IsTrue(report.Succeeded, report.Error);
        Assert.IsTrue(report.IsChatStore);
        Assert.AreEqual(2, report.ConversationCount);
        Assert.IsTrue(report.ChatBytes > 0);
        Assert.IsTrue(report.FileBytes > 0);
        Assert.IsTrue(report.LogicalBytes >= 0);
        Assert.IsTrue(report.FreePagesBytes >= 0);
        CollectionAssert.Contains(report.Tables.Select(item => item.Name).ToArray(), "cursorDiskKV");
        CollectionAssert.Contains(report.KeyPrefixes.Select(item => item.Name).ToArray(), "bubbleId");
        CollectionAssert.Contains(report.KeyPrefixes.Select(item => item.Name).ToArray(), "checkpointId");
        CollectionAssert.Contains(report.TopItemTableKeys.Select(item => item.Name).ToArray(), "composer.composerData");
        CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(database));
    }

    [TestMethod]
    public async Task AnalyzeUsage_UnknownSchemaIsNotChatStore()
    {
        using var temp = new TemporaryDirectory();
        var root = Path.Combine(temp.Path, "approved");
        Directory.CreateDirectory(root);
        var database = Path.Combine(root, "unknown.db");
        await SqliteChatFixtures.CreateUnknownSchemaAsync(database);
        var original = await File.ReadAllBytesAsync(database);
        var service = CreateService(temp.Path);

        var report = await service.AnalyzeUsageAsync(database, [root]);

        Assert.IsTrue(report.Succeeded, report.Error);
        Assert.IsFalse(report.IsChatStore);
        Assert.AreEqual(0, report.ConversationCount);
        Assert.AreEqual(0, report.ChatBytes);
        CollectionAssert.Contains(report.Tables.Select(item => item.Name).ToArray(), "widgets");
        Assert.AreEqual(0, report.KeyPrefixes.Count);
        CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(database));
    }

    [TestMethod]
    public async Task AnalyzeUsage_OutsideApprovedRootReturnsError()
    {
        using var temp = new TemporaryDirectory();
        var approved = Path.Combine(temp.Path, "approved");
        Directory.CreateDirectory(approved);
        var database = Path.Combine(temp.Path, "outside.db");
        await CreateDatabaseAsync(database);
        var service = CreateService(temp.Path);

        var report = await service.AnalyzeUsageAsync(database, [approved]);

        Assert.IsFalse(report.Succeeded);
        StringAssert.Contains(report.Error!, "outside");
        Assert.AreEqual(0, report.Tables.Count);
    }

    [TestMethod]
    public async Task AnalyzeUsage_CancelledTokenDoesNotWrite()
    {
        using var temp = new TemporaryDirectory();
        var root = Path.Combine(temp.Path, "approved");
        Directory.CreateDirectory(root);
        var database = Path.Combine(root, "state.vscdb");
        await SqliteChatFixtures.CreateStateDatabaseAsync(database);
        var original = await File.ReadAllBytesAsync(database);
        var service = CreateService(temp.Path);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsExceptionAsync<OperationCanceledException>(() =>
            service.AnalyzeUsageAsync(database, [root], cancellation.Token));
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
        public Task<StopCursorResult> StopCursorAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new StopCursorResult(true, false, 0, null));
    }

    private sealed class AlwaysRunningProcessService : IProcessService
    {
        public bool IsCursorRunning() => true;
        public Task<StopCursorResult> StopCursorAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new StopCursorResult(false, true, 0, "still running"));
    }
}
