using CursorCleaner.Models;
using CursorCleaner.Services;

namespace CursorCleaner.Tests;

[TestClass]
public sealed class SessionContentServiceTests
{
    [TestMethod]
    public async Task ReadAsync_ParsesCursorJsonlUserQueryAndAssistantText()
    {
        using var temp = new TemporaryDirectory();
        var root = Path.Combine(temp.Path, "Cursor");
        var transcripts = Path.Combine(root, "projects", "demo", "agent-transcripts");
        Directory.CreateDirectory(transcripts);
        var path = Path.Combine(transcripts, "chat.jsonl");
        await File.WriteAllTextAsync(path,
            """
            {"role":"user","message":{"content":[{"type":"text","text":"<user_query>\n测试\n</user_query>\n<timestamp>2026-01-01T00:00:00Z</timestamp>"}]}}
            {"role":"assistant","message":{"content":[{"type":"text","text":"收到，我这边正常"}]}}
            {"type":"meta","title":"ignored"}
            """);
        var service = new SessionContentService(new PathGuard([root]));

        var preview = await service.ReadAsync(path);

        Assert.IsNull(preview.Error);
        Assert.AreEqual(2, preview.Messages.Count);
        Assert.AreEqual("user", preview.Messages[0].Role);
        Assert.AreEqual("用户", preview.Messages[0].DisplayRole);
        Assert.AreEqual("测试", preview.Messages[0].Text);
        Assert.AreEqual("assistant", preview.Messages[1].Role);
        Assert.AreEqual("助手", preview.Messages[1].DisplayRole);
        Assert.AreEqual("收到，我这边正常", preview.Messages[1].Text);
        Assert.IsFalse(preview.Truncated);
    }

    [TestMethod]
    public async Task ReadAsync_ParsesNestedJsonMessages()
    {
        using var temp = new TemporaryDirectory();
        var root = Path.Combine(temp.Path, "Cursor");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "session.json");
        await File.WriteAllTextAsync(path,
            """
            {"messages":[{"role":"human","content":"hello"},{"role":"ai","content":[{"text":"world"}]}]}
            """);
        var service = new SessionContentService(new PathGuard([root]));

        var preview = await service.ReadAsync(path);

        Assert.IsNull(preview.Error);
        CollectionAssert.AreEqual(new[] { "human", "ai" }, preview.Messages.Select(message => message.Role).ToArray());
        CollectionAssert.AreEqual(new[] { "hello", "world" }, preview.Messages.Select(message => message.Text).ToArray());
    }

    [TestMethod]
    public async Task ReadAsync_RejectsPathOutsideTrustedRoots()
    {
        using var temp = new TemporaryDirectory();
        var root = Path.Combine(temp.Path, "Cursor");
        Directory.CreateDirectory(root);
        var outside = Path.Combine(temp.Path, "outside.jsonl");
        await File.WriteAllTextAsync(outside, """{"role":"user","text":"secret"}""");
        var service = new SessionContentService(new PathGuard([root]));

        var preview = await service.ReadAsync(outside);

        Assert.AreEqual(0, preview.Messages.Count);
        Assert.IsNotNull(preview.Error);
        StringAssert.Contains(preview.Error, "受信任");
    }

    [TestMethod]
    public async Task ReadAsync_RejectsUnsupportedExtension()
    {
        using var temp = new TemporaryDirectory();
        var root = Path.Combine(temp.Path, "Cursor");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "notes.txt");
        await File.WriteAllTextAsync(path, "not a session");
        var service = new SessionContentService(new PathGuard([root]));

        var preview = await service.ReadAsync(path);

        Assert.AreEqual(0, preview.Messages.Count);
        StringAssert.Contains(preview.Error, "JSON");
    }

    [TestMethod]
    public async Task ReadAsync_TruncatesAfterMaximumMessages()
    {
        using var temp = new TemporaryDirectory();
        var root = Path.Combine(temp.Path, "Cursor");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "long.jsonl");
        var lines = Enumerable.Range(0, 90).Select(index => $"{{\"role\":\"user\",\"text\":\"m{index}\"}}");
        await File.WriteAllLinesAsync(path, lines);
        var service = new SessionContentService(new PathGuard([root]));

        var preview = await service.ReadAsync(path);

        Assert.IsNull(preview.Error);
        Assert.AreEqual(80, preview.Messages.Count);
        Assert.IsTrue(preview.Truncated);
        Assert.AreEqual("m0", preview.Messages[0].Text);
        Assert.AreEqual("m79", preview.Messages[^1].Text);
    }
}
