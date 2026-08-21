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
    public CleanerTheme Theme { get; set; } = CleanerTheme.System;
    public string? BackupDirectory { get; set; }
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
    string? Error,
    SqliteSpaceFailure? SpaceFailure = null)
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
    string? Error,
    bool Cancelled = false)
{
    public int DeletedRows => Databases.Sum(item => item.DeletedRows);
    public int FailedDatabases => Databases.Count(item => !item.Succeeded);
}

public enum SqliteProgressStage
{
    CheckingSpace,
    Checking,
    PreparingBackup,
    BackingUp,
    VerifyingBackup,
    DeletingRows,
    Checkpoint,
    Vacuuming,
    VerifyingResult,
    Completed
}

public sealed record SqliteProgress(
    SqliteProgressStage Stage,
    string DatabasePath,
    int DatabaseIndex,
    int DatabaseCount,
    int? Percent,
    string Message);

public enum SqliteSpaceFailureStage
{
    InitialCheck,
    BackupCheck,
    BeforeVacuum,
    Vacuum
}

public sealed record VolumeInfo(
    string Id,
    string DisplayName,
    long AvailableBytes);

public sealed record VolumeSpaceRequirement(
    VolumeInfo Volume,
    long RequiredBytes)
{
    public long MissingBytes => Math.Max(0, RequiredBytes - Volume.AvailableBytes);
    public bool HasEnoughSpace => MissingBytes == 0;
}

public sealed record SqliteSpacePlan(
    string DatabasePath,
    string BackupRootPath,
    long MainDatabaseBytes,
    long BackupBytes,
    long VacuumWorkingBytes,
    long ExistingBackupBytes,
    long SafetyMarginBytes,
    bool IncludesVacuum,
    bool IsSameVolume,
    VolumeSpaceRequirement SourceRequirement,
    VolumeSpaceRequirement BackupRequirement)
{
    public bool HasEnoughSpace => SourceRequirement.HasEnoughSpace && BackupRequirement.HasEnoughSpace;
}

public sealed record SqliteSpaceFailure(
    SqliteSpaceFailureStage Stage,
    string VolumeName,
    long AvailableBytes,
    long RequiredBytes,
    bool IsSameVolume,
    bool BackupWasKept)
{
    public long MissingBytes => Math.Max(0, RequiredBytes - AvailableBytes);
}

public sealed record SqliteUsageEntry(string Name, long RowCount, long TotalBytes);

public sealed record SqliteUsageReport(
    string DatabasePath,
    long FileBytes,
    long WalBytes,
    long LogicalBytes,
    long FreePagesBytes,
    bool IsChatStore,
    int ConversationCount,
    long ChatBytes,
    IReadOnlyList<SqliteUsageEntry> Tables,
    IReadOnlyList<SqliteUsageEntry> KeyPrefixes,
    IReadOnlyList<SqliteUsageEntry> TopItemTableKeys,
    string? Error)
{
    public bool Succeeded => Error is null;
}

public sealed record SqliteBackupUsage(
    string BackupRootPath,
    long RollingBytes,
    int RollingDatabaseCount,
    long LegacyBytes,
    int LegacyDirectoryCount);

public sealed record SqliteBackupCleanupResult(
    bool Succeeded,
    int DeletedDirectories,
    long ReclaimedBytes,
    string? Error);

public sealed record StopCursorResult(
    bool Succeeded,
    bool WasRunning,
    int TerminatedCount,
    string? Error);

public interface IProcessService
{
    bool IsCursorRunning();
    Task<StopCursorResult> StopCursorAsync(CancellationToken cancellationToken = default);
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
    string BackupRootPath { get; }
    Task<BackupOperationResult> BackupAsync(IEnumerable<CleanupPlanItem> items, CancellationToken cancellationToken = default);
    Task<string> CreateSqliteBackupPathAsync(string databasePath, CancellationToken cancellationToken = default);
    Task<string> CommitSqliteBackupAsync(string stagingPath, CancellationToken cancellationToken = default);
    SqliteSpacePlan CreateSqliteSpacePlan(string databasePath, bool includeVacuum);
    SqliteSpaceFailure? CheckSqliteSpace(SqliteSpacePlan plan, SqliteSpaceFailureStage stage, bool backupWasKept = false);
    SqliteSpaceFailure? CheckVacuumSpace(string databasePath, bool backupWasKept = true);
    /// <summary>
    /// Fail-closed free-space check on the volume that contains <paramref name="pathOnVolume"/>.
    /// </summary>
    void EnsureVolumeFreeSpace(string pathOnVolume, long requiredBytes, string operationLabel);
    SqliteBackupUsage GetSqliteBackupUsage();
    Task<SqliteBackupCleanupResult> CleanupLegacySqliteBackupsAsync(CancellationToken cancellationToken = default);
}

public interface IVolumeService
{
    bool TryGetVolume(string path, out VolumeInfo? volume, out string? error);
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

public interface IFolderPickerService
{
    Task<string?> PickFolderAsync(string title, string? suggestedPath = null, CancellationToken cancellationToken = default);
}

public interface ISqliteService
{
    Task<SqliteMaintenanceResult> VacuumAsync(
        string databasePath,
        IEnumerable<string> approvedRoots,
        IProgress<SqliteProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<SqliteChatCleanupResult> DeleteChatRecordsAsync(
        IEnumerable<string> conversationIds,
        IEnumerable<string> databasePaths,
        IEnumerable<string> approvedRoots,
        IProgress<SqliteProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<SqliteUsageReport> AnalyzeUsageAsync(
        string databasePath,
        IEnumerable<string> approvedRoots,
        CancellationToken cancellationToken = default);
}

public interface ISessionContentService
{
    Task<SessionContentPreview> ReadAsync(string path, CancellationToken cancellationToken = default);
}
