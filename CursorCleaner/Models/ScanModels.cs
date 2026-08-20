using System.IO;

namespace CursorCleaner.Models;

public enum RootKind
{
    RoamingData,
    LocalData,
    UserProfile,
    Compatibility
}

public sealed record CursorDataRoot(string Path, RootKind Kind, string DisplayName);

public enum DataCategory
{
    SQLite,
    Workspace,
    AgentTranscript,
    ChatSession,
    Other
}

public sealed record ScanItem(
    string FullPath,
    string RelativePath,
    CursorDataRoot Root,
    DataCategory Category,
    long Size,
    DateTime LastWriteTimeUtc,
    FileAttributes Attributes);

public sealed record LargeFileInfo(
    string FullPath,
    long Size,
    DataCategory Category,
    DateTime LastWriteTimeUtc);

public sealed record ScanProgress(
    long FilesScanned,
    long BytesScanned,
    string? CurrentPath,
    int RootsCompleted,
    int TotalRoots);

public sealed record ScanSummary(
    long TotalFiles,
    long TotalBytes,
    IReadOnlyDictionary<DataCategory, long> FileCountByCategory,
    IReadOnlyDictionary<DataCategory, long> BytesByCategory,
    IReadOnlyList<LargeFileInfo> LargestFiles,
    int ErrorCount,
    TimeSpan Duration);

public sealed record ScanResult(
    IReadOnlyList<ScanItem> Items,
    ScanSummary Summary,
    DateTime CompletedAtUtc);
