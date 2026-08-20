using CursorCleaner.Models;
using CursorCleaner.Services;

namespace CursorCleaner.Tests;

[TestClass]
public sealed class ScannerTests
{
    [TestMethod]
    public async Task ScanAsync_MissingRoots_ReturnsEmptyResult()
    {
        using var temp = new TemporaryDirectory();
        var pathService = new CursorPathService(
            Path.Combine(temp.Path, "missing-roaming"),
            Path.Combine(temp.Path, "missing-local"),
            Path.Combine(temp.Path, "missing-profile"));

        var result = await new CursorScannerService(pathService).ScanAsync();

        Assert.AreEqual(0, result.Summary.TotalFiles);
        Assert.AreEqual(0, result.Summary.TotalBytes);
        Assert.AreEqual(0, result.Items.Count);
    }

    [DataTestMethod]
    [DataRow("globalStorage/state.vscdb", DataCategory.SQLite)]
    [DataRow("globalStorage/state.vscdb-wal", DataCategory.SQLite)]
    [DataRow("workspaceStorage/abc/workspace.json", DataCategory.Workspace)]
    [DataRow("agent-transcripts/run.jsonl", DataCategory.AgentTranscript)]
    [DataRow("projects/demo/agent-transcripts/run.jsonl", DataCategory.AgentTranscript)]
    [DataRow("projects/demo/chats/chat.json", DataCategory.ChatSession)]
    [DataRow("projects/demo/sessions/one.json", DataCategory.ChatSession)]
    [DataRow("projects/demo/notes.json", DataCategory.Other)]
    [DataRow("projects/empty-window/mcps/cursor-ide-browser/tools/browser_lock.json", DataCategory.Other)]
    [DataRow("projects/empty-window/mcps/cursor-app-control/tools/cursor_dialog.json", DataCategory.Other)]
    [DataRow("projects/empty-window/canvases/node_modules/cursor/package.json", DataCategory.Other)]
    [DataRow("projects/empty-window/canvases/tsconfig.json", DataCategory.Other)]
    [DataRow("logs/window.log", DataCategory.Other)]
    public void Classify_UsesExclusivePriority(string path, DataCategory expected)
    {
        Assert.AreEqual(expected, CursorScannerService.Classify(path));
    }

    [TestMethod]
    public async Task ScanAsync_TracksTop50AndDoesNotDoubleCountOverlappingRoots()
    {
        using var temp = new TemporaryDirectory();
        var root = Path.Combine(temp.Path, "Cursor");
        Directory.CreateDirectory(root);
        for (var index = 1; index <= 60; index++)
        {
            await File.WriteAllBytesAsync(Path.Combine(root, $"file-{index:D2}.bin"), new byte[index]);
        }

        var roots = new StaticPathService(
            new CursorDataRoot(root, RootKind.RoamingData, "root"),
            new CursorDataRoot(Path.Combine(root, "nested"), RootKind.Compatibility, "nested"));
        Directory.CreateDirectory(Path.Combine(root, "nested"));
        await File.WriteAllBytesAsync(Path.Combine(root, "nested", "one.bin"), [1]);

        var result = await new CursorScannerService(roots).ScanAsync();

        Assert.AreEqual(61, result.Summary.TotalFiles);
        Assert.AreEqual(50, result.Summary.LargestFiles.Count);
        Assert.AreEqual(60, result.Summary.LargestFiles[0].Size);
        Assert.AreEqual(11, result.Summary.LargestFiles[^1].Size);
        Assert.AreEqual(result.Items.Sum(item => item.Size), result.Summary.TotalBytes);
    }

    [TestMethod]
    public async Task ScanAsync_SkipsDirectoryReparsePoint_WhenSupported()
    {
        using var temp = new TemporaryDirectory();
        var root = Path.Combine(temp.Path, "Cursor");
        var target = Path.Combine(temp.Path, "outside");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(target);
        await File.WriteAllTextAsync(Path.Combine(target, "outside.db"), "data");
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(root, "link"), target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            Assert.Inconclusive("Directory symbolic links are unavailable in this environment.");
        }

        var result = await new CursorScannerService(
            new StaticPathService(new CursorDataRoot(root, RootKind.RoamingData, "root"))).ScanAsync();

        Assert.AreEqual(0, result.Summary.TotalFiles);
    }

    [TestMethod]
    public async Task ScanAsync_SkipsNodeModulesAndDoesNotTreatMcpToolsAsSessions()
    {
        using var temp = new TemporaryDirectory();
        var root = Path.Combine(temp.Path, ".cursor");
        var project = Path.Combine(root, "projects", "empty-window");
        var transcripts = Path.Combine(project, "agent-transcripts");
        var chats = Path.Combine(project, "chats");
        var mcp = Path.Combine(project, "mcps", "cursor-ide-browser", "tools");
        var vendor = Path.Combine(project, "canvases", "node_modules", "cursor");
        Directory.CreateDirectory(transcripts);
        Directory.CreateDirectory(chats);
        Directory.CreateDirectory(mcp);
        Directory.CreateDirectory(vendor);
        await File.WriteAllTextAsync(Path.Combine(transcripts, "run.jsonl"), "{\"role\":\"user\",\"text\":\"hi\"}");
        await File.WriteAllTextAsync(Path.Combine(chats, "chat.json"), "{\"title\":\"Fix indexing\"}");
        await File.WriteAllTextAsync(Path.Combine(mcp, "browser_lock.json"), "{\"name\":\"browser_lock\"}");
        await File.WriteAllTextAsync(Path.Combine(vendor, "package.json"), "{\"name\":\"cursor\"}");

        var result = await new CursorScannerService(
            new StaticPathService(new CursorDataRoot(root, RootKind.UserProfile, "user"))).ScanAsync();

        CollectionAssert.AreEquivalent(
            new[] { DataCategory.AgentTranscript, DataCategory.ChatSession, DataCategory.Other },
            result.Items.Select(item => item.Category).ToArray());
        Assert.IsFalse(result.Items.Any(item => item.FullPath.Contains("node_modules", StringComparison.OrdinalIgnoreCase)));
        Assert.AreEqual(1, result.Items.Count(item => item.Category == DataCategory.AgentTranscript));
        Assert.AreEqual(1, result.Items.Count(item => item.Category == DataCategory.ChatSession));
        Assert.AreEqual("browser_lock.json", Path.GetFileName(result.Items.Single(item => item.Category == DataCategory.Other).FullPath));
    }

    [TestMethod]
    public async Task ScanAsync_FileRootIsCountedAsErrorNotMissing()
    {
        using var temp = new TemporaryDirectory();
        var root = Path.Combine(temp.Path, "Cursor");
        await File.WriteAllTextAsync(root, "not-a-directory");

        var result = await new CursorScannerService(
            new StaticPathService(new CursorDataRoot(root, RootKind.RoamingData, "root"))).ScanAsync();

        Assert.AreEqual(0, result.Summary.TotalFiles);
        Assert.AreEqual(1, result.Summary.ErrorCount);
    }

    private sealed class StaticPathService(params CursorDataRoot[] roots) : ICursorPathService
    {
        public IReadOnlyList<CursorDataRoot> GetDataRoots() => roots;
    }
}

internal sealed class TemporaryDirectory : IDisposable
{
    public TemporaryDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "CursorCleaner.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
