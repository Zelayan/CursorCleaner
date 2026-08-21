using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using CursorCleaner.Models;
using Microsoft.Data.Sqlite;

namespace CursorCleaner.Services;

internal static class CursorChatSchema
{
    internal const string EmptyStateDraftId = "empty-state-draft";

    internal sealed record DatabaseShape(
        bool ComposerHeaders,
        bool CursorDiskKv,
        bool ItemTable,
        bool Conversations,
        bool ConversationSearchCandidates,
        bool ConversationFts,
        bool ConversationsHasFtsRowId,
        bool LegacyComposerData)
    {
        public bool IsRecognized =>
            ComposerHeaders || CursorDiskKv || Conversations || LegacyComposerData;
    }

    internal sealed record ComposerRecord(
        string ComposerId,
        string Title,
        string? ProjectName,
        DateTime LastWriteTimeUtc,
        string DatabasePath);

    public static string ReadOnlyConnectionString(string path) => new SqliteConnectionStringBuilder
    {
        DataSource = path,
        Mode = SqliteOpenMode.ReadOnly,
        Cache = SqliteCacheMode.Private,
        Pooling = false
    }.ToString();

    public static string ReadWriteConnectionString(string path) => new SqliteConnectionStringBuilder
    {
        DataSource = path,
        Mode = SqliteOpenMode.ReadWrite,
        Cache = SqliteCacheMode.Private,
        Pooling = false
    }.ToString();

