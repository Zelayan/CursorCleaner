using System.Collections.ObjectModel;
using System.IO;

namespace CursorCleaner.Models;

public enum CleanerTheme
{
    System,
    Light,
    Dark
}

public sealed class OperationSettings
{
    public int RetentionDays { get; set; } = 30;
    public bool AutomaticBackup { get; set; } = true;
    public bool UseRecycleBin { get; set; } = true;
    public bool ScanRoamingData { get; set; } = true;
    public bool ScanLocalData { get; set; } = true;
    public bool ScanUserProfile { get; set; } = true;
    public bool AdvancedToolsEnabled { get; set; }
    public bool AdvancedFeaturesEnabled { get; set; }
    public CleanerTheme Theme { get; set; } = CleanerTheme.System;
}

public sealed record CleanupPlanItem(
    string FullPath,
    string RelativePath,
    CursorDataRoot Root,
    DataCategory Category,
    long Size,
    DateTime LastWriteTimeUtc,
    FileIdentity? Identity = null);

public sealed record FileIdentity(ulong DeviceId, ulong FileId);

public sealed record CleanupPlan
{
    public CleanupPlan(Guid id, DateTime createdAtUtc, IEnumerable<CleanupPlanItem> items)
    {
        Id = id;
        CreatedAtUtc = createdAtUtc;
        Items = new ReadOnlyCollection<CleanupPlanItem>(items.ToArray());
        TotalSize = Items.Sum(item => item.Size);
    }

    public Guid Id { get; }
    public DateTime CreatedAtUtc { get; }
    public IReadOnlyList<CleanupPlanItem> Items { get; }
    public int FileCount => Items.Count;
    public long TotalSize { get; }
}

public sealed record PathGuardResult(bool IsSafe, string? NormalizedPath, string? Error);

public sealed record BackupItemResult(
    string OriginalPath,
    string? BackupPath,
    long Size,
    DateTime LastWriteTimeUtc,
    bool Succeeded,
    string? Error);

public sealed record BackupOperationResult(
    bool Succeeded,
    string? BackupDirectory,
    long OriginalSize,
    IReadOnlyList<BackupItemResult> Files,
    string? Error = null);

public sealed record RecycleResult(string Path, bool Succeeded, string? Error);

public enum CleanupDisposition
{
    Recycle,
    PermanentDelete
}

public sealed record CleanupOptions(bool AutomaticBackup = true, CleanupDisposition Disposition = CleanupDisposition.Recycle);

public sealed record CleanupItemResult(
    string Path,
    bool Succeeded,
    long ReclaimedBytes,
    string? Error,
    string? BackupPath = null);

public sealed record CleanupOperationResult(
    Guid PlanId,
    bool Succeeded,
    bool Blocked,
    bool Cancelled,
    IReadOnlyList<CleanupItemResult> Items,
    string? Error = null)
{
    public int DeletedFiles => Items.Count(item => item.Succeeded);
    public long ReclaimedBytes => Items.Sum(item => item.ReclaimedBytes);
}

public sealed record SqliteMaintenanceResult(
    string DatabasePath,
    bool Succeeded,
    long SizeBefore,
    long SizeAfter,
    string? BackupPath,
    string? Error)
{
    public long ReclaimedBytes => Math.Max(0, SizeBefore - SizeAfter);
}

public sealed record SqliteChatDatabaseResult(
    string DatabasePath,
    bool Succeeded,
    int DeletedRows,
    string? BackupPath,
    string? Error);

public sealed record SqliteChatCleanupResult(
    bool Succeeded,
    bool Blocked,
    IReadOnlyList<SqliteChatDatabaseResult> Databases,
    string? Error)
{
    public int DeletedRows => Databases.Sum(item => item.DeletedRows);
    public int FailedDatabases => Databases.Count(item => !item.Succeeded);
}

public interface IProcessService
{
    bool IsCursorRunning();
}

public interface ILogService
{
    string LogDirectory { get; }
    Task WriteAsync(string level, string operation, string message, string? path = null, Exception? exception = null, CancellationToken cancellationToken = default);
}

public interface ISettingsService
{
    string SettingsPath { get; }
    Task<OperationSettings> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(OperationSettings settings, CancellationToken cancellationToken = default);
}

public interface ICleanupPlannerService
{
    CleanupPlan CreatePlan(ScanResult scanResult, IEnumerable<string> approvedRoots, DateTime cutoffUtc);
    CleanupPlan CreateSelectedPlan(ScanResult scanResult, IEnumerable<string> approvedRoots, IEnumerable<string> selectedPaths);
}

public interface IFileIdentityService
{
    bool TryGetFileIdentity(string path, out FileIdentity? identity, out string? error);
}

public interface IPathGuard
{
    IReadOnlyList<string> CursorRoots { get; }
    PathGuardResult ValidateCleanupTarget(string path, IEnumerable<string> approvedRoots);
    PathGuardResult ValidateSqliteTarget(string path, IEnumerable<string> approvedRoots);
    bool TryGetFileIdentity(string path, out FileIdentity? identity, out string? error);
}

public interface IThemeService
{
    void Apply(CleanerTheme theme);
}

public interface IBackupService
{
    Task<BackupOperationResult> BackupAsync(IEnumerable<CleanupPlanItem> items, CancellationToken cancellationToken = default);
    Task<string> CreateSqliteBackupPathAsync(string databasePath, CancellationToken cancellationToken = default);
}

public interface IRecycleBinService
{
    Task<RecycleResult> RecycleAsync(string path, CancellationToken cancellationToken = default);
}

public interface ICleanupService
{
    Task<CleanupOperationResult> ExecuteAsync(CleanupPlan plan, bool confirmed, CleanupOptions options, CancellationToken cancellationToken = default);
}

public interface IShellService
{
    void OpenDirectory(string path);
    void SelectFile(string path);
    void OpenLogs();
}

public interface IDialogService
{
    Task<bool> ConfirmAsync(string title, string message, CancellationToken cancellationToken = default);
    Task ShowErrorAsync(string title, string message, CancellationToken cancellationToken = default);
}

public interface ISqliteService
{
    Task<SqliteMaintenanceResult> VacuumAsync(string databasePath, IEnumerable<string> approvedRoots, CancellationToken cancellationToken = default);

    Task<SqliteChatCleanupResult> DeleteChatRecordsAsync(
        IEnumerable<string> conversationIds,
        IEnumerable<string> databasePaths,
        IEnumerable<string> approvedRoots,
        CancellationToken cancellationToken = default);
}

public interface ISessionContentService
{
    Task<SessionContentPreview> ReadAsync(string path, CancellationToken cancellationToken = default);
}
