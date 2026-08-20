using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CursorCleaner.Helpers;
using CursorCleaner.Models;

namespace CursorCleaner.Services;

public sealed class SessionContentService : ISessionContentService
{
    private const int MaximumBytes = 512 * 1024;
    private const int MaximumLines = 400;
    private const int MaximumMessages = 80;
    private const int MaximumMessageChars = 4_000;
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".json", ".jsonl"
    };
    private static readonly Regex UserQueryRegex = new(
        @"<user_query>\s*(.*?)\s*</user_query>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);
    private static readonly Regex TimestampRegex = new(
        @"<timestamp>.*?</timestamp>\s*",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);

    private readonly IPathGuard _pathGuard;

    public SessionContentService(IPathGuard pathGuard)
    {
        _pathGuard = pathGuard ?? throw new ArgumentNullException(nameof(pathGuard));
    }

    public Task<SessionContentPreview> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Task.Run(() => Read(path, cancellationToken), cancellationToken);
    }

    private SessionContentPreview Read(string path, CancellationToken cancellationToken)
    {
        var guard = _pathGuard.ValidateCleanupTarget(path, _pathGuard.CursorRoots);
        if (!guard.IsSafe)
        {
            return new SessionContentPreview(path, [], false, "会话文件不在受信任的 Cursor 数据根内。");
        }

        var normalized = guard.NormalizedPath!;
        if (!SupportedExtensions.Contains(Path.GetExtension(normalized)))
        {
            return new SessionContentPreview(normalized, [], false, "仅支持预览 JSON 或 JSONL 会话文件。");
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var stream = new FileStream(
                normalized,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                4096,
                FileOptions.SequentialScan);

            var messages = new List<SessionMessage>();
            var truncated = stream.Length > MaximumBytes;
            if (Path.GetExtension(normalized).Equals(".jsonl", StringComparison.OrdinalIgnoreCase))
            {
                truncated |= ReadJsonl(stream, messages, cancellationToken);
            }
            else
            {
                truncated |= ReadJson(stream, messages, cancellationToken);
            }

            return new SessionContentPreview(normalized, messages, truncated, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or DecoderFallbackException or ArgumentException)
        {
            return new SessionContentPreview(normalized, [], false, $"无法读取会话内容：{ex.Message}");
        }
    }

    private static bool ReadJsonl(Stream stream, List<SessionMessage> messages, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 4096, leaveOpen: false);
        var truncated = false;
        var lines = 0;
        while (reader.ReadLine() is { } line)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lines++;
            if (lines > MaximumLines)
            {
                truncated = true;
                break;
            }

            CollectMessages(line.Trim(), messages);
            if (messages.Count >= MaximumMessages)
            {
                truncated = true;
                break;
            }
        }

        return truncated || !reader.EndOfStream;
    }

    private static bool ReadJson(Stream stream, List<SessionMessage> messages, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
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

        var truncated = stream.Position < stream.Length || total >= MaximumBytes;
        if (total == 0)
        {
            return truncated;
        }

        using var document = JsonDocument.Parse(Encoding.UTF8.GetString(buffer, 0, total), new JsonDocumentOptions { MaxDepth = 32 });
        CollectFromElement(document.RootElement, messages, 0);
        if (messages.Count > MaximumMessages)
        {
            messages.RemoveRange(MaximumMessages, messages.Count - MaximumMessages);
            truncated = true;
        }

        return truncated;
    }

    private static void CollectMessages(string json, List<SessionMessage> messages)
    {
        if (string.IsNullOrWhiteSpace(json) || messages.Count >= MaximumMessages)
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
            CollectFromElement(document.RootElement, messages, 0);
        }
        catch (JsonException)
        {
        }
    }

    private static void CollectFromElement(JsonElement element, List<SessionMessage> messages, int depth)
    {
        if (depth > 10 || messages.Count >= MaximumMessages)
        {
            return;
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            if (TryCreateMessage(element) is { } message)
            {
                messages.Add(message);
                return;
            }

            foreach (var property in element.EnumerateObject())
            {
                CollectFromElement(property.Value, messages, depth + 1);
                if (messages.Count >= MaximumMessages)
                {
                    return;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray().Take(200))
            {
                CollectFromElement(child, messages, depth + 1);
                if (messages.Count >= MaximumMessages)
                {
                    return;
                }
            }
        }
    }

    private static SessionMessage? TryCreateMessage(JsonElement element)
    {
        var role = ReadRole(element);
        if (role is null)
        {
            return null;
        }

        var text = ExtractText(element);
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return new SessionMessage(role, DisplayRole(role), Truncate(CleanText(text)));
    }

    private static string? ReadRole(JsonElement element)
    {
        foreach (var name in new[] { "role", "type", "kind", "speaker" })
        {
            if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
            {
                var role = value.GetString()?.Trim();
                if (string.IsNullOrWhiteSpace(role))
                {
                    continue;
                }

                if (role.Equals("user", StringComparison.OrdinalIgnoreCase) ||
                    role.Equals("human", StringComparison.OrdinalIgnoreCase) ||
                    role.Equals("assistant", StringComparison.OrdinalIgnoreCase) ||
                    role.Equals("ai", StringComparison.OrdinalIgnoreCase) ||
                    role.Equals("model", StringComparison.OrdinalIgnoreCase) ||
                    role.Equals("system", StringComparison.OrdinalIgnoreCase))
                {
                    return role.ToLowerInvariant();
                }
            }
        }

        return null;
    }

    private static string ExtractText(JsonElement element)
    {
        if (element.TryGetProperty("message", out var message))
        {
            var nested = ExtractText(message);
            if (!string.IsNullOrWhiteSpace(nested))
            {
                return nested;
            }
        }

        foreach (var name in new[] { "text", "content", "value" })
        {
            if (!element.TryGetProperty(name, out var value))
            {
                continue;
            }

            var text = FlattenText(value, 0);
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text;
            }
        }

        return string.Empty;
    }

    private static string FlattenText(JsonElement element, int depth)
    {
        if (depth > 8)
        {
            return string.Empty;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                return element.GetString() ?? string.Empty;
            case JsonValueKind.Array:
                var parts = new List<string>();
                foreach (var child in element.EnumerateArray().Take(50))
                {
                    var text = FlattenText(child, depth + 1);
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        parts.Add(text);
                    }
                }

                return string.Join("\n", parts);
            case JsonValueKind.Object:
                if (element.TryGetProperty("text", out var textProperty) && textProperty.ValueKind == JsonValueKind.String)
                {
                    return textProperty.GetString() ?? string.Empty;
                }

                if (element.TryGetProperty("content", out var contentProperty))
                {
                    return FlattenText(contentProperty, depth + 1);
                }

                return string.Empty;
            default:
                return string.Empty;
        }
    }

    private static string CleanText(string text)
    {
        var match = UserQueryRegex.Match(text);
        if (match.Success)
        {
            text = match.Groups[1].Value;
        }

        text = TimestampRegex.Replace(text, string.Empty);
        return text.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
    }

    private static string Truncate(string text)
    {
        if (text.Length <= MaximumMessageChars)
        {
            return text;
        }

        return text[..MaximumMessageChars] + "\n…（内容已截断）";
    }

    private static string DisplayRole(string role) => role switch
    {
        "user" or "human" => "用户",
        "assistant" or "ai" or "model" => "助手",
        "system" => "系统",
        _ => role
    };
}
