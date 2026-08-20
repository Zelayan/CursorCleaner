using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using CursorCleaner.Helpers;
using CursorCleaner.Models;

namespace CursorCleaner.Services;

public interface ICursorScannerService
{
    IAsyncEnumerable<ScanItem> ScanItemsAsync(
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<ScanResult> ScanAsync(
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

public sealed class CursorScannerService : ICursorScannerService
{
    private static readonly HashSet<string> SQLiteExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".vscdb", ".db", ".sqlite"
    };

    private readonly ICursorPathService _pathService;

    public CursorScannerService(ICursorPathService pathService)
    {
        _pathService = pathService ?? throw new ArgumentNullException(nameof(pathService));
    }

    public async IAsyncEnumerable<ScanItem> ScanItemsAsync(
        IProgress<ScanProgress>? progress = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var scanCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = scanCancellation.Token;
        var channel = Channel.CreateBounded<ScanItem>(new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true
        });
        var state = new ScanState();
        var producer = Task.Run(async () =>
        {
            try
            {
                await foreach (var item in ScanCoreAsync(state, progress, token).ConfigureAwait(false))
                {
                    await channel.Writer.WriteAsync(item, token).ConfigureAwait(false);
                }
                channel.Writer.TryComplete();
            }
            catch (Exception ex)
            {
                channel.Writer.TryComplete(ex);
            }
        }, CancellationToken.None);

        try
        {
            await foreach (var item in channel.Reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                yield return item;
            }
            await producer.ConfigureAwait(false);
        }
        finally
        {
            scanCancellation.Cancel();
            try
            {
                await producer.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (scanCancellation.IsCancellationRequested)
            {
            }
        }
    }

    public Task<ScanResult> ScanAsync(
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => ScanAggregatedAsync(progress, cancellationToken), cancellationToken);

    private async Task<ScanResult> ScanAggregatedAsync(
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        var state = new ScanState();
        var started = Stopwatch.StartNew();
        var items = new List<ScanItem>();
        var counts = Enum.GetValues<DataCategory>().ToDictionary(category => category, _ => 0L);
        var sizes = Enum.GetValues<DataCategory>().ToDictionary(category => category, _ => 0L);
        var largest = new PriorityQueue<LargeFileInfo, long>();

        await foreach (var item in ScanCoreAsync(state, progress, cancellationToken))
        {
            items.Add(item);
            counts[item.Category]++;
            sizes[item.Category] += item.Size;

            var largeFile = new LargeFileInfo(item.FullPath, item.Size, item.Category, item.LastWriteTimeUtc);
            largest.Enqueue(largeFile, item.Size);
            if (largest.Count > 50)
            {
                largest.Dequeue();
            }
        }

        var largestFiles = new List<LargeFileInfo>(largest.Count);
        while (largest.TryDequeue(out var item, out _))
        {
            largestFiles.Add(item);
        }
        largestFiles.Reverse();

        var summary = new ScanSummary(
            state.FilesScanned,
            state.BytesScanned,
            counts,
            sizes,
            largestFiles,
            state.ErrorCount,
            started.Elapsed);
        return new ScanResult(items, summary, DateTime.UtcNow);
    }

    public static DataCategory Classify(string path)
    {
        var extension = Path.GetExtension(path);
        if (SQLiteExtensions.Contains(extension) || IsSQLiteSidecar(path))
        {
            return DataCategory.SQLite;
        }

        var segments = path.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment =>
                segment.Equals("workspaceStorage", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals("workspaces", StringComparison.OrdinalIgnoreCase)) ||
            Path.GetFileName(path).Equals("workspace.json", StringComparison.OrdinalIgnoreCase))
        {
            return DataCategory.Workspace;
        }

        if (IsJsonSessionExtension(extension) &&
            HasSegment(segments, "agent-transcripts", "agentTranscripts"))
        {
            return DataCategory.AgentTranscript;
        }

        if (IsJsonSessionExtension(extension) &&
            HasSegment(segments, "chats", "sessions") &&
            !HasSegment(segments, "node_modules", "mcps", "canvases"))
        {
            return DataCategory.ChatSession;
        }

        return DataCategory.Other;
    }

    private static bool IsJsonSessionExtension(string extension) =>
        extension.Equals(".json", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".jsonl", StringComparison.OrdinalIgnoreCase);

    private static bool HasSegment(string[] segments, params string[] names) =>
        segments.Any(segment => names.Any(name => segment.Equals(name, StringComparison.OrdinalIgnoreCase)));

    private static bool IsSkippedScanDirectory(string path)
    {
        var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return name.Equals("node_modules", StringComparison.OrdinalIgnoreCase);
    }

    private async IAsyncEnumerable<ScanItem> ScanCoreAsync(
        ScanState state,
        IProgress<ScanProgress>? progress,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var roots = GetNonOverlappingRoots();
        var progressClock = Stopwatch.StartNew();
        var lastProgress = TimeSpan.Zero;

        for (var rootIndex = 0; rootIndex < roots.Count; rootIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var root = roots[rootIndex];
            if (!TryOpenRoot(root.Path, state))
            {
                ReportProgress(progress, state, null, rootIndex + 1, roots.Count);
                continue;
            }

            var pending = new Stack<string>();
            pending.Push(root.Path);
            while (pending.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var directory = pending.Pop();
                IEnumerator<string>? entries;
                try
                {
                    entries = Directory.EnumerateFileSystemEntries(directory).GetEnumerator();
                }
                catch (Exception ex) when (IsFileSystemException(ex))
                {
                    state.ErrorCount++;
                    continue;
                }

                using (entries)
                {
                    while (true)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        string path;
                        try
                        {
                            if (!entries.MoveNext())
                            {
                                break;
                            }
                            path = entries.Current;
                        }
                        catch (Exception ex) when (IsFileSystemException(ex))
                        {
                            state.ErrorCount++;
                            break;
                        }

                        FileAttributes attributes;
                        try
                        {
                            attributes = File.GetAttributes(path);
                        }
                        catch (Exception ex) when (IsFileSystemException(ex))
                        {
                            state.ErrorCount++;
                            continue;
                        }

                        if ((attributes & FileAttributes.ReparsePoint) != 0)
                        {
                            continue;
                        }

                        if ((attributes & FileAttributes.Directory) != 0)
                        {
                            if (IsSkippedScanDirectory(path))
                            {
                                continue;
                            }

                            pending.Push(path);
                            continue;
                        }

                        ScanItem item;
                        try
                        {
                            var info = new FileInfo(path);
                            item = new ScanItem(
                                info.FullName,
                                Path.GetRelativePath(root.Path, info.FullName),
                                root,
                                Classify(info.FullName),
                                info.Length,
                                info.LastWriteTimeUtc,
                                attributes);
                        }
                        catch (Exception ex) when (IsFileSystemException(ex))
                        {
                            state.ErrorCount++;
                            continue;
                        }

                        state.FilesScanned++;
                        state.BytesScanned += item.Size;
                        if (progressClock.Elapsed - lastProgress >= TimeSpan.FromMilliseconds(200))
                        {
                            ReportProgress(progress, state, item.FullPath, rootIndex, roots.Count);
                            lastProgress = progressClock.Elapsed;
                        }

                        yield return item;
                        if ((state.FilesScanned & 255) == 0)
                        {
                            await Task.Yield();
                        }
                    }
                }
            }

            ReportProgress(progress, state, null, rootIndex + 1, roots.Count);
        }
    }

