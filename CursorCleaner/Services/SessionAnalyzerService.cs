using System.IO;
using System.Text;
using System.Text.Json;
using CursorCleaner.Models;

namespace CursorCleaner.Services;

public interface ISessionAnalyzerService
{
    Task<IReadOnlyList<SessionInfo>> AnalyzeAsync(
        ScanResult scanResult,
        CancellationToken cancellationToken = default);
}

public sealed class SessionAnalyzerService : ISessionAnalyzerService
{
    private const int MaximumBytes = 64 * 1024;
    private const int MaximumLines = 80;
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".json", ".jsonl"
    };

    public Task<IReadOnlyList<SessionInfo>> AnalyzeAsync(
        ScanResult scanResult,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scanResult);
        return Task.Run<IReadOnlyList<SessionInfo>>(
            () => Analyze(scanResult.Items, cancellationToken),
            cancellationToken);
    }

    private static IReadOnlyList<SessionInfo> Analyze(
        IEnumerable<ScanItem> items,
        CancellationToken cancellationToken)
    {
        var sessions = new List<SessionInfo>();
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if ((item.Category != DataCategory.ChatSession &&
                 item.Category != DataCategory.AgentTranscript) ||
                !SupportedExtensions.Contains(Path.GetExtension(item.FullPath)))
            {
                continue;
            }

            var projectName = GetProjectName(item.FullPath);
            var title = TryReadTitle(item.FullPath) ?? BuildFallbackTitle(projectName, item.LastWriteTimeUtc);
            sessions.Add(new SessionInfo(
                Path.GetFileNameWithoutExtension(item.FullPath),
                item.FullPath,
                title,
                projectName,
                item.Category,
                item.Size,
                item.LastWriteTimeUtc));
        }

        return sessions
            .OrderByDescending(session => session.LastWriteTimeUtc)
            .ToArray();
    }

    private static string? TryReadTitle(string path)
    {
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                4096,
                FileOptions.SequentialScan);
            var buffer = new byte[MaximumBytes];
            var total = 0;
            while (total < buffer.Length)
            {
                var read = stream.Read(buffer, total, buffer.Length - total);
                if (read == 0)
                {
                    break;
                }
                total += read;
            }

            var text = Encoding.UTF8.GetString(buffer, 0, total)
                .TrimStart('\uFEFF', ' ', '\r', '\n', '\t');
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            if (Path.GetExtension(path).Equals(".jsonl", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var line in text.Split('\n').Take(MaximumLines))
                {
                    var title = TryFindTitle(line.Trim());
                    if (title is not null)
                    {
                        return title;
                    }
                }
                return null;
            }

            return TryFindTitle(text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or DecoderFallbackException)
        {
            return null;
        }
    }

    private static string? TryFindTitle(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
            return FindTitle(document.RootElement, 0);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? FindTitle(JsonElement element, int depth)
    {
        if (depth > 8)
        {
            return null;
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var name in new[] { "title", "name", "summary" })
            {
                if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                {
                    var title = value.GetString()?.Trim();
                    if (!string.IsNullOrWhiteSpace(title) && title.Length <= 300)
                    {
                        return title;
                    }
                }
            }

            foreach (var property in element.EnumerateObject())
            {
                var title = FindTitle(property.Value, depth + 1);
                if (title is not null)
                {
                    return title;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray().Take(20))
            {
                var title = FindTitle(child, depth + 1);
                if (title is not null)
                {
                    return title;
                }
            }
        }
        return null;
    }

    private static string? GetProjectName(string path)
    {
        var segments = path.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < segments.Length - 1; index++)
        {
            if (segments[index].Equals("projects", StringComparison.OrdinalIgnoreCase))
            {
                return segments[index + 1];
            }
        }

        var markerIndex = Array.FindIndex(segments, segment =>
            segment.Equals("chats", StringComparison.OrdinalIgnoreCase) ||
            segment.Equals("agent-transcripts", StringComparison.OrdinalIgnoreCase) ||
            segment.Equals("agentTranscripts", StringComparison.OrdinalIgnoreCase));
        return markerIndex > 0 ? segments[markerIndex - 1] : null;
    }

    private static string BuildFallbackTitle(string? projectName, DateTime timestamp)
    {
        var prefix = string.IsNullOrWhiteSpace(projectName) ? "Cursor session" : projectName;
        return $"{prefix} - {timestamp.ToLocalTime():yyyy-MM-dd HH:mm}";
    }
}
