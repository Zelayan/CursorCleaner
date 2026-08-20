using System.IO;
using System.Text.Json;
using CursorCleaner.Helpers;
using CursorCleaner.Models;
using Microsoft.Data.Sqlite;

namespace CursorCleaner.Services;

public sealed class SqliteService : ISqliteService
{
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
            await using var connection = new SqliteConnection(BuildConnectionString(path, SqliteOpenMode.ReadWrite));
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            guard = _pathGuard.ValidateSqliteTarget(path, roots);
            if (!guard.IsSafe || !IdentityMatches(path, initialIdentity, out identityError))
            {
                return Failure(path, sizeBefore, null, guard.Error ?? identityError ?? "Database changed while opening the write connection.");
            }

            if (_processService.IsCursorRunning())
            {
                return Failure(path, sizeBefore, null, "Cursor started before database maintenance; no backup, checkpoint, or VACUUM was performed.");
            }

            await RunQuickCheckAsync(connection, cancellationToken).ConfigureAwait(false);

            reservedBackupPath = await _backupService.CreateSqliteBackupPathAsync(path, cancellationToken).ConfigureAwait(false);
            await using (var backupConnection = new SqliteConnection(BuildConnectionString(reservedBackupPath, SqliteOpenMode.ReadWriteCreate)))
            {
                await backupConnection.OpenAsync(cancellationToken).ConfigureAwait(false);
                connection.BackupDatabase(backupConnection);
                await RunQuickCheckAsync(backupConnection, cancellationToken).ConfigureAwait(false);
            }

            verifiedBackupPath = reservedBackupPath;

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

            await using var vacuum = connection.CreateCommand();
            vacuum.CommandText = "VACUUM;";
            await vacuum.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
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
            var classification = ex is SqliteException sqliteException && sqliteException.SqliteErrorCode == 26
                ? "Database is damaged or is not a SQLite database"
                : "Database is locked or maintenance failed";
            return Failure(path, sizeBefore, verifiedBackupPath, $"{classification}: {ex.Message}");
        }

        if (!TryGetCombinedSize(path, out var sizeAfter, out sizeError))
        {
            return new SqliteMaintenanceResult(path, false, sizeBefore, sizeBefore, verifiedBackupPath,
                $"VACUUM completed, but the resulting database size could not be read: {sizeError}");
        }

        await TryLogAsync("info", "sqlite.vacuum", $"Database maintenance completed; size changed from {sizeBefore} to {sizeAfter} bytes.", path, null).ConfigureAwait(false);
        return new SqliteMaintenanceResult(path, true, sizeBefore, sizeAfter, verifiedBackupPath, null);
    }

    public async Task<SqliteChatCleanupResult> DeleteChatRecordsAsync(
        IEnumerable<string> conversationIds,
        IEnumerable<string> databasePaths,
        IEnumerable<string> approvedRoots,
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
        foreach (var databasePath in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_processService.IsCursorRunning())
            {
                results.Add(new SqliteChatDatabaseResult(databasePath, false, 0, null, "Cursor started; remaining databases were not modified."));
                continue;
            }

            results.Add(await DeleteFromDatabaseAsync(databasePath, ids, roots, cancellationToken).ConfigureAwait(false));
        }

        var blocked = results.Any(item => item.Error?.Contains("Cursor", StringComparison.OrdinalIgnoreCase) == true && !item.Succeeded);
        var succeeded = results.Count > 0 && results.All(item => item.Succeeded);
        var error = succeeded
            ? null
            : results.Where(item => !item.Succeeded).Select(item => item.Error).FirstOrDefault() ?? "SQLite chat deletion failed.";
        return new SqliteChatCleanupResult(succeeded, blocked && !results.Any(item => item.Succeeded), results, error);
    }

    private async Task<SqliteChatDatabaseResult> DeleteFromDatabaseAsync(
        string databasePath,
        IReadOnlyList<string> ids,
        IReadOnlyList<string> roots,
        CancellationToken cancellationToken)
    {
        var guard = _pathGuard.ValidateSqliteTarget(databasePath, roots);
        if (!guard.IsSafe)
        {
            return new SqliteChatDatabaseResult(databasePath, false, 0, null, guard.Error ?? "Database path validation failed.");
        }

        var path = guard.NormalizedPath!;
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

            guard = _pathGuard.ValidateSqliteTarget(path, roots);
            if (!guard.IsSafe || !IdentityMatches(path, initialIdentity, out identityError))
            {
                return new SqliteChatDatabaseResult(path, false, 0, null, guard.Error ?? identityError ?? "Database changed while opening the write connection.");
            }

            if (_processService.IsCursorRunning())
            {
                return new SqliteChatDatabaseResult(path, false, 0, null, "Cursor started before chat deletion; no backup or write was performed.");
            }

            await RunQuickCheckAsync(connection, cancellationToken).ConfigureAwait(false);

            var shape = await CursorChatSchema.DiscoverAsync(connection, cancellationToken).ConfigureAwait(false);
            if (!shape.IsRecognized)
            {
                return new SqliteChatDatabaseResult(path, false, 0, null, "Database schema is not a recognized Cursor chat store; no rows were deleted.");
            }

            if (!await CursorChatSchema.HasChatDataAsync(connection, shape, cancellationToken).ConfigureAwait(false))
            {
                return new SqliteChatDatabaseResult(path, true, 0, null, "Skipped: no Cursor chat records were found.");
            }

            reservedBackupPath = await _backupService.CreateSqliteBackupPathAsync(path, cancellationToken).ConfigureAwait(false);
            await using (var backupConnection = new SqliteConnection(BuildConnectionString(reservedBackupPath, SqliteOpenMode.ReadWriteCreate)))
            {
                await backupConnection.OpenAsync(cancellationToken).ConfigureAwait(false);
                connection.BackupDatabase(backupConnection);
                await RunQuickCheckAsync(backupConnection, cancellationToken).ConfigureAwait(false);
            }

            verifiedBackupPath = reservedBackupPath;

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

            var deleted = await CursorChatSchema.DeleteAsync(connection, shape, ids, cancellationToken).ConfigureAwait(false);
            await TryLogAsync("info", "sqlite.chat.delete", $"Deleted {deleted} chat rows for {ids.Count} conversation IDs.", path, null).ConfigureAwait(false);
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
            if (File.Exists(path))
            {
                File.Delete(path);
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

    private static SqliteMaintenanceResult Failure(string path, long sizeBefore, string? backupPath, string error) =>
        new(path, false, sizeBefore, sizeBefore, backupPath, error);
}
