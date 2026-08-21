using System.Text.Json;
using CursorCleaner.Models;
using CursorCleaner.Services;

namespace CursorCleaner.Tests;

[TestClass]
public sealed class AnalyzerTests
{
    [TestMethod]
    public async Task WorkspaceAnalyzer_ParsesFileUriAndMarksMissingProject()
    {
        using var temp = new TemporaryDirectory();
        var unit = Path.Combine(temp.Path, "workspaceStorage", "workspace-1");
        Directory.CreateDirectory(unit);
        var missingProject = Path.Combine(temp.Path, "missing-project");
        var workspaceJson = Path.Combine(unit, "workspace.json");
        await File.WriteAllTextAsync(
            workspaceJson,
            JsonSerializer.Serialize(new { folder = new Uri(missingProject).AbsoluteUri }));
        var item = CreateItem(workspaceJson, temp.Path, DataCategory.Workspace);
        var result = CreateResult([item]);

        var workspaces = await new WorkspaceAnalyzerService().AnalyzeAsync(result);

        Assert.AreEqual(1, workspaces.Count);
        Assert.AreEqual("workspace-1", workspaces[0].Id);
        Assert.AreEqual(Path.GetFullPath(missingProject), workspaces[0].ProjectPath);
        Assert.IsTrue(workspaces[0].ProjectMissing);
        Assert.AreEqual("missing-project", workspaces[0].DisplayName);
    }

    [TestMethod]
    public async Task SessionAnalyzer_ReadsJsonlTitleAndFallsBackForInvalidJson()
    {
        using var temp = new TemporaryDirectory();
        var chats = Path.Combine(temp.Path, "projects", "demo", "chats");
        Directory.CreateDirectory(chats);
        var titled = Path.Combine(chats, "one.jsonl");
        var invalid = Path.Combine(chats, "two.json");
        await File.WriteAllTextAsync(titled, "{\"type\":\"meta\",\"title\":\"Fix indexing\"}\n{\"text\":\"ignored\"}");
        await File.WriteAllTextAsync(invalid, "not json");
        File.SetLastWriteTimeUtc(invalid, new DateTime(2025, 1, 2, 3, 4, 0, DateTimeKind.Utc));
        var result = CreateResult([
            CreateItem(titled, temp.Path, DataCategory.ChatSession),
            CreateItem(invalid, temp.Path, DataCategory.ChatSession)
        ]);

        var sessions = await new SessionAnalyzerService().AnalyzeAsync(result);

        Assert.AreEqual(2, sessions.Count);
        Assert.AreEqual("Fix indexing", sessions.Single(session => session.Id == "one").Title);
        var fallback = sessions.Single(session => session.Id == "two");
        StringAssert.StartsWith(fallback.Title, "demo - ");
        Assert.AreEqual("demo", fallback.ProjectName);
    }

    [TestMethod]
    public async Task SessionAnalyzer_IgnoresNonSessionJsonEvenIfNamedLikeATool()
    {
        using var temp = new TemporaryDirectory();
        var tool = Path.Combine(temp.Path, "projects", "empty-window", "mcps", "tools", "browser_lock.json");
        Directory.CreateDirectory(Path.GetDirectoryName(tool)!);
        await File.WriteAllTextAsync(tool, """{"name":"browser_lock"}""");
        var result = CreateResult([CreateItem(tool, temp.Path, DataCategory.Other)]);

        var sessions = await new SessionAnalyzerService().AnalyzeAsync(result);

        Assert.AreEqual(0, sessions.Count);
    }

    [TestMethod]
    public async Task SessionAnalyzer_MergesSqliteComposerWithMatchingJsonl()
    {
        using var temp = new TemporaryDirectory();
        var chats = Path.Combine(temp.Path, "projects", "demo", "agent-transcripts");
        Directory.CreateDirectory(chats);
        var composerId = "488ef4de-7b32-4b7c-b7be-6b67203f8717";
        var transcript = Path.Combine(chats, composerId + ".jsonl");
        await File.WriteAllTextAsync(transcript, """{"role":"user","composerId":"488ef4de-7b32-4b7c-b7be-6b67203f8717","text":"hi"}""");
        var database = Path.Combine(temp.Path, "state.vscdb");
        await SqliteChatFixtures.CreateStateDatabaseAsync(database, keepId: composerId, extraId: "dbe365e0-620f-4277-9f42-ab778a5749d9");
        var result = CreateResult([
            CreateItem(transcript, temp.Path, DataCategory.AgentTranscript),
            CreateItem(database, temp.Path, DataCategory.SQLite)
        ]);

        var sessions = await new SessionAnalyzerService().AnalyzeAsync(result);

        Assert.AreEqual(2, sessions.Count);
        var merged = sessions.Single(session => session.Id == composerId);
        Assert.AreEqual(SessionSource.Both, merged.Source);
        Assert.AreEqual(transcript, merged.FilePath);
        Assert.AreEqual(database, merged.DatabasePath);
        Assert.AreEqual("Greeting conversation", merged.Title);
        Assert.IsTrue(sessions.Any(session => session.Id == "dbe365e0-620f-4277-9f42-ab778a5749d9" && session.Source == SessionSource.Database));
    }