    private IReadOnlyList<CursorDataRoot> GetNonOverlappingRoots()
    {
        var candidates = _pathService.GetDataRoots()
            .Select(root => root with { Path = PathSafety.Normalize(root.Path) })
            .OrderBy(root => root.Path.Length)
            .ToList();
        var roots = new List<CursorDataRoot>();
        foreach (var candidate in candidates)
        {
            if (!roots.Any(root => PathSafety.IsWithin(candidate.Path, root.Path)))
            {
                roots.Add(candidate);
            }
        }
        return roots;
    }

    private static bool TryOpenRoot(string path, ScanState state)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.Directory) == 0)
            {
                state.ErrorCount++;
                return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
        catch (Exception ex) when (IsFileSystemException(ex))
        {
            state.ErrorCount++;
            return false;
        }
    }

    private static bool IsSQLiteSidecar(string path)
    {
        var fileName = Path.GetFileName(path);
        foreach (var suffix in new[] { "-wal", "-shm", "-journal" })
        {
            if (!fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var baseName = fileName[..^suffix.Length];
            return SQLiteExtensions.Contains(Path.GetExtension(baseName));
        }
        return false;
    }

    private static bool IsFileSystemException(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or System.Security.SecurityException or
            NotSupportedException or ArgumentException;

    private static void ReportProgress(
        IProgress<ScanProgress>? progress,
        ScanState state,
        string? currentPath,
        int rootsCompleted,
        int totalRoots) =>
        progress?.Report(new ScanProgress(
            state.FilesScanned,
            state.BytesScanned,
            currentPath,
            rootsCompleted,
            totalRoots));

    private sealed class ScanState
    {
        public long FilesScanned { get; set; }
        public long BytesScanned { get; set; }
        public int ErrorCount { get; set; }
    }
}
