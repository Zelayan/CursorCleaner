using System.IO;
using System.Text.Json;
using CursorCleaner.Helpers;
using CursorCleaner.Models;

namespace CursorCleaner.Services;

public interface IWorkspaceAnalyzerService
{
    Task<IReadOnlyList<WorkspaceInfo>> AnalyzeAsync(
        ScanResult scanResult,
        CancellationToken cancellationToken = default);
}

public sealed class WorkspaceAnalyzerService : IWorkspaceAnalyzerService
{
    private const int MaximumWorkspaceJsonBytes = 64 * 1024;

    public Task<IReadOnlyList<WorkspaceInfo>> AnalyzeAsync(
        ScanResult scanResult,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scanResult);
        return Task.Run<IReadOnlyList<WorkspaceInfo>>(
            () => Analyze(scanResult.Items, cancellationToken),
            cancellationToken);
    }

    private static IReadOnlyList<WorkspaceInfo> Analyze(
        IEnumerable<ScanItem> items,
        CancellationToken cancellationToken)
    {
        var groups = new Dictionary<string, WorkspaceGroup>(PathSafety.PathComparer);
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryGetWorkspaceUnit(item.FullPath, out var id, out var workspacePath))
            {
                continue;
            }

            if (!groups.TryGetValue(workspacePath, out var group))
            {
                group = new WorkspaceGroup(id, workspacePath);
                groups.Add(workspacePath, group);
            }

            group.FileCount++;
            group.TotalBytes += item.Size;
            if (item.LastWriteTimeUtc > group.LastWriteTimeUtc)
            {
                group.LastWriteTimeUtc = item.LastWriteTimeUtc;
            }
            if (Path.GetFileName(item.FullPath).Equals("workspace.json", StringComparison.OrdinalIgnoreCase))
            {
                group.WorkspaceJsonPath = item.FullPath;
            }
        }

        var result = new List<WorkspaceInfo>(groups.Count);
        foreach (var group in groups.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var projectPath = TryReadProjectPath(group.WorkspaceJsonPath);
            var displayName = GetDisplayName(projectPath, group.Id);
            var projectMissing = projectPath is not null &&
                !Directory.Exists(projectPath) &&
                !File.Exists(projectPath);
            result.Add(new WorkspaceInfo(
                group.Id,
                group.WorkspacePath,
                projectPath,
                displayName,
                group.FileCount,
                group.TotalBytes,
                group.LastWriteTimeUtc,
                projectMissing));
        }

        return result
            .OrderByDescending(workspace => workspace.LastWriteTimeUtc)
            .ToArray();
    }

    private static bool TryGetWorkspaceUnit(string path, out string id, out string workspacePath)
    {
        id = string.Empty;
        workspacePath = string.Empty;
        var normalized = PathSafety.Normalize(path);
        var root = Path.GetPathRoot(normalized) ?? string.Empty;
        var relative = normalized[root.Length..];
        var segments = relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < segments.Length - 1; index++)
        {
            if (!segments[index].Equals("workspaceStorage", StringComparison.OrdinalIgnoreCase) &&
                !segments[index].Equals("workspaces", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            id = segments[index + 1];
            workspacePath = Path.Combine(root, Path.Combine(segments[..(index + 2)]));
            return true;
        }
        return false;
    }

    private static string? TryReadProjectPath(string? workspaceJsonPath)
    {
        if (workspaceJsonPath is null)
        {
            return null;
        }

        try
        {
            var info = new FileInfo(workspaceJsonPath);
            if (!info.Exists || info.Length > MaximumWorkspaceJsonBytes)
            {
                return null;
            }

            using var stream = new FileStream(
                workspaceJsonPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                4096,
                FileOptions.SequentialScan);
            using var document = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 16 });
            foreach (var propertyName in new[] { "folder", "workspace", "file" })
            {
                if (document.RootElement.TryGetProperty(propertyName, out var property) &&
                    property.ValueKind == JsonValueKind.String &&
                    TryParseFileUri(property.GetString(), out var path))
                {
                    return path;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or UriFormatException)
        {
        }
        return null;
    }

    private static bool TryParseFileUri(string? value, out string path)
    {
        path = string.Empty;
        if (string.IsNullOrWhiteSpace(value) ||
            !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            !uri.IsFile)
        {
            return false;
        }

        path = PathSafety.Normalize(uri.LocalPath);
        return true;
    }

    private static string GetDisplayName(string? projectPath, string fallback)
    {
        if (projectPath is null)
        {
            return fallback;
        }

        var name = Path.GetFileName(projectPath);
        if (string.IsNullOrWhiteSpace(name))
        {
            return projectPath;
        }
        return name;
    }

    private sealed class WorkspaceGroup(string id, string workspacePath)
    {
        public string Id { get; } = id;
        public string WorkspacePath { get; } = workspacePath;
        public string? WorkspaceJsonPath { get; set; }
        public long FileCount { get; set; }
        public long TotalBytes { get; set; }
        public DateTime LastWriteTimeUtc { get; set; }
    }
}
