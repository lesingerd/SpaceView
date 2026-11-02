using System.IO;

namespace SpaceView.Core;

/// <summary>The outcome of checking a set of sources against a destination folder (see <see cref="MoveRules.Validate"/>).</summary>
public sealed class MoveValidation
{
    /// <summary>The paths that will really be moved: de-duplicated, without descendants of other sources and without no-ops.</summary>
    public required IReadOnlyList<string> Sources { get; init; }

    /// <summary>Sources that already are directly inside the destination (left out).</summary>
    public required IReadOnlyList<string> NoOps { get; init; }

    /// <summary>Why nothing can be moved; null if <see cref="Sources"/> is worth moving.</summary>
    public string? Error { get; init; }

    public bool IsValid => Error == null && Sources.Count > 0;
}

/// <summary>One folder whose view of the tree must be brought up to date after a move.</summary>
public readonly record struct RefreshStep(string Path, bool IsRoot);

/// <summary>
/// The pure decisions behind multi-row move and cut: which selected items are moved, whether a destination is allowed
/// and which folders must be refreshed afterwards. Works on path strings only (no file system access, no UI), so it can
/// be tested on its own.
/// </summary>
public static class MoveRules
{
    /// <summary>A path in comparison form: backslashes, no trailing separator (except for a drive root such as <c>X:\</c>).</summary>
    public static string Normalize(string path)
    {
        string p = path.Replace('/', '\\');
        if (p.Length == 2 && p[1] == ':')
            return p + "\\"; // "X:" is the current folder of drive X for Windows, but here it always means the root

        string root = Path.GetPathRoot(p) ?? string.Empty;
        if (p.Length <= root.Length)
            return p;

        string trimmed = p.TrimEnd('\\');
        return trimmed.Length < root.Length ? root : trimmed;
    }

    /// <summary>True for a drive root such as <c>X:\</c> (or a UNC share root).</summary>
    public static bool IsRoot(string path)
    {
        string p = Normalize(path);
        string? root = Path.GetPathRoot(p);
        return root != null && string.Equals(root, p, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The folder that contains the path; null for a root.</summary>
    public static string? GetParent(string path)
    {
        string p = Normalize(path);
        if (IsRoot(p))
            return null;

        string? parent = Path.GetDirectoryName(p);
        return parent == null ? null : Normalize(parent);
    }

    /// <summary>The leaf name of a path (empty for a root).</summary>
    public static string GetName(string path) => IsRoot(path) ? string.Empty : Path.GetFileName(Normalize(path));

    public static bool SamePath(string a, string b) =>
        string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>True if <paramref name="path"/> is <paramref name="ancestor"/> itself or lies anywhere below it.</summary>
    public static bool IsSameOrInside(string path, string ancestor)
    {
        string p = Normalize(path);
        string a = Normalize(ancestor);
        if (string.Equals(p, a, StringComparison.OrdinalIgnoreCase))
            return true;

        string prefix = a.EndsWith('\\') ? a : a + "\\";
        return p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True if <paramref name="path"/> lies below <paramref name="ancestor"/> (not the same path).</summary>
    public static bool IsInside(string path, string ancestor) => !SamePath(path, ancestor) && IsSameOrInside(path, ancestor);

    /// <summary>True if the two paths are on different drives (a move between them is a copy followed by a delete).</summary>
    public static bool CrossesDrives(string source, string destinationFolder) =>
        !string.Equals(Path.GetPathRoot(Normalize(source)), Path.GetPathRoot(Normalize(destinationFolder)), StringComparison.OrdinalIgnoreCase);

    /// <summary>Removes duplicates and every path that lies inside another path of the list (a folder and its descendant: the descendant goes along with the folder). Keeps the order.</summary>
    public static List<string> FilterDescendants(IEnumerable<string> paths)
    {
        var distinct = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in paths)
        {
            string p = Normalize(path);
            if (seen.Add(p))
                distinct.Add(p);
        }

        var result = new List<string>(distinct.Count);
        foreach (string p in distinct)
        {
            bool covered = false;
            foreach (string other in distinct)
            {
                if (IsInside(p, other))
                {
                    covered = true;
                    break;
                }
            }
            if (!covered)
                result.Add(p);
        }
        return result;
    }

    /// <summary>
    /// Checks the sources against the destination: drive roots cannot be moved, a folder cannot go into itself or one of
    /// its own subfolders, and an item that is already directly inside the destination is a no-op. The returned sources
    /// are what is left to move.
    /// </summary>
    public static MoveValidation Validate(IEnumerable<string> sources, string destinationFolder)
    {
        string dest = Normalize(destinationFolder);
        List<string> top = FilterDescendants(sources);
        var noOps = new List<string>();
        var toMove = new List<string>();

        if (top.Count == 0)
            return new MoveValidation { Sources = toMove, NoOps = noOps, Error = "Nothing to move." };

        foreach (string source in top)
        {
            if (IsRoot(source))
                return new MoveValidation { Sources = toMove, NoOps = noOps, Error = $"A drive ({source}) cannot be moved." };

            if (IsSameOrInside(dest, source))
            {
                return new MoveValidation
                {
                    Sources = toMove,
                    NoOps = noOps,
                    Error = SamePath(dest, source)
                        ? $"Cannot move \"{GetName(source)}\" into itself."
                        : $"Cannot move \"{GetName(source)}\" into one of its own subfolders."
                };
            }

            string? parent = GetParent(source);
            if (parent != null && SamePath(parent, dest))
                noOps.Add(source);
            else
                toMove.Add(source);
        }

        if (toMove.Count == 0)
        {
            return new MoveValidation
            {
                Sources = toMove,
                NoOps = noOps,
                Error = noOps.Count == 1
                    ? $"\"{GetName(noOps[0])}\" is already in that folder."
                    : "The items are already in that folder."
            };
        }

        return new MoveValidation { Sources = toMove, NoOps = noOps };
    }

    /// <summary>
    /// The folders to bring up to date after items were moved: each distinct parent of the moved items, then the
    /// destination. Folders inside another folder of the plan are dropped (refreshing the outer one covers them) and so
    /// are duplicates. A drive root is never dropped for the sake of another folder and never covers one: a root is
    /// updated by a delta (<see cref="RefreshStep.IsRoot"/>), not by rescanning it, so the caller must not treat it as a branch.
    /// Sources come first and the destination last, so a moved item is gone from its old place before it is added at its new one.
    /// </summary>
    public static List<RefreshStep> PlanRefresh(IEnumerable<string> movedSources, string destinationFolder)
    {
        var candidates = new List<string>();
        foreach (string source in movedSources)
        {
            string? parent = GetParent(source);
            if (parent != null)
                candidates.Add(parent);
        }
        candidates.Add(Normalize(destinationFolder));

        var distinct = new List<string>();
        foreach (string c in candidates)
        {
            string p = Normalize(c);
            if (!distinct.Any(d => SamePath(d, p)))
                distinct.Add(p);
        }

        var plan = new List<RefreshStep>();
        foreach (string p in distinct)
        {
            if (IsRoot(p))
            {
                plan.Add(new RefreshStep(p, IsRoot: true));
                continue;
            }

            bool covered = distinct.Any(other => !IsRoot(other) && IsInside(p, other));
            if (!covered)
                plan.Add(new RefreshStep(p, IsRoot: false));
        }
        return plan;
    }
}
