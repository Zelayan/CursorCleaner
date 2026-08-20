using System.IO;
using System.Text;
using System.Text.Json;
using CursorCleaner.Models;
using Microsoft.Data.Sqlite;

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

    private readonly ILogService? _log;

    public SessionAnalyzerService(ILogService? log = null)
    {
        _log = log;
    }

    public Task<IReadOnlyList<SessionInfo>> AnalyzeAsync(
        ScanResult scanResult,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scanResult);
        return Task.Run<IReadOnlyList<SessionInfo>>(
            () => Analyze(scanResult.Items, cancellationToken),
            cancellationToken);
    }

    private IReadOnlyList<SessionInfo> Analyze(
        IEnumerable<ScanItem> items,
        CancellationToken cancellationToken)
    {
        var itemList = items as IReadOnlyList<ScanItem> ?? items.ToArray();
        var fileSessions = new List<SessionInfo>();
        foreach (var item in itemList)
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
            var conversationIds = TryReadConversationIds(item.FullPath);
            fileSessions.Add(new SessionInfo(
                Path.GetFileNameWithoutExtension(item.FullPath),
                item.FullPath,
                title,
                projectName,
                item.Category,
                item.Size,
                item.LastWriteTimeUtc,
                SessionSource.File,
                null,
                conversationIds));
        }

        var composers = CatalogComposers(itemList, cancellationToken);
        return Merge(fileSessions, composers)
            .OrderByDescending(session => session.LastWriteTimeUtc)
            .ToArray();
    }

    private IReadOnlyList<CursorChatSchema.ComposerRecord> CatalogComposers(
        IEnumerable<ScanItem> items,
        CancellationToken cancellationToken)
    {
        var composers = new List<CursorChatSchema.ComposerRecord>();
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item.Category != DataCategory.SQLite || !CursorChatSchema.IsChatDatabaseName(item.FullPath))
            {
                continue;
            }

            try
            {
                using var connection = new SqliteConnection(CursorChatSchema.ReadOnlyConnectionString(item.FullPath));
                connection.Open();
                var shape = CursorChatSchema.DiscoverAsync(connection, cancellationToken).GetAwaiter().GetResult();
                if (!shape.IsRecognized)
                {
                    continue;
                }

                composers.AddRange(CursorChatSchema.ListComposersAsync(connection, item.FullPath, shape, cancellationToken).GetAwaiter().GetResult());
            }
            catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
            {
                try
                {
                    _log?.WriteAsync("warning", "session.sqlite.catalog", ex.Message, item.FullPath, ex, cancellationToken).GetAwaiter().GetResult();
                }
                catch
                {
                }
            }
        }

        return composers;
    }

    private static IReadOnlyList<SessionInfo> Merge(
        IReadOnlyList<SessionInfo> fileSessions,
        IReadOnlyList<CursorChatSchema.ComposerRecord> composers)
    {
        var uniqueComposers = new Dictionary<string, CursorChatSchema.ComposerRecord>(StringComparer.OrdinalIgnoreCase);
        foreach (var composer in composers)
        {
            if (uniqueComposers.TryGetValue(composer.ComposerId, out var existing))
            {
                uniqueComposers[composer.ComposerId] = existing with
                {
                    Title = PreferTitle(existing.Title, composer.Title, existing.ProjectName),
                    ProjectName = existing.ProjectName ?? composer.ProjectName,
                    LastWriteTimeUtc = existing.LastWriteTimeUtc > composer.LastWriteTimeUtc
                        ? existing.LastWriteTimeUtc
                        : composer.LastWriteTimeUtc
                };
                continue;
            }

            uniqueComposers[composer.ComposerId] = composer;
        }

        var merged = new List<SessionInfo>(fileSessions);
        var claimed = new HashSet<int>();
        foreach (var composer in uniqueComposers.Values)
        {
            var index = -1;
            for (var i = 0; i < merged.Count; i++)
            {
                if (claimed.Contains(i))
                {
                    continue;
                }

                if (MatchesComposer(merged[i], composer.ComposerId))
                {
                    index = i;
                    break;
                }
            }

            if (index >= 0)
            {
                claimed.Add(index);
                var existing = merged[index];
                var ids = existing.DeletableConversationIds.Append(composer.ComposerId)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                merged[index] = existing with
                {
                    Title = PreferTitle(existing.Title, composer.Title, existing.ProjectName),
                    ProjectName = existing.ProjectName ?? composer.ProjectName,
                    Source = SessionSource.Both,
                    DatabasePath = composer.DatabasePath,
                    ConversationIds = ids,
                    LastWriteTimeUtc = existing.LastWriteTimeUtc > composer.LastWriteTimeUtc
                        ? existing.LastWriteTimeUtc
                        : composer.LastWriteTimeUtc
                };
                continue;
            }

            merged.Add(new SessionInfo(
                composer.ComposerId,
                string.Empty,
                composer.Title,
                composer.ProjectName,
                DataCategory.ChatSession,
                0,
                composer.LastWriteTimeUtc,
                SessionSource.Database,
                composer.DatabasePath,
                [composer.ComposerId]));
        }

        return merged;
    }

    private static bool MatchesComposer(SessionInfo session, string composerId) =>
        session.Id.Equals(composerId, StringComparison.OrdinalIgnoreCase) ||
        session.DeletableConversationIds.Contains(composerId, StringComparer.OrdinalIgnoreCase);

    private static string PreferTitle(string current, string candidate, string? projectName)
    {
        if (string.IsNullOrWhiteSpace(current) || current.StartsWith("Cursor session - ", StringComparison.Ordinal))
        {
            return candidate;
        }

        if (!string.IsNullOrWhiteSpace(projectName) &&
            current.StartsWith(projectName + " - ", StringComparison.Ordinal) &&
            !candidate.StartsWith(projectName + " - ", StringComparison.Ordinal))
        {
            return candidate;
        }

        return current;
    }

    private static IReadOnlyList<string>? TryReadConversationIds(string path)
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

            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (Path.GetExtension(path).Equals(".jsonl", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var line in text.Split('\n').Take(MaximumLines))
                {
                    CollectIds(line.Trim(), ids);
                }
            }
            else
            {
                CollectIds(text, ids);
            }

            return ids.Count == 0 ? null : ids.ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or DecoderFallbackException)
        {
            return null;
        }
    }

    private static void CollectIds(string json, HashSet<string> ids)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
            foreach (var id in CursorChatSchema.CollectJsonConversationIds(document.RootElement))
            {
                ids.Add(id);
            }
        }
        catch (JsonException)
        {
        }
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
