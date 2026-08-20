namespace CursorCleaner.Models;

public sealed record WorkspaceInfo(
    string Id,
    string WorkspacePath,
    string? ProjectPath,
    string DisplayName,
    long FileCount,
    long TotalBytes,
    DateTime LastWriteTimeUtc,
    bool ProjectMissing);

public sealed record SessionInfo(
    string Id,
    string FilePath,
    string Title,
    string? ProjectName,
    DataCategory Category,
    long Size,
    DateTime LastWriteTimeUtc);

public sealed record SessionMessage(string Role, string DisplayRole, string Text);

public sealed record SessionContentPreview(
    string FilePath,
    IReadOnlyList<SessionMessage> Messages,
    bool Truncated,
    string? Error);

public sealed class AppSettings
{
    public bool ScanOnStartup { get; set; } = true;
    public bool ConfirmBeforeCleanup { get; set; } = true;
    public bool CreateBackupBeforeCleanup { get; set; } = true;
    public int BackupRetentionDays { get; set; } = 30;
    public long LargeFileThresholdBytes { get; set; } = 100L * 1024 * 1024;
    public int MaximumDisplayedItems { get; set; } = 250_000;
    public string? BackupDirectory { get; set; }
}
