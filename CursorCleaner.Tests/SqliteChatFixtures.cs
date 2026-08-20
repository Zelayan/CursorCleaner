using Microsoft.Data.Sqlite;

namespace CursorCleaner.Tests;

internal static class SqliteChatFixtures
{
    public const string KeepId = "488ef4de-7b32-4b7c-b7be-6b67203f8717";
    public const string ExtraId = "dbe365e0-620f-4277-9f42-ab778a5749d9";

    public static async Task CreateStateDatabaseAsync(
        string path,
        string keepId = KeepId,
        string? extraId = ExtraId)
    {
        var builder = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false };
        await using var connection = new SqliteConnection(builder.ToString());
        await connection.OpenAsync();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                CREATE TABLE ItemTable (key TEXT UNIQUE ON CONFLICT REPLACE, value BLOB);
                CREATE TABLE cursorDiskKV (key TEXT UNIQUE ON CONFLICT REPLACE, value BLOB);
                CREATE TABLE composerHeaders (
                    composerId TEXT PRIMARY KEY,
                    workspaceId TEXT,
                    createdAt INTEGER,
                    lastUpdatedAt INTEGER,
                    isArchived INTEGER,
                    isSubagent INTEGER,
                    recency INTEGER,
                    checkpointAt INTEGER,
                    value TEXT);
                """;
            await command.ExecuteNonQueryAsync();
        }

        await InsertComposerAsync(connection, keepId, "Greeting conversation", extraId is null);
        if (extraId is not null)
        {
            await InsertComposerAsync(connection, extraId, "Capabilities inquiry", selected: false);
        }

        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                INSERT INTO ItemTable(key, value) VALUES ('workbench.view.search.state', 'keep-me');
                INSERT INTO cursorDiskKV(key, value) VALUES
                    ('agentKv:blob:keep', 'blob'),
                    ('composer.composerHeaders.migratedToTable', '1');
                INSERT INTO composerHeaders(composerId, workspaceId, createdAt, lastUpdatedAt, isArchived, isSubagent, recency, value)
                VALUES ('empty-state-draft', 'empty-window', 1787197810364, 1787197810448, 0, 0, 1787197810448,
                        '{"type":"head","composerId":"empty-state-draft","isDraft":true}');
                """;
            await command.ExecuteNonQueryAsync();
        }
    }

    public static async Task CreateSearchDatabaseAsync(
        string path,
        string keepId = KeepId,
        string extraId = ExtraId)
    {
        var builder = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false };
        await using var connection = new SqliteConnection(builder.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE conversations (
                fts_rowid INTEGER PRIMARY KEY,
                source TEXT NOT NULL CHECK (source IN ('local', 'cloud-cache')),
                scope TEXT NOT NULL,
                id TEXT NOT NULL,
                title TEXT NOT NULL,
                branches TEXT NOT NULL,
                updated_at INTEGER NOT NULL,
                is_archived INTEGER NOT NULL,
                root_fingerprint TEXT,
                cache_fingerprint TEXT,
                UNIQUE(source, scope, id)
            );
            CREATE VIRTUAL TABLE conversation_fts USING fts5(title, body, branches, tokenize = 'unicode61 remove_diacritics 2');
            CREATE TABLE conversation_search_candidates (id TEXT PRIMARY KEY, updated_at INTEGER NOT NULL);

            INSERT INTO conversations(fts_rowid, source, scope, id, title, branches, updated_at, is_archived, root_fingerprint)
            VALUES
                (1, 'local', '', @keep, 'Greeting conversation', '', 1787205014582, 0, 'aaaa'),
                (2, 'local', '', @extra, 'Capabilities inquiry', '', 1787211584274, 0, 'bbbb');
            INSERT INTO conversation_fts(rowid, title, body, branches) VALUES
                (1, 'Greeting conversation', 'hello keep', ''),
                (2, 'Capabilities inquiry', 'hello extra', '');
            INSERT INTO conversation_search_candidates(id, updated_at) VALUES
                (@keep, 1787205014582),
                (@extra, 1787211584274);
            """;
        command.Parameters.AddWithValue("@keep", keepId);
        command.Parameters.AddWithValue("@extra", extraId);
        await command.ExecuteNonQueryAsync();
    }

    public static async Task CreateUnknownSchemaAsync(string path)
    {
        var builder = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false };
        await using var connection = new SqliteConnection(builder.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE widgets (id INTEGER PRIMARY KEY, name TEXT); INSERT INTO widgets(name) VALUES ('keep');";
        await command.ExecuteNonQueryAsync();
    }

    private static async Task InsertComposerAsync(SqliteConnection connection, string id, string name, bool selected)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO composerHeaders(composerId, workspaceId, createdAt, lastUpdatedAt, isArchived, isSubagent, recency, value)
            VALUES (@id, 'empty-window', 1787205014252, 1787205014582, 0, 0, 1787205014582, @value);
            INSERT INTO cursorDiskKV(key, value) VALUES
                (@composerData, @payload),
                (@bubble, '{}'),
                (@checkpoint, '{}');
            INSERT INTO ItemTable(key, value) VALUES (@panel, '{}');
            """;
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@value", $$"""{"type":"head","composerId":"{{id}}","name":"{{name}}"}""");
        command.Parameters.AddWithValue("@payload", $$"""{"composerId":"{{id}}"}""");
        command.Parameters.AddWithValue("@composerData", "composerData:" + id);
        command.Parameters.AddWithValue("@bubble", "bubbleId:" + id + ":00f2ba3c-209b-4bad-a719-785490ddbb17");
        command.Parameters.AddWithValue("@checkpoint", "checkpointId:" + id + ":5f58e845-2a1c-45c8-8505-ee3c75312044");
        command.Parameters.AddWithValue("@panel", "glass/cursor.editorPanelVisibility.agent/" + id);
        await command.ExecuteNonQueryAsync();

        await using var composerData = connection.CreateCommand();
        composerData.CommandText = "SELECT value FROM ItemTable WHERE key = 'composer.composerData' LIMIT 1;";
        var existing = await composerData.ExecuteScalarAsync() as string;
        var ids = new List<string>();
        if (!string.IsNullOrWhiteSpace(existing) && existing.Contains("allComposers", StringComparison.Ordinal))
        {
            foreach (var token in new[] { KeepId, ExtraId, id })
            {
                if (existing.Contains(token, StringComparison.OrdinalIgnoreCase) && !ids.Contains(token, StringComparer.OrdinalIgnoreCase))
                {
                    ids.Add(token);
                }
            }
        }

        if (!ids.Contains(id, StringComparer.OrdinalIgnoreCase))
        {
            ids.Add(id);
        }

        var selectedId = selected ? id : ids[0];
        var json = "{\"allComposers\":[" + string.Join(",", ids.Select(item => "{\"composerId\":\"" + item + "\"}")) + "],\"selectedComposerId\":\"" + selectedId + "\"}";
        await using var upsert = connection.CreateCommand();
        upsert.CommandText = "INSERT INTO ItemTable(key, value) VALUES ('composer.composerData', @value) ON CONFLICT(key) DO UPDATE SET value = @value;";
        upsert.Parameters.AddWithValue("@value", json);
        await upsert.ExecuteNonQueryAsync();
    }
}
