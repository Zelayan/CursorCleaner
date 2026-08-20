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
