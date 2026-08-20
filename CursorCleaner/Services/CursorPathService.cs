using System.IO;
using CursorCleaner.Helpers;
using CursorCleaner.Models;

namespace CursorCleaner.Services;

public interface ICursorPathService
{
    IReadOnlyList<CursorDataRoot> GetDataRoots();
}

public sealed class CursorPathService : ICursorPathService
{
    private readonly string _roamingData;
    private readonly string _localData;
    private readonly string _userProfile;
    private readonly bool _probeCompatibilityRoots;

    public CursorPathService()
        : this(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            true)
    {
    }

    public CursorPathService(
        string roamingData,
        string localData,
        string userProfile,
        bool probeCompatibilityRoots = false)
    {
        _roamingData = PathSafety.Normalize(roamingData);
        _localData = PathSafety.Normalize(localData);
        _userProfile = PathSafety.Normalize(userProfile);
        _probeCompatibilityRoots = probeCompatibilityRoots;
    }

    public IReadOnlyList<CursorDataRoot> GetDataRoots()
    {
        var candidates = new List<CursorDataRoot>
        {
            Create(_roamingData, "Cursor", RootKind.RoamingData, "Cursor roaming data"),
            Create(_localData, "Cursor", RootKind.LocalData, "Cursor local data"),
            Create(_userProfile, ".cursor", RootKind.UserProfile, "Cursor user data")
        };

        if (_probeCompatibilityRoots)
        {
            AddIfExisting(candidates, _roamingData, "Cursor - Insiders", "Cursor Insiders roaming data");
            AddIfExisting(candidates, _localData, "Cursor - Insiders", "Cursor Insiders local data");
            AddIfExisting(candidates, _userProfile, ".cursor-insiders", "Cursor Insiders user data");
        }

        var result = new List<CursorDataRoot>();
        var seen = new HashSet<string>(PathSafety.PathComparer);
        foreach (var candidate in candidates)
        {
            var normalized = PathSafety.Normalize(candidate.Path);
            if (seen.Add(normalized))
            {
                result.Add(candidate with { Path = normalized });
            }
        }

        return result;
    }

    private static CursorDataRoot Create(
        string parent,
        string child,
        RootKind kind,
        string displayName)
    {
        if (!PathSafety.TryGetSafePath(parent, child, out var path))
        {
            throw new InvalidOperationException($"Invalid Cursor data root: {child}");
        }

        return new CursorDataRoot(path, kind, displayName);
    }

    private static void AddIfExisting(
        ICollection<CursorDataRoot> roots,
        string parent,
        string child,
        string displayName)
    {
        var root = Create(parent, child, RootKind.Compatibility, displayName);
        if (Directory.Exists(root.Path))
        {
            roots.Add(root);
        }
    }
}