    [TestMethod]
    public async Task SessionAnalyzer_ListsDatabaseOnlyComposerWithoutJsonl()
    {
        using var temp = new TemporaryDirectory();
        var database = Path.Combine(temp.Path, "state.vscdb");
        await SqliteChatFixtures.CreateStateDatabaseAsync(database, keepId: "488ef4de-7b32-4b7c-b7be-6b67203f8717", extraId: null);
        var result = CreateResult([CreateItem(database, temp.Path, DataCategory.SQLite)]);

        var sessions = await new SessionAnalyzerService().AnalyzeAsync(result);

        Assert.AreEqual(1, sessions.Count);
        Assert.AreEqual(SessionSource.Database, sessions[0].Source);
        Assert.AreEqual(string.Empty, sessions[0].FilePath);
        Assert.AreEqual("Greeting conversation", sessions[0].Title);
        Assert.AreEqual(0, sessions[0].Size);
        Assert.AreEqual("—", sessions[0].DisplaySizeText);
    }

    [TestMethod]
    public async Task SessionAnalyzer_PrefersStateDatabasePathWhenSameComposerAppearsInSearchDb()
    {
        using var temp = new TemporaryDirectory();
        var composerId = "488ef4de-7b32-4b7c-b7be-6b67203f8717";
        var search = Path.Combine(temp.Path, "conversation-search.db");
        var state = Path.Combine(temp.Path, "state.vscdb");
        await SqliteChatFixtures.CreateSearchDatabaseAsync(search, keepId: composerId, extraId: "dbe365e0-620f-4277-9f42-ab778a5749d9");
        await SqliteChatFixtures.CreateStateDatabaseAsync(state, keepId: composerId, extraId: null);
        // Search first so a naive merge would keep the weaker path without PreferDatabasePath.
        var result = CreateResult([
            CreateItem(search, temp.Path, DataCategory.SQLite),
            CreateItem(state, temp.Path, DataCategory.SQLite)
        ]);

        var sessions = await new SessionAnalyzerService().AnalyzeAsync(result);

        var merged = sessions.Single(session => session.Id == composerId);
        Assert.AreEqual(SessionSource.Database, merged.Source);
        Assert.AreEqual(state, merged.DatabasePath);
        CollectionAssert.AreEquivalent(new[] { state, search }, merged.AllDatabasePaths.ToArray());
        Assert.AreEqual("Greeting conversation", merged.Title);
        Assert.AreEqual("empty-window", merged.ProjectName);
        Assert.AreEqual("—", merged.DisplaySizeText);
        Assert.AreEqual(2, sessions.Count);
    }

    [TestMethod]
    public async Task SessionAnalyzer_FallbackTitleIncludesShortIdWhenNoProject()
    {
        using var temp = new TemporaryDirectory();
        var composerId = "29b8856f-d840-ef4f-362b-1103fa4e3c82";
        var search = Path.Combine(temp.Path, "conversation-search.db");
        await SqliteChatFixtures.CreateSearchDatabaseAsync(search, keepId: composerId, extraId: "58e2baaf-bd49-71e2-ef52-e76f27c4e8a2");
        // Clear titles so ListComposers falls back.
        var builder = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = search, Pooling = false };
        await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(builder.ToString()))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE conversations SET title = '';";
            await command.ExecuteNonQueryAsync();
        }

        var sessions = await new SessionAnalyzerService().AnalyzeAsync(CreateResult([CreateItem(search, temp.Path, DataCategory.SQLite)]));

        var first = sessions.Single(session => session.Id == composerId);
        StringAssert.Contains(first.Title, "Cursor session - ");
        StringAssert.Contains(first.Title, "29b8856f");
        Assert.AreEqual("—", first.DisplaySizeText);
    }

    [TestMethod]
    public void ScanResultStore_RaisesChangeAndKeepsLatest()
    {
        var store = new ScanResultStore();
        var result = CreateResult([]);
        var notifications = 0;
        store.Changed += (_, value) =>
        {
            notifications++;
            Assert.AreSame(result, value);
        };

        store.Set(result);

        Assert.AreSame(result, store.Latest);
        Assert.AreEqual(1, notifications);
    }

    private static ScanItem CreateItem(string path, string rootPath, DataCategory category)
    {
        var info = new FileInfo(path);
        return new ScanItem(
            info.FullName,
            Path.GetRelativePath(rootPath, path),
            new CursorDataRoot(rootPath, RootKind.RoamingData, "test"),
            category,
            info.Length,
            info.LastWriteTimeUtc,
            info.Attributes);
    }

    private static ScanResult CreateResult(IReadOnlyList<ScanItem> items)
    {
        var emptyCounts = Enum.GetValues<DataCategory>().ToDictionary(category => category, _ => 0L);
        return new ScanResult(
            items,
            new ScanSummary(items.Count, items.Sum(item => item.Size), emptyCounts, emptyCounts, [], 0, TimeSpan.Zero),
            DateTime.UtcNow);
    }
}
