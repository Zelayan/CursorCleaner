using System.IO;

namespace CursorCleaner.Helpers;

public static class PathSafety
{
    public static StringComparer PathComparer { get; } = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    public static string Normalize(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath);
        return PathComparer.Equals(fullPath, root) ? fullPath : Path.TrimEndingDirectorySeparator(fullPath);
    }

    public static bool IsWithin(string candidate, string root, bool allowRoot = true)
    {
        var normalizedCandidate = Normalize(candidate);
        var normalizedRoot = Normalize(root);
        if (PathComparer.Equals(normalizedCandidate, normalizedRoot))
        {
            return allowRoot;
        }

        return normalizedCandidate.StartsWith(
            normalizedRoot + Path.DirectorySeparatorChar,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    public static bool TryGetSafePath(string root, string relativePath, out string fullPath)
    {
        fullPath = string.Empty;
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
        {
            return false;
        }

        try
        {
            var candidate = Normalize(Path.Combine(Normalize(root), relativePath));
            if (!IsWithin(candidate, root))
            {
                return false;
            }

            fullPath = candidate;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return false;
        }
    }
}