    public static bool IsChatDatabaseName(string path)
    {
        var name = Path.GetFileName(path);
        return name.Equals("state.vscdb", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("conversation-search.db", StringComparison.OrdinalIgnoreCase);
    }

    public static async Task<DatabaseShape> DiscoverAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table';";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!reader.IsDBNull(0))
                {
                    tables.Add(reader.GetString(0));
                }
            }
        }

        var composerHeaders = tables.Contains("composerHeaders") &&
                               await HasColumnsAsync(connection, "composerHeaders", cancellationToken, "composerId");
        var cursorDiskKv = tables.Contains("cursorDiskKV") &&
                           await HasColumnsAsync(connection, "cursorDiskKV", cancellationToken, "key");
        var itemTable = tables.Contains("ItemTable") &&
                        await HasColumnsAsync(connection, "ItemTable", cancellationToken, "key", "value");
        var conversations = tables.Contains("conversations") &&
                            await HasColumnsAsync(connection, "conversations", cancellationToken, "id");
        var candidates = tables.Contains("conversation_search_candidates") &&
                         await HasColumnsAsync(connection, "conversation_search_candidates", cancellationToken, "id");
        var fts = tables.Contains("conversation_fts");
        var ftsRowId = conversations &&
                       await HasColumnsAsync(connection, "conversations", cancellationToken, "fts_rowid");
        var legacy = itemTable && await HasLegacyComposerDataAsync(connection, cancellationToken).ConfigureAwait(false);
        return new DatabaseShape(composerHeaders, cursorDiskKv, itemTable, conversations, candidates, fts, ftsRowId, legacy);
    }

    public static async Task<bool> HasChatDataAsync(
        SqliteConnection connection,
        DatabaseShape shape,
        CancellationToken cancellationToken)
    {
        if (shape.LegacyComposerData)
        {
            return true;
        }

        if (shape.ComposerHeaders && await CountAsync(connection, "SELECT COUNT(*) FROM composerHeaders;", cancellationToken).ConfigureAwait(false) > 0)
        {
            return true;
        }

        if (shape.Conversations && await CountAsync(connection, "SELECT COUNT(*) FROM conversations;", cancellationToken).ConfigureAwait(false) > 0)
        {
            return true;
        }

        if (shape.CursorDiskKv &&
            await CountAsync(
                connection,
                """
                SELECT COUNT(*) FROM cursorDiskKV
                WHERE key LIKE 'composerData:%'
                   OR key LIKE 'bubbleId:%'
                   OR key LIKE 'checkpointId:%'
                   OR key LIKE 'composerChat:%';
                """,
                cancellationToken).ConfigureAwait(false) > 0)
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Lightweight existence check used before online backup so unrelated chat DBs are not copied.
    /// </summary>
    public static async Task<bool> HasMatchingConversationAsync(
        SqliteConnection connection,
        DatabaseShape shape,
        IReadOnlyList<string> conversationIds,
        CancellationToken cancellationToken)
    {
        if (conversationIds.Count == 0)
        {
            return false;
        }

        if (shape.ComposerHeaders &&
            await AnyByIdAsync(connection, "composerHeaders", "composerId", conversationIds, cancellationToken).ConfigureAwait(false))
        {
            return true;
        }

        if (shape.Conversations &&
            await AnyByIdAsync(connection, "conversations", "id", conversationIds, cancellationToken).ConfigureAwait(false))
        {
            return true;
        }

        if (shape.ConversationSearchCandidates &&
            await AnyByIdAsync(connection, "conversation_search_candidates", "id", conversationIds, cancellationToken).ConfigureAwait(false))
        {
            return true;
        }

        if (shape.CursorDiskKv &&
            await AnyKeyedChatRowsAsync(connection, "cursorDiskKV", conversationIds, cancellationToken).ConfigureAwait(false))
        {
            return true;
        }

        if (shape.ItemTable &&
            await AnyKeyedChatRowsAsync(connection, "ItemTable", conversationIds, cancellationToken).ConfigureAwait(false))
        {
            return true;
        }

        if (shape.LegacyComposerData &&
            await LegacyComposerDataContainsAsync(connection, conversationIds, cancellationToken).ConfigureAwait(false))
        {
            return true;
        }

        return false;
    }

    public static async Task<IReadOnlyList<ComposerRecord>> ListComposersAsync(
        SqliteConnection connection,
        string databasePath,
        DatabaseShape shape,
        CancellationToken cancellationToken)
    {
        var records = new Dictionary<string, ComposerRecord>(StringComparer.OrdinalIgnoreCase);
        if (shape.ComposerHeaders)
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT composerId, workspaceId, createdAt, lastUpdatedAt, recency, isSubagent, value FROM composerHeaders;";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var id = reader.IsDBNull(0) ? null : reader.GetString(0);
                if (!SqliteConversationId.IsMatch(id) ||
                    string.Equals(id, EmptyStateDraftId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!reader.IsDBNull(5) && IsTruthy(reader.GetValue(5)))
                {
                    continue;
                }

                var workspace = reader.IsDBNull(1) ? null : reader.GetString(1);
                var timestamp = FirstTimestamp(reader, 4, 3, 2);
                var title = TryReadComposerTitle(ReadOptionalString(reader, 6)) ?? BuildFallbackTitle(workspace, timestamp, id);
                records[id!] = new ComposerRecord(id!, title, workspace, timestamp, databasePath);
            }
        }

        if (shape.Conversations)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = shape.ConversationsHasFtsRowId
                ? "SELECT id, title, updated_at FROM conversations;"
                : "SELECT id, title, updated_at FROM conversations;";
            try
            {
                await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var id = reader.IsDBNull(0) ? null : reader.GetString(0);
                    if (!SqliteConversationId.IsMatch(id))
                    {
                        continue;
                    }

                    var title = reader.IsDBNull(1) ? null : reader.GetString(1);
                    var timestamp = UnixMilliseconds(ReadOptionalInt64(reader, 2));
                    if (records.TryGetValue(id!, out var existing))
                    {
                        if (!string.IsNullOrWhiteSpace(title) &&
                            (string.IsNullOrWhiteSpace(existing.Title) || existing.Title.StartsWith(existing.ProjectName + " - ", StringComparison.Ordinal)))
                        {
                            records[id!] = existing with { Title = title!.Trim() };
                        }

                        continue;
                    }

                    records[id!] = new ComposerRecord(
                        id!,
                        string.IsNullOrWhiteSpace(title) ? BuildFallbackTitle(null, timestamp, id) : title.Trim(),
                        null,
                        timestamp,
                        databasePath);
                }
            }
            catch (SqliteException)
            {
                // conversation-search.db without a title/updated_at column is still handled by deletes.
            }
        }

        return records.Values.ToArray();
    }

    public static async Task<int> DeleteAsync(
        SqliteConnection connection,
        DatabaseShape shape,
        IReadOnlyList<string> conversationIds,
        CancellationToken cancellationToken)
    {
        if (conversationIds.Count == 0)
        {
            return 0;
        }

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var deleted = 0;
        try
        {
            if (shape.ComposerHeaders)
            {
                deleted += await DeleteByIdAsync(connection, "composerHeaders", "composerId", conversationIds, cancellationToken).ConfigureAwait(false);
            }

            if (shape.CursorDiskKv)
            {
                deleted += await DeleteKeyedChatRowsAsync(connection, "cursorDiskKV", conversationIds, cancellationToken).ConfigureAwait(false);
            }

            if (shape.ItemTable)
            {
                deleted += await DeleteKeyedChatRowsAsync(connection, "ItemTable", conversationIds, cancellationToken).ConfigureAwait(false);
                deleted += await UpdateLegacyComposerDataAsync(connection, conversationIds, cancellationToken).ConfigureAwait(false);
            }

            if (shape.Conversations)
            {
                if (shape.ConversationFts && shape.ConversationsHasFtsRowId)
                {
                    deleted += await DeleteConversationFtsAsync(connection, conversationIds, cancellationToken).ConfigureAwait(false);
                }

                deleted += await DeleteByIdAsync(connection, "conversations", "id", conversationIds, cancellationToken).ConfigureAwait(false);
            }

            if (shape.ConversationSearchCandidates)
            {
                deleted += await DeleteByIdAsync(connection, "conversation_search_candidates", "id", conversationIds, cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        return deleted;
    }

    public static IReadOnlyList<string> CollectJsonConversationIds(JsonElement element) =>
        CollectJsonConversationIds(element, 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    private static IEnumerable<string> CollectJsonConversationIds(JsonElement element, int depth)
    {
        if (depth > 8)
        {
            yield break;
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var name in new[] { "composerId", "conversationId", "sessionId", "chatId" })
            {
                if (element.TryGetProperty(name, out var value) &&
                    value.ValueKind == JsonValueKind.String &&
                    SqliteConversationId.IsMatch(value.GetString()))
                {
                    yield return value.GetString()!;
                }
            }

            foreach (var property in element.EnumerateObject())
            {
                foreach (var id in CollectJsonConversationIds(property.Value, depth + 1))
                {
                    yield return id;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray().Take(20))
            {
                foreach (var id in CollectJsonConversationIds(child, depth + 1))
                {
                    yield return id;
                }
            }
        }
    }

    private static async Task<bool> HasLegacyComposerDataAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM ItemTable WHERE key = 'composer.composerData' LIMIT 1;";
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is not null and not DBNull;
    }

    private static async Task<long> CountAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is long number ? number : Convert.ToInt64(value);
    }

    private static async Task<bool> HasColumnsAsync(
        SqliteConnection connection,
        string table,
        CancellationToken cancellationToken,
        params string[] required)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info(\"{table.Replace("\"", "\"\"", StringComparison.Ordinal)}\");";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!reader.IsDBNull(1))
            {
                columns.Add(reader.GetString(1));
            }
        }

        return required.All(columns.Contains);
    }

    private static async Task<int> DeleteByIdAsync(
        SqliteConnection connection,
        string table,
        string column,
        IReadOnlyList<string> ids,
        CancellationToken cancellationToken)
    {
        var deleted = 0;
        foreach (var id in ids)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"DELETE FROM \"{table.Replace("\"", "\"\"", StringComparison.Ordinal)}\" WHERE \"{column.Replace("\"", "\"\"", StringComparison.Ordinal)}\" = @id;";
            command.Parameters.AddWithValue("@id", id);
            deleted += await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        return deleted;
    }

    private static async Task<bool> AnyByIdAsync(
        SqliteConnection connection,
        string table,
        string column,
        IReadOnlyList<string> ids,
        CancellationToken cancellationToken)
    {
        foreach (var id in ids)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var command = connection.CreateCommand();
            command.CommandText =
                $"SELECT 1 FROM \"{table.Replace("\"", "\"\"", StringComparison.Ordinal)}\" WHERE \"{column.Replace("\"", "\"\"", StringComparison.Ordinal)}\" = @id LIMIT 1;";
            command.Parameters.AddWithValue("@id", id);
            var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (value is not null and not DBNull)
            {
                return true;
            }
        }

        return false;
    }

    private static async Task<bool> AnyKeyedChatRowsAsync(
        SqliteConnection connection,
        string table,
        IReadOnlyList<string> ids,
        CancellationToken cancellationToken)
    {
        var quoted = table.Replace("\"", "\"\"", StringComparison.Ordinal);
        foreach (var id in ids)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var command = connection.CreateCommand();
            command.CommandText =
                $"""
                 SELECT 1 FROM "{quoted}"
                 WHERE key = @composerData
                    OR key = @composerChat
                    OR key = @itemComposerData
                    OR key = @editorPanel
                    OR key = @fullscreen
                    OR key LIKE @bubblePrefix ESCAPE '\'
                    OR key LIKE @checkpointPrefix ESCAPE '\'
                 LIMIT 1;
                 """;
            command.Parameters.AddWithValue("@composerData", "composerData:" + id);
            command.Parameters.AddWithValue("@composerChat", "composerChat:" + id);
            command.Parameters.AddWithValue("@itemComposerData", "composerData:" + id);
            command.Parameters.AddWithValue("@editorPanel", "glass/cursor.editorPanelVisibility.agent/" + id);
            command.Parameters.AddWithValue("@fullscreen", "cursor/glass.editorPanelFullscreen/" + id);
            command.Parameters.AddWithValue("@bubblePrefix", EscapeLike("bubbleId:" + id + ":") + "%");
            command.Parameters.AddWithValue("@checkpointPrefix", EscapeLike("checkpointId:" + id + ":") + "%");
            var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (value is not null and not DBNull)
            {
                return true;
            }
        }

        return false;
    }

    private static async Task<bool> LegacyComposerDataContainsAsync(
        SqliteConnection connection,
        IReadOnlyList<string> ids,
        CancellationToken cancellationToken)
    {
        await using var select = connection.CreateCommand();
        select.CommandText = "SELECT value FROM ItemTable WHERE key = 'composer.composerData' LIMIT 1;";
        var raw = await select.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (raw is null or DBNull)
        {
            return false;
        }

        var json = raw switch
        {
            string text => text,
            byte[] bytes => Encoding.UTF8.GetString(bytes),
            _ => raw.ToString()
        };
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var present = new HashSet<string>(
                CollectJsonConversationIds(document.RootElement),
                StringComparer.OrdinalIgnoreCase);
            return ids.Any(present.Contains);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static async Task<int> DeleteKeyedChatRowsAsync(
        SqliteConnection connection,
        string table,
        IReadOnlyList<string> ids,
        CancellationToken cancellationToken)
    {
        var quoted = table.Replace("\"", "\"\"", StringComparison.Ordinal);
        var deleted = 0;
        foreach (var id in ids)
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                $"""
                 DELETE FROM "{quoted}"
                 WHERE key = @composerData
                    OR key = @composerChat
                    OR key = @itemComposerData
                    OR key = @editorPanel
                    OR key = @fullscreen
                    OR key LIKE @bubblePrefix ESCAPE '\'
                    OR key LIKE @checkpointPrefix ESCAPE '\';
                 """;
            command.Parameters.AddWithValue("@composerData", "composerData:" + id);
            command.Parameters.AddWithValue("@composerChat", "composerChat:" + id);
            command.Parameters.AddWithValue("@itemComposerData", "composerData:" + id);
            command.Parameters.AddWithValue("@editorPanel", "glass/cursor.editorPanelVisibility.agent/" + id);
            command.Parameters.AddWithValue("@fullscreen", "cursor/glass.editorPanelFullscreen/" + id);
            command.Parameters.AddWithValue("@bubblePrefix", EscapeLike("bubbleId:" + id + ":") + "%");
            command.Parameters.AddWithValue("@checkpointPrefix", EscapeLike("checkpointId:" + id + ":") + "%");
            deleted += await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        return deleted;
    }

    private static async Task<int> DeleteConversationFtsAsync(
        SqliteConnection connection,
        IReadOnlyList<string> ids,
        CancellationToken cancellationToken)
    {
        var deleted = 0;
        foreach (var id in ids)
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                DELETE FROM conversation_fts
                WHERE rowid IN (SELECT fts_rowid FROM conversations WHERE id = @id);
                """;
            command.Parameters.AddWithValue("@id", id);
            deleted += await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        return deleted;
    }

    private static async Task<int> UpdateLegacyComposerDataAsync(
        SqliteConnection connection,
        IReadOnlyList<string> ids,
        CancellationToken cancellationToken)
    {
        await using var select = connection.CreateCommand();
        select.CommandText = "SELECT value FROM ItemTable WHERE key = 'composer.composerData' LIMIT 1;";
        var raw = await select.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (raw is null or DBNull)
        {
            return 0;
        }

        var json = raw switch
        {
            string text => text,
            byte[] bytes => Encoding.UTF8.GetString(bytes),
            _ => raw.ToString()
        };
        if (string.IsNullOrWhiteSpace(json))
        {
            return 0;
        }

        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return 0;
        }

        var remove = ids.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var changed = false;
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.NameEquals("allComposers") && property.Value.ValueKind == JsonValueKind.Array)
                {
                    writer.WritePropertyName(property.Name);
                    writer.WriteStartArray();
                    foreach (var item in property.Value.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.Object &&
                            item.TryGetProperty("composerId", out var composerId) &&
                            composerId.ValueKind == JsonValueKind.String &&
                            remove.Contains(composerId.GetString() ?? string.Empty))
                        {
                            changed = true;
                            continue;
                        }

                        item.WriteTo(writer);
                    }

                    writer.WriteEndArray();
                    continue;
                }

                if (property.NameEquals("selectedComposerId") &&
                    property.Value.ValueKind == JsonValueKind.String &&
                    remove.Contains(property.Value.GetString() ?? string.Empty))
                {
                    writer.WriteNull(property.Name);
                    changed = true;
                    continue;
                }

                property.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        if (!changed)
        {
            return 0;
        }

        await using var update = connection.CreateCommand();
        update.CommandText = "UPDATE ItemTable SET value = @value WHERE key = 'composer.composerData';";
        update.Parameters.AddWithValue("@value", Encoding.UTF8.GetString(stream.ToArray()));
        return await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);

    private static string? TryReadComposerTitle(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            foreach (var name in new[] { "name", "title", "subtitle" })
            {
                if (document.RootElement.TryGetProperty(name, out var value) &&
                    value.ValueKind == JsonValueKind.String)
                {
                    var title = value.GetString()?.Trim();
                    if (!string.IsNullOrWhiteSpace(title) && title.Length <= 300)
                    {
                        return title;
                    }
                }
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }

    private static string BuildFallbackTitle(string? projectName, DateTime timestamp, string? composerId = null)
    {
        var prefix = string.IsNullOrWhiteSpace(projectName) ? "Cursor session" : projectName;
        var title = $"{prefix} - {timestamp.ToLocalTime():yyyy-MM-dd HH:mm}";
        if (string.IsNullOrWhiteSpace(projectName) &&
            !string.IsNullOrWhiteSpace(composerId) &&
            composerId.Length >= 8)
        {
            title += $" · {composerId[..8]}";
        }

        return title;
    }

    private static DateTime FirstTimestamp(SqliteDataReader reader, params int[] ordinals)
    {
        foreach (var ordinal in ordinals)
        {
            var value = ReadOptionalInt64(reader, ordinal);
            if (value is > 0)
            {
                return UnixMilliseconds(value);
            }
        }

        return DateTime.UnixEpoch;
    }

    private static DateTime UnixMilliseconds(long? value)
    {
        if (value is null or <= 0)
        {
            return DateTime.UnixEpoch;
        }

        try
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(value.Value).UtcDateTime;
        }
        catch (ArgumentOutOfRangeException)
        {
            return DateTime.UnixEpoch;
        }
    }

    private static long? ReadOptionalInt64(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        var value = reader.GetValue(ordinal);
        return value switch
        {
            long number => number,
            int number => number,
            double number => (long)number,
            string text when long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null
        };
    }

    private static string? ReadOptionalString(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        var value = reader.GetValue(ordinal);
        return value switch
        {
            string text => text,
            byte[] bytes => Encoding.UTF8.GetString(bytes),
            _ => value.ToString()
        };
    }

    private static bool IsTruthy(object value) => value switch
    {
        bool flag => flag,
        long number => number != 0,
        int number => number != 0,
        string text => text is "1" or "true" or "True",
        _ => false
    };
}
