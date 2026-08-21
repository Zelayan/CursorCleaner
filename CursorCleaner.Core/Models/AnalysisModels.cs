using CursorCleaner.Helpers;

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

public enum SessionSource
{
    File,
    Database,
    Both
}

public sealed record SessionInfo(
    string Id,
    string FilePath,
    string Title,
    string? ProjectName,
    DataCategory Category,
    long Size,
    DateTime LastWriteTimeUtc,
    SessionSource Source = SessionSource.File,
    string? DatabasePath = null,
    IReadOnlyList<string>? ConversationIds = null,
    IReadOnlyList<string>? DatabasePaths = null)
{
    public string DisplayPath => !string.IsNullOrWhiteSpace(FilePath) ? FilePath : DatabasePath ?? string.Empty;

    /// <summary>
    /// File bytes that can be reclaimed immediately. Database-only sessions show an em dash
    /// because Size is 0 and does not mean the chat occupies no disk in SQLite.
    /// </summary>
    public string DisplaySizeText =>
        Source == SessionSource.Database && Size <= 0
            ? "—"
            : ByteSizeFormatter.Format(Size);

    /// <summary>
    /// All chat database paths known for this session (preferred path first when present).
    /// </summary>
    public IReadOnlyList<string> AllDatabasePaths
    {
        get
        {
            var paths = new List<string>();
            var seen = new HashSet<string>(PathSafety.PathComparer);
            void Add(string? path)
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    return;
                }

                var normalized = PathSafety.Normalize(path);
                if (seen.Add(normalized))
                {
                    paths.Add(normalized);
                }
            }

            Add(DatabasePath);
            if (DatabasePaths is not null)
            {
                foreach (var path in DatabasePaths)
                {
                    Add(path);
                }
            }

            return paths;
        }
    }

    public IReadOnlyList<string> DeletableConversationIds
    {
        get
        {
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (ConversationIds is not null)
            {
                foreach (var id in ConversationIds)
                {
                    if (SqliteConversationId.IsMatch(id))
                    {
                        ids.Add(id);
                    }
                }
            }

            if (SqliteConversationId.IsMatch(Id))
            {
                ids.Add(Id);
            }

            return ids.ToArray();
        }
    }
}

public static class SqliteConversationId
{
    public static bool IsMatch(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        Guid.TryParse(value, out var guid) &&
        guid != Guid.Empty;
}

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
