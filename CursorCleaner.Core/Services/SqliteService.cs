using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using CursorCleaner.Helpers;
using CursorCleaner.Models;
using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace CursorCleaner.Services;

public sealed class SqliteService : ISqliteService
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> DatabaseLocks = new(StringComparer.Ordinal);
    private static readonly HashSet<string> ChatKeyPrefixes = new(StringComparer.Ordinal)
    {
        "composerData",
        "bubbleId",
        "checkpointId",
        "composerChat"
    };

    private readonly IProcessService _processService;
    private readonly IPathGuard _pathGuard;
    private readonly IBackupService _backupService;
    private readonly ILogService _log;

    public SqliteService(IProcessService processService, IPathGuard pathGuard, IBackupService backupService, ILogService log)
    {
        _processService = processService;
        _pathGuard = pathGuard;
        _backupService = backupService;
        _log = log;
    }

    public async Task<SqliteMaintenanceResult> VacuumAsync(
        string databasePath,
        IEnumerable<string> approvedRoots,
        IProgress<SqliteProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(approvedRoots);
        var roots = approvedRoots.Select(PathSafety.Normalize).Distinct(PathSafety.PathComparer).ToArray();
        var guard = _pathGuard.ValidateSqliteTarget(databasePath, roots);
        if (!guard.IsSafe)
        {
            return Failure(databasePath, 0, null, guard.Error ?? "Database path validation failed.");
        }

        var path = guard.NormalizedPath!;
        var gate = await AcquireDatabaseLockAsync(path, cancellationToken).ConfigureAwait(false);
        try
        {
            return await VacuumCoreAsync(path, roots, progress, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<SqliteMaintenanceResult> VacuumCoreAsync(
        string path,
        IReadOnlyList<string> roots,
        IProgress<SqliteProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!_pathGuard.TryGetFileIdentity(path, out var initialIdentity, out var identityError))
        {
            return Failure(path, 0, null, identityError ?? "Database identity verification failed.");
        }

        if (_processService.IsCursorRunning())
        {
            if (!TryGetCombinedSize(path, out var blockedSize, out var blockedSizeError))
            {
                return Failure(path, 0, null, $"Cursor is running and the database size could not be read: {blockedSizeError}");
            }

            return Failure(path, blockedSize, null, "Cursor is running; database maintenance is blocked.");
        }

        if (!TryGetCombinedSize(path, out var sizeBefore, out var sizeError))
        {
            return Failure(path, 0, null, $"Database size could not be read: {sizeError}");
        }

        string? reservedBackupPath = null;
        string? verifiedBackupPath = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Report(progress, SqliteProgressStage.CheckingSpace, path);
            var spacePlan = _backupService.CreateSqliteSpacePlan(path, includeVacuum: true);
            var initialSpaceFailure = _backupService.CheckSqliteSpace(spacePlan, SqliteSpaceFailureStage.InitialCheck);
            if (initialSpaceFailure is not null)
            {
                return SpaceFailure(path, sizeBefore, null, initialSpaceFailure);
            }

            await using var connection = new SqliteConnection(BuildConnectionString(path, SqliteOpenMode.ReadWrite));
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            var guard = _pathGuard.ValidateSqliteTarget(path, roots);
            if (!guard.IsSafe || !IdentityMatches(path, initialIdentity, out identityError))
            {
                return Failure(path, sizeBefore, null, guard.Error ?? identityError ?? "Database changed while opening the write connection.");
            }

            if (_processService.IsCursorRunning())
            {
                return Failure(path, sizeBefore, null, "Cursor started before database maintenance; no backup, checkpoint, or VACUUM was performed.");
            }

            Report(progress, SqliteProgressStage.Checking, path);
            await RunQuickCheckAsync(connection, cancellationToken).ConfigureAwait(false);

            Report(progress, SqliteProgressStage.PreparingBackup, path);
            reservedBackupPath = await _backupService.CreateSqliteBackupPathAsync(path, cancellationToken).ConfigureAwait(false);
            verifiedBackupPath = await CreateVerifiedBackupAsync(connection, reservedBackupPath, progress, path, cancellationToken).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            guard = _pathGuard.ValidateSqliteTarget(path, roots);
            if (!guard.IsSafe || !IdentityMatches(path, initialIdentity, out identityError))
            {
                return Failure(path, sizeBefore, verifiedBackupPath, guard.Error ?? identityError ?? "Database changed before checkpoint.");
            }

            if (_processService.IsCursorRunning())
            {
                return Failure(path, sizeBefore, verifiedBackupPath, "Cursor started before checkpoint; the verified backup was kept and no write was started.");
            }

            Report(progress, SqliteProgressStage.CheckingSpace, path);
            var afterBackupFailure = _backupService.CheckVacuumSpace(path);
            if (afterBackupFailure is not null)
            {
                return SpaceFailure(path, sizeBefore, verifiedBackupPath, afterBackupFailure);
            }

            Report(progress, SqliteProgressStage.Checkpoint, path);
            await RunCheckpointAsync(connection, cancellationToken).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            guard = _pathGuard.ValidateSqliteTarget(path, roots);
            if (!guard.IsSafe || !IdentityMatches(path, initialIdentity, out identityError))
            {
                return Failure(path, sizeBefore, verifiedBackupPath, guard.Error ?? identityError ?? "Database changed before VACUUM.");
            }

            if (_processService.IsCursorRunning())
            {
                return Failure(path, sizeBefore, verifiedBackupPath, "Cursor started before VACUUM; the verified backup was kept and VACUUM was not started.");
            }

            Report(progress, SqliteProgressStage.CheckingSpace, path);
            var beforeVacuumFailure = _backupService.CheckVacuumSpace(path);
            if (beforeVacuumFailure is not null)
            {
                return SpaceFailure(path, sizeBefore, verifiedBackupPath, beforeVacuumFailure);
            }

            Report(progress, SqliteProgressStage.Vacuuming, path);
            await using var vacuum = connection.CreateCommand();
            vacuum.CommandText = "VACUUM;";
            await vacuum.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);

            Report(progress, SqliteProgressStage.VerifyingResult, path);
            await RunQuickCheckAsync(connection, CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (verifiedBackupPath is null)
            {
                TryDeleteIncompleteBackup(reservedBackupPath);
            }

            return Failure(path, sizeBefore, verifiedBackupPath, "Database maintenance was cancelled before VACUUM started. VACUUM itself is intentionally non-cancellable once started.");
        }
        catch (InvalidDataException ex)
        {
            if (verifiedBackupPath is null)
            {
                TryDeleteIncompleteBackup(reservedBackupPath);
            }

            await TryLogAsync("error", "sqlite.quick_check", "Database integrity check failed.", path, ex).ConfigureAwait(false);
            return Failure(path, sizeBefore, verifiedBackupPath, $"Database or online backup failed quick_check: {ex.Message}");
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            if (verifiedBackupPath is null)
            {
                TryDeleteIncompleteBackup(reservedBackupPath);
            }

            await TryLogAsync("error", "sqlite.maintenance", "Database maintenance failed.", path, ex).ConfigureAwait(false);
            var classification = ex is SqliteException sqliteException
                ? sqliteException.SqliteErrorCode switch
                {
                    13 => "磁盘空间不足，SQLite 无法完成数据库维护",
                    26 => "Database is damaged or is not a SQLite database",
                    _ => "Database is locked or maintenance failed"
                }
                : "Database is locked or maintenance failed";
            var spaceFailure = ex is SqliteException { SqliteErrorCode: 13 }
                ? CreateDiskFullFailure(path, verifiedBackupPath is not null)
                : null;
            return new SqliteMaintenanceResult(
                path,
                false,
                sizeBefore,
                sizeBefore,
                verifiedBackupPath,
                $"{classification}: {ex.Message}",
                spaceFailure);
        }

        if (!TryGetCombinedSize(path, out var sizeAfter, out sizeError))
        {
            return new SqliteMaintenanceResult(path, false, sizeBefore, sizeBefore, verifiedBackupPath,
                $"VACUUM completed, but the resulting database size could not be read: {sizeError}");
        }

        await TryLogAsync("info", "sqlite.vacuum", $"Database maintenance completed; size changed from {sizeBefore} to {sizeAfter} bytes.", path, null).ConfigureAwait(false);
        Report(progress, SqliteProgressStage.Completed, path, percent: 100);
        return new SqliteMaintenanceResult(path, true, sizeBefore, sizeAfter, verifiedBackupPath, null);
    }

    public async Task<SqliteChatCleanupResult> DeleteChatRecordsAsync(
        IEnumerable<string> conversationIds,
        IEnumerable<string> databasePaths,
        IEnumerable<string> approvedRoots,
        IProgress<SqliteProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conversationIds);
        ArgumentNullException.ThrowIfNull(databasePaths);
        ArgumentNullException.ThrowIfNull(approvedRoots);

        var ids = conversationIds
            .Where(SqliteConversationId.IsMatch)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var roots = approvedRoots.Select(PathSafety.Normalize).Distinct(PathSafety.PathComparer).ToArray();
        var paths = databasePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(PathSafety.Normalize)
            .Distinct(PathSafety.PathComparer)
            .ToArray();

        if (ids.Length == 0)
        {
            return new SqliteChatCleanupResult(true, false, [], "没有可匹配的会话 ID，未修改 SQLite。");
        }

        if (paths.Length == 0)
        {
            return new SqliteChatCleanupResult(true, false, [], "没有可处理的聊天数据库，未修改 SQLite。");
        }

        if (_processService.IsCursorRunning())
        {
            return new SqliteChatCleanupResult(false, true, [], "Cursor is running; chat record deletion is blocked.");
        }

        var results = new List<SqliteChatDatabaseResult>();
        var cancelled = false;
        foreach (var databasePath in paths)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                cancelled = true;
                break;
            }

            if (_processService.IsCursorRunning())
            {
                results.Add(new SqliteChatDatabaseResult(databasePath, false, 0, null, "Cursor started; remaining databases were not modified."));
                continue;
            }

            try
            {
                results.Add(await DeleteFromDatabaseAsync(databasePath, ids, roots, progress, results.Count + 1, paths.Length, cancellationToken).ConfigureAwait(false));
            }
            catch (OperationCanceledException)
            {
                // Lock acquisition or outer cancel must not drop already-completed database results.
                cancelled = true;
                break;
            }
        }

        var anySuccess = results.Any(item => item.Succeeded);
        var blocked = results.Any(item => item.Error?.Contains("Cursor", StringComparison.OrdinalIgnoreCase) == true && !item.Succeeded)
                      && !anySuccess;
        var succeeded = !cancelled && results.Count > 0 && results.All(item => item.Succeeded);
        string? error;
        if (succeeded)
        {
            error = null;
        }
        else if (cancelled)
        {
            error = results.Count == 0
                ? "Chat record deletion was cancelled before any database was processed."
                : "Chat record deletion was cancelled; completed databases were kept and remaining databases were not modified.";
        }
        else
        {
            error = results.Where(item => !item.Succeeded).Select(item => item.Error).FirstOrDefault() ?? "SQLite chat deletion failed.";
        }

        return new SqliteChatCleanupResult(succeeded, blocked, results, error, cancelled);
    }

    private async Task<SqliteChatDatabaseResult> DeleteFromDatabaseAsync(
        string databasePath,
        IReadOnlyList<string> ids,
        IReadOnlyList<string> roots,
        IProgress<SqliteProgress>? progress,
        int databaseIndex,
        int databaseCount,
        CancellationToken cancellationToken)
    {
        var guard = _pathGuard.ValidateSqliteTarget(databasePath, roots);
        if (!guard.IsSafe)
        {
            return new SqliteChatDatabaseResult(databasePath, false, 0, null, guard.Error ?? "Database path validation failed.");
        }

        var path = guard.NormalizedPath!;
        var gate = await AcquireDatabaseLockAsync(path, cancellationToken).ConfigureAwait(false);
        try
        {
            return await DeleteFromDatabaseCoreAsync(path, ids, roots, progress, databaseIndex, databaseCount, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<SqliteChatDatabaseResult> DeleteFromDatabaseCoreAsync(
        string path,
        IReadOnlyList<string> ids,
        IReadOnlyList<string> roots,
        IProgress<SqliteProgress>? progress,
        int databaseIndex,
        int databaseCount,
        CancellationToken cancellationToken)
    {
        if (!_pathGuard.TryGetFileIdentity(path, out var initialIdentity, out var identityError))
        {
            return new SqliteChatDatabaseResult(path, false, 0, null, identityError ?? "Database identity verification failed.");
        }

        if (_processService.IsCursorRunning())
        {
            return new SqliteChatDatabaseResult(path, false, 0, null, "Cursor is running; chat record deletion is blocked.");
        }

        string? reservedBackupPath = null;
        string? verifiedBackupPath = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var connection = new SqliteConnection(BuildConnectionString(path, SqliteOpenMode.ReadWrite));
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            var guard = _pathGuard.ValidateSqliteTarget(path, roots);
            if (!guard.IsSafe || !IdentityMatches(path, initialIdentity, out identityError))
            {
                return new SqliteChatDatabaseResult(path, false, 0, null, guard.Error ?? identityError ?? "Database changed while opening the write connection.");
            }

            if (_processService.IsCursorRunning())
            {
                return new SqliteChatDatabaseResult(path, false, 0, null, "Cursor started before chat deletion; no backup or write was performed.");
            }

            Report(progress, SqliteProgressStage.Checking, path, databaseIndex, databaseCount);
            await RunQuickCheckAsync(connection, cancellationToken).ConfigureAwait(false);

            var shape = await CursorChatSchema.DiscoverAsync(connection, cancellationToken).ConfigureAwait(false);
            if (!shape.IsRecognized)
            {
                // Workspace state.vscdb files only hold ItemTable; they are not chat
                // stores, so an unrecognized schema is a skip, not a failure.
                Report(progress, SqliteProgressStage.Completed, path, databaseIndex, databaseCount, 100);
                return new SqliteChatDatabaseResult(path, true, 0, null, "Skipped: not a Cursor chat store; no rows were deleted.");
            }

            if (!await CursorChatSchema.HasChatDataAsync(connection, shape, cancellationToken).ConfigureAwait(false))
            {
                Report(progress, SqliteProgressStage.Completed, path, databaseIndex, databaseCount, 100);
                return new SqliteChatDatabaseResult(path, true, 0, null, "Skipped: no Cursor chat records were found.");
            }

            if (!await CursorChatSchema.HasMatchingConversationAsync(connection, shape, ids, cancellationToken).ConfigureAwait(false))
            {
                Report(progress, SqliteProgressStage.Completed, path, databaseIndex, databaseCount, 100);
                return new SqliteChatDatabaseResult(path, true, 0, null, "No matching chat rows were found.");
            }

            Report(progress, SqliteProgressStage.CheckingSpace, path, databaseIndex, databaseCount);
            var spacePlan = _backupService.CreateSqliteSpacePlan(path, includeVacuum: false);
            var spaceFailure = _backupService.CheckSqliteSpace(spacePlan, SqliteSpaceFailureStage.BackupCheck);
            if (spaceFailure is not null)
            {
                return new SqliteChatDatabaseResult(path, false, 0, null, FormatSpaceFailure(spaceFailure));
            }

            Report(progress, SqliteProgressStage.PreparingBackup, path, databaseIndex, databaseCount);
            reservedBackupPath = await _backupService.CreateSqliteBackupPathAsync(path, cancellationToken).ConfigureAwait(false);
            verifiedBackupPath = await CreateVerifiedBackupAsync(connection, reservedBackupPath, progress, path, cancellationToken, databaseIndex, databaseCount).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            guard = _pathGuard.ValidateSqliteTarget(path, roots);
            if (!guard.IsSafe || !IdentityMatches(path, initialIdentity, out identityError))
            {
                return new SqliteChatDatabaseResult(path, false, 0, verifiedBackupPath, guard.Error ?? identityError ?? "Database changed before chat deletion.");
            }

            if (_processService.IsCursorRunning())
            {
                return new SqliteChatDatabaseResult(path, false, 0, verifiedBackupPath, "Cursor started before chat deletion; the verified backup was kept and no write was started.");
            }

            Report(progress, SqliteProgressStage.DeletingRows, path, databaseIndex, databaseCount);
            var deleted = await CursorChatSchema.DeleteAsync(connection, shape, ids, cancellationToken).ConfigureAwait(false);
            await TryLogAsync("info", "sqlite.chat.delete", $"Deleted {deleted} chat rows for {ids.Count} conversation IDs.", path, null).ConfigureAwait(false);
            Report(progress, SqliteProgressStage.Completed, path, databaseIndex, databaseCount, 100);
            return new SqliteChatDatabaseResult(path, true, deleted, verifiedBackupPath, deleted == 0 ? "No matching chat rows were found." : null);
        }
        catch (OperationCanceledException)
        {
            if (verifiedBackupPath is null)
            {
                TryDeleteIncompleteBackup(reservedBackupPath);
            }

            return new SqliteChatDatabaseResult(path, false, 0, verifiedBackupPath, "Chat record deletion was cancelled before writes completed.");
        }
        catch (InvalidDataException ex)
        {
            if (verifiedBackupPath is null)
            {
                TryDeleteIncompleteBackup(reservedBackupPath);
            }

            await TryLogAsync("error", "sqlite.chat.quick_check", "Database integrity check failed.", path, ex).ConfigureAwait(false);
            return new SqliteChatDatabaseResult(path, false, 0, verifiedBackupPath, $"Database or online backup failed quick_check: {ex.Message}");
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or JsonException)
        {
            if (verifiedBackupPath is null)
            {
                TryDeleteIncompleteBackup(reservedBackupPath);
            }

            await TryLogAsync("error", "sqlite.chat.delete", "Chat record deletion failed.", path, ex).ConfigureAwait(false);
            var classification = ex is SqliteException sqliteException && sqliteException.SqliteErrorCode == 26
                ? "Database is damaged or is not a SQLite database"
                : "Database is locked or chat deletion failed";
            return new SqliteChatDatabaseResult(path, false, 0, verifiedBackupPath, $"{classification}: {ex.Message}");
        }
    }

    public async Task<SqliteUsageReport> AnalyzeUsageAsync(
        string databasePath,
        IEnumerable<string> approvedRoots,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(approvedRoots);
        var roots = approvedRoots.Select(PathSafety.Normalize).Distinct(PathSafety.PathComparer).ToArray();
        var guard = _pathGuard.ValidateSqliteTarget(databasePath, roots);
        if (!guard.IsSafe)
        {
            return new SqliteUsageReport(databasePath, 0, 0, 0, 0, false, 0, 0, [], [], [], guard.Error ?? "Database path validation failed.");
        }

        var path = guard.NormalizedPath!;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var connection = new SqliteConnection(BuildConnectionString(path, SqliteOpenMode.ReadOnly));
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            var shape = await CursorChatSchema.DiscoverAsync(connection, cancellationToken).ConfigureAwait(false);
            var tables = await CollectTableUsageAsync(connection, cancellationToken).ConfigureAwait(false);
            var prefixes = shape.CursorDiskKv
                ? await CollectKeyPrefixesAsync(connection, cancellationToken).ConfigureAwait(false)
                : [];
            var topKeys = shape.ItemTable
                ? await CollectTopKeysAsync(connection, cancellationToken).ConfigureAwait(false)
                : [];

            var isChatStore = await CursorChatSchema.HasChatDataAsync(connection, shape, cancellationToken).ConfigureAwait(false);
            var chatBytes = await CollectChatBytesAsync(connection, shape, tables, prefixes, cancellationToken).ConfigureAwait(false);
            var conversationCount = await CountConversationsAsync(connection, shape, cancellationToken).ConfigureAwait(false);

            var fileBytes = new FileInfo(path).Length;
            var walBytes = File.Exists(path + "-wal") ? new FileInfo(path + "-wal").Length : 0;
            var (logicalBytes, freeBytes) = await GetPageStatsAsync(connection, cancellationToken).ConfigureAwait(false);

            return new SqliteUsageReport(
                path,
                fileBytes,
                walBytes,
                logicalBytes,
                freeBytes,
                isChatStore,
                conversationCount,
                chatBytes,
                tables,
                prefixes,
                topKeys,
                null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            await TryLogAsync("error", "sqlite.usage", "Database usage analysis failed.", path, ex).ConfigureAwait(false);
            return new SqliteUsageReport(path, 0, 0, 0, 0, false, 0, 0, [], [], [], ex.Message);
        }
    }

    private static async Task<IReadOnlyList<SqliteUsageEntry>> CollectTableUsageAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var tableNames = new List<string>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name;";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                tableNames.Add(reader.GetString(0));
            }
        }

        var entries = new List<SqliteUsageEntry>(tableNames.Count);
        foreach (var table in tableNames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var columns = new List<string>();
            await using (var info = connection.CreateCommand())
            {
                info.CommandText = $"PRAGMA table_info('{table.Replace("'", "''")}');";
                await using var reader = await info.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    var type = reader.GetString(2);
                    if (type.Contains("TEXT", StringComparison.OrdinalIgnoreCase) ||
                        type.Contains("CHAR", StringComparison.OrdinalIgnoreCase) ||
                        type.Contains("BLOB", StringComparison.OrdinalIgnoreCase))
                    {
                        columns.Add(reader.GetString(1));
                    }
                }
            }

            var quotedTable = QuoteIdentifier(table);
            long rowCount;
            long totalBytes;
            if (columns.Count == 0)
            {
                rowCount = await ScalarCountAsync(connection, $"SELECT COUNT(*) FROM {quotedTable};", cancellationToken).ConfigureAwait(false);
                totalBytes = 0;
            }
            else
            {
                var sums = string.Join("+", columns.Take(8).Select(column => $"COALESCE(LENGTH({QuoteIdentifier(column)}), 0)"));
                await using var stats = connection.CreateCommand();
                stats.CommandText = $"SELECT COUNT(*), COALESCE(SUM({sums}), 0) FROM {quotedTable};";
                await using var statsReader = await stats.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (!await statsReader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    continue;
                }

                rowCount = statsReader.GetInt64(0);
                totalBytes = statsReader.GetInt64(1);
            }

            entries.Add(new SqliteUsageEntry(table, rowCount, totalBytes));
        }

        return entries.OrderByDescending(entry => entry.TotalBytes).ThenBy(entry => entry.Name, StringComparer.Ordinal).ToArray();
    }

    private static async Task<IReadOnlyList<SqliteUsageEntry>> CollectKeyPrefixesAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT CASE
                     WHEN key IS NULL OR key = '' THEN '(无前缀)'
                     WHEN INSTR(key, ':') = 0 THEN key
                     ELSE SUBSTR(key, 1, INSTR(key, ':') - 1)
                   END AS prefix,
                   COUNT(*) AS rows,
                   COALESCE(SUM(LENGTH(key) + LENGTH(value)), 0) AS bytes
            FROM cursorDiskKV
            GROUP BY prefix
            ORDER BY bytes DESC, prefix;
            """;
        var entries = new List<SqliteUsageEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            entries.Add(new SqliteUsageEntry(
                reader.IsDBNull(0) ? "(无前缀)" : reader.GetString(0),
                reader.GetInt64(1),
                reader.GetInt64(2)));
        }

        return entries;
    }

    private static async Task<IReadOnlyList<SqliteUsageEntry>> CollectTopKeysAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT key, COALESCE(LENGTH(value), 0)
            FROM ItemTable
            WHERE key IS NOT NULL
            ORDER BY LENGTH(value) DESC, key
            LIMIT 10;
            """;
        var entries = new List<SqliteUsageEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            entries.Add(new SqliteUsageEntry(reader.GetString(0), 1, reader.GetInt64(1)));
        }

        return entries;
    }

    private static async Task<long> CollectChatBytesAsync(
        SqliteConnection connection,
        CursorChatSchema.DatabaseShape shape,
        IReadOnlyList<SqliteUsageEntry> tables,
        IReadOnlyList<SqliteUsageEntry> prefixes,
        CancellationToken cancellationToken)
    {
        var chatBytes = tables.Where(table => table.Name is "composerHeaders" or "conversations").Sum(table => table.TotalBytes)
            + prefixes.Where(prefix => ChatKeyPrefixes.Contains(prefix.Name)).Sum(prefix => prefix.TotalBytes);

        if (shape.ItemTable)
        {
            chatBytes += await ScalarCountAsync(
                connection,
                """
                SELECT COALESCE(SUM(LENGTH(key) + LENGTH(value)), 0)
                FROM ItemTable
                WHERE key = 'composer.composerData'
                   OR key LIKE 'composerData:%'
                   OR key LIKE 'bubbleId:%'
                   OR key LIKE 'checkpointId:%'
                   OR key LIKE 'composerChat:%';
                """,
                cancellationToken).ConfigureAwait(false);
        }

        return chatBytes;
    }

    private static async Task<int> CountConversationsAsync(
        SqliteConnection connection,
        CursorChatSchema.DatabaseShape shape,
        CancellationToken cancellationToken)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (shape.ComposerHeaders)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT composerId FROM composerHeaders;";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                AddConversationId(ids, reader.IsDBNull(0) ? null : reader.GetString(0));
            }
        }

        if (shape.Conversations)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT id FROM conversations;";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                AddConversationId(ids, reader.IsDBNull(0) ? null : reader.GetString(0));
            }
        }

        if (shape.CursorDiskKv)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT key FROM cursorDiskKV WHERE key LIKE 'composerData:%';";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var key = reader.IsDBNull(0) ? null : reader.GetString(0);
                AddConversationId(ids, ExtractComposerDataId(key));
            }
        }

        return ids.Count;
    }

    private static void AddConversationId(HashSet<string> ids, string? value)
    {
        if (SqliteConversationId.IsMatch(value) &&
            !string.Equals(value, CursorChatSchema.EmptyStateDraftId, StringComparison.OrdinalIgnoreCase))
        {
            ids.Add(value!);
        }
    }

    private static string? ExtractComposerDataId(string? key)
    {
        const string prefix = "composerData:";
        if (string.IsNullOrEmpty(key) || !key.StartsWith(prefix, StringComparison.Ordinal))
        {
            return null;
        }

        var remainder = key[prefix.Length..];
        var separator = remainder.IndexOf(':');
        return separator < 0 ? remainder : remainder[..separator];
    }

    private static string QuoteIdentifier(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";

    private static async Task<(long LogicalBytes, long FreeBytes)> GetPageStatsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA page_size; PRAGMA page_count; PRAGMA freelist_count;";
        var values = new List<long>(3);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        do
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                values.Add(reader.GetInt64(0));
            }
        }
        while (await reader.NextResultAsync(cancellationToken).ConfigureAwait(false));

        if (values.Count < 3 || values[0] <= 0)
        {
            return (0, 0);
        }

        return (values[1] * values[0], values[2] * values[0]);
    }

    private static async Task<long> ScalarCountAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is long value ? value : Convert.ToInt64(result ?? 0L);
    }

    private bool IdentityMatches(string path, FileIdentity? expected, out string? error)
    {
        if (!_pathGuard.TryGetFileIdentity(path, out var actual, out error))
        {
            return false;
        }

        if (actual != expected)
        {
            error = "The database file identity changed during maintenance.";
            return false;
        }

        error = null;
        return true;
    }

    private async Task<string> CreateVerifiedBackupAsync(
        SqliteConnection connection,
        string stagingPath,
        IProgress<SqliteProgress>? progress,
        string sourcePath,
        CancellationToken cancellationToken,
        int databaseIndex = 1,
        int databaseCount = 1)
    {
        await using (var backupConnection = new SqliteConnection(BuildConnectionString(stagingPath, SqliteOpenMode.ReadWriteCreate)))
        {
            await backupConnection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await BackupDatabaseWithProgressAsync(connection, backupConnection, progress, sourcePath, databaseIndex, databaseCount, cancellationToken).ConfigureAwait(false);
            Report(progress, SqliteProgressStage.VerifyingBackup, sourcePath, databaseIndex, databaseCount);
            await RunQuickCheckAsync(backupConnection, cancellationToken).ConfigureAwait(false);
            await RunCheckpointAsync(backupConnection, cancellationToken).ConfigureAwait(false);
        }

        return await _backupService.CommitSqliteBackupAsync(stagingPath, cancellationToken).ConfigureAwait(false);
    }

    private static async Task BackupDatabaseWithProgressAsync(
        SqliteConnection source,
        SqliteConnection destination,
        IProgress<SqliteProgress>? progress,
        string sourcePath,
        int databaseIndex,
        int databaseCount,
        CancellationToken cancellationToken)
    {
        var sourceHandle = source.Handle;
        var destinationHandle = destination.Handle;
        if (sourceHandle is null || destinationHandle is null || sourceHandle.IsInvalid || destinationHandle.IsInvalid)
        {
            throw new IOException("SQLite connection handle is unavailable for online backup.");
        }

        using var backup = raw.sqlite3_backup_init(destinationHandle, "main", sourceHandle, "main");
        if (backup is null || backup.IsInvalid)
        {
            throw new IOException("SQLite online backup could not be started.");
        }

        Report(progress, SqliteProgressStage.BackingUp, sourcePath, databaseIndex, databaseCount, 0);
        var lastReported = -1;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rc = raw.sqlite3_backup_step(backup, 64);
            if (rc is not raw.SQLITE_OK and not raw.SQLITE_DONE and not raw.SQLITE_BUSY and not raw.SQLITE_LOCKED)
            {
                throw new IOException($"SQLite online backup failed with code {rc}.");
            }

            var remaining = raw.sqlite3_backup_remaining(backup);
            var pageCount = raw.sqlite3_backup_pagecount(backup);
            var percent = pageCount <= 0
                ? 0
                : Math.Clamp((int)Math.Round((pageCount - remaining) * 100d / pageCount), 0, 100);
            if (percent != lastReported)
            {
                lastReported = percent;
                Report(progress, SqliteProgressStage.BackingUp, sourcePath, databaseIndex, databaseCount, percent);
            }

            if (rc == raw.SQLITE_DONE)
            {
                break;
            }

            if (rc is raw.SQLITE_BUSY or raw.SQLITE_LOCKED)
            {
                await Task.Delay(15, cancellationToken).ConfigureAwait(false);
                continue;
            }

            await Task.Yield();
        }

        Report(progress, SqliteProgressStage.BackingUp, sourcePath, databaseIndex, databaseCount, 100);
    }

    private static void Report(
        IProgress<SqliteProgress>? progress,
        SqliteProgressStage stage,
        string databasePath,
        int databaseIndex = 1,
        int databaseCount = 1,
        int? percent = null)
    {
        progress?.Report(new SqliteProgress(
            stage,
            databasePath,
            databaseIndex,
            databaseCount,
            percent,
            DisplayText.SqliteProgressMessage(stage, databasePath, databaseIndex, databaseCount, percent)));
    }

    private static async Task<SemaphoreSlim> AcquireDatabaseLockAsync(string path, CancellationToken cancellationToken)
    {
        var key = CanonicalLockKey(path);
        var gate = DatabaseLocks.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return gate;
    }

    private static string CanonicalLockKey(string path)
    {
        var normalized = PathSafety.Normalize(path);
        return OperatingSystem.IsWindows() ? normalized.ToUpperInvariant() : normalized;
    }

    private static async Task RunCheckpointAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var checkpoint = connection.CreateCommand();
        checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
        await using var reader = await checkpoint.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new IOException("wal_checkpoint returned no status row.");
        }

        var busy = reader.GetInt64(0);
        if (busy != 0)
        {
            throw new IOException($"wal_checkpoint is busy ({busy}); VACUUM was not started.");
        }
    }

    private static async Task RunQuickCheckAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA quick_check;";
        var rows = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(reader.GetString(0));
        }

        if (rows.Count != 1 || !string.Equals(rows[0], "ok", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"quick_check returned: {string.Join("; ", rows)}");
        }
    }

    private static string BuildConnectionString(string path, SqliteOpenMode mode) => new SqliteConnectionStringBuilder
    {
        DataSource = path,
        Mode = mode,
        Cache = SqliteCacheMode.Private,
        Pooling = false
    }.ToString();

    private static bool TryGetCombinedSize(string path, out long size, out string? error)
    {
        try
        {
            size = new[] { path, path + "-wal", path + "-shm" }
                .Where(File.Exists)
                .Sum(candidate => new FileInfo(candidate).Length);
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            size = 0;
            error = ex.Message;
            return false;
        }
    }

    private static void TryDeleteIncompleteBackup(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            foreach (var candidate in new[] { path, path + "-wal", path + "-shm", path + "-journal" })
            {
                if (File.Exists(candidate))
                {
                    File.Delete(candidate);
                }
            }
        }
        catch
        {
        }

        try
        {
            var directory = Path.GetDirectoryName(path);
            if (directory is not null && Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory);
            }
        }
        catch
        {
        }
    }

    private async Task TryLogAsync(string level, string operation, string message, string path, Exception? exception)
    {
        try { await _log.WriteAsync(level, operation, message, path, exception).ConfigureAwait(false); } catch { }
    }

    private SqliteSpaceFailure? CreateDiskFullFailure(string path, bool backupWasKept)
    {
        try
        {
            var failure = _backupService.CheckVacuumSpace(path, backupWasKept);
            if (failure is not null)
            {
                return failure with { Stage = SqliteSpaceFailureStage.Vacuum };
            }
        }
        catch
        {
        }

        return new SqliteSpaceFailure(
            SqliteSpaceFailureStage.Vacuum,
            "数据库所在卷",
            0,
            0,
            false,
            backupWasKept);
    }

    private static SqliteMaintenanceResult SpaceFailure(
        string path,
        long sizeBefore,
        string? backupPath,
        SqliteSpaceFailure failure) =>
        new(path, false, sizeBefore, sizeBefore, backupPath, FormatSpaceFailure(failure), failure);

    private static string FormatSpaceFailure(SqliteSpaceFailure failure)
    {
        var backup = failure.BackupWasKept ? "；已校验的在线备份已保留，未启动 VACUUM" : string.Empty;
        return $"卷 {failure.VolumeName} 空间不足：可用 {failure.AvailableBytes} 字节，需要 {failure.RequiredBytes} 字节，还差 {failure.MissingBytes} 字节{backup}";
    }

    private static SqliteMaintenanceResult Failure(string path, long sizeBefore, string? backupPath, string error) =>
        new(path, false, sizeBefore, sizeBefore, backupPath, error);
}
