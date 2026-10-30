using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace Erkmangelis.Ek;

public enum ChangeKind
{
    /// <summary>The template added the file and the project did not have it.</summary>
    Added,

    /// <summary>The project had the template's old file unchanged; it now has the new one.</summary>
    Updated,

    /// <summary>The template removed a file the project had unchanged.</summary>
    Removed,

    /// <summary>Both the project and the template changed the file, in different places.</summary>
    Merged,

    /// <summary>Both changed the same lines. The file now has conflict markers to resolve by hand.</summary>
    Conflict,

    /// <summary>The template removed or replaced a file the project changed; the project's file stays.</summary>
    Kept,

    /// <summary>The project deleted a file the template changed; it stays deleted.</summary>
    StaysDeleted,
}

/// <summary>One file the update changes or reports. <see cref="Content"/> is null when the file is deleted or left alone.</summary>
public sealed record FileChange(string Path, ChangeKind Kind, byte[]? Content, UnixFileMode? Mode = null)
{
    public bool NeedsReview => Kind is ChangeKind.Conflict or ChangeKind.Kept or ChangeKind.StaysDeleted;
}

/// <summary>A generated file: its bytes and, on Unix, its permissions.</summary>
public sealed record GeneratedFile(byte[] Content, UnixFileMode? Mode = null);

/// <summary>
/// The Copier-style update: the difference between the project generated at the old version and at
/// the new version is applied to the real project. A file the project never changed takes the new
/// version; a file both changed is merged line by line, and overlapping changes get conflict markers.
/// Nothing the project changed is ever overwritten.
/// </summary>
public static partial class ThreeWayMerge
{
    public delegate (byte[] Merged, int Conflicts) MergeText(byte[] ours, byte[] baseline, byte[] theirs);

    public static IReadOnlyList<FileChange> Plan(
        IReadOnlyDictionary<string, GeneratedFile> before,
        IReadOnlyDictionary<string, GeneratedFile> after,
        Func<string, byte[]?> project,
        MergeText merge)
    {
        var changes = new List<FileChange>();
        foreach (var path in before.Keys.Union(after.Keys).Order(StringComparer.Ordinal))
        {
            var old = before.GetValueOrDefault(path);
            var @new = after.GetValueOrDefault(path);
            if (Same(old?.Content, @new?.Content))
            {
                continue;
            }

            var current = project(path);
            if (Same(current, old?.Content))
            {
                changes.Add(@new is null
                    ? new FileChange(path, ChangeKind.Removed, null)
                    : new FileChange(path, old is null ? ChangeKind.Added : ChangeKind.Updated, @new.Content, @new.Mode));
            }
            else if (Same(current, @new?.Content))
            {
                // The project already has the new version.
            }
            else if (current is null)
            {
                changes.Add(new FileChange(path, ChangeKind.StaysDeleted, null));
            }
            else if (@new is null || IsBinary(current) || IsBinary(@new.Content) || (old is not null && IsBinary(old.Content)))
            {
                changes.Add(new FileChange(path, ChangeKind.Kept, null));
            }
            else
            {
                var (merged, conflicts) = merge(current, old?.Content ?? [], @new.Content);
                changes.Add(new FileChange(path, conflicts == 0 ? ChangeKind.Merged : ChangeKind.Conflict, merged));
            }
        }

        return changes;
    }

    /// <summary>
    /// <c>dotnet new</c> gives every generation fresh GUIDs (the user secrets ids). Before comparing,
    /// each fresh GUID in a generated tree is replaced with the one the project has in the same
    /// place, so only real template changes show up.
    /// </summary>
    public static void AlignGuids(Dictionary<string, GeneratedFile> before, Dictionary<string, GeneratedFile> after, Func<string, byte[]?> project)
    {
        var beforeGuids = Guids(before);
        var afterGuids = Guids(after);
        Align(before, beforeGuids.Except(afterGuids), project);
        Align(after, afterGuids.Except(beforeGuids), project);
    }

    public static bool IsBinary(byte[] content) => Array.IndexOf(content, (byte)0) >= 0;

    private static void Align(Dictionary<string, GeneratedFile> tree, IEnumerable<string> fresh, Func<string, byte[]?> project)
    {
        var replacements = new Dictionary<string, string>();
        foreach (var guid in fresh)
        {
            foreach (var (path, file) in tree.Where(pair => !IsBinary(pair.Value.Content)))
            {
                var text = Encoding.UTF8.GetString(file.Content);
                var line = text.Split('\n').FirstOrDefault(candidate => candidate.Contains(guid, StringComparison.Ordinal));
                if (line is null || project(path) is not { } current)
                {
                    continue;
                }

                var pattern = Regex.Escape(line.TrimEnd('\r')).Replace(Regex.Escape(guid), $"({GuidPattern().ToString()})", StringComparison.Ordinal);
                var match = Regex.Match(Encoding.UTF8.GetString(current), pattern);
                if (match.Success)
                {
                    replacements[guid] = match.Groups[1].Value;
                    break;
                }
            }
        }

        foreach (var path in tree.Keys.ToList())
        {
            var file = tree[path];
            if (IsBinary(file.Content))
            {
                continue;
            }

            var text = Encoding.UTF8.GetString(file.Content);
            var aligned = replacements.Aggregate(text, (current, pair) => current.Replace(pair.Key, pair.Value, StringComparison.Ordinal));
            if (aligned != text)
            {
                tree[path] = file with { Content = Encoding.UTF8.GetBytes(aligned) };
            }
        }
    }

    private static HashSet<string> Guids(Dictionary<string, GeneratedFile> tree) =>
        [.. tree.Values.Where(file => !IsBinary(file.Content))
            .SelectMany(file => GuidPattern().Matches(Encoding.UTF8.GetString(file.Content)).Select(match => match.Value))];

    private static bool Same(byte[]? left, byte[]? right) =>
        left is null ? right is null : right is not null && left.AsSpan().SequenceEqual(right);

    [GeneratedRegex("[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}")]
    private static partial Regex GuidPattern();

    /// <summary>A line merge with <c>git merge-file</c>, labelling the sides for the conflict markers.</summary>
    public static MergeText GitMergeFile(string oldVersion, string newVersion) => (ours, baseline, theirs) =>
    {
        var directory = Directory.CreateTempSubdirectory("ek-merge-").FullName;
        try
        {
            var files = new[] { ("ours", ours), ("base", baseline), ("theirs", theirs) }
                .Select(pair =>
                {
                    var path = Path.Combine(directory, pair.Item1);
                    File.WriteAllBytes(path, pair.Item2);
                    return path;
                })
                .ToArray();
            var start = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true };
            string[] arguments = ["merge-file", "-p", "-L", "your project", "-L", $"template {oldVersion}", "-L", $"template {newVersion}", .. files];
            foreach (var argument in arguments)
            {
                start.ArgumentList.Add(argument);
            }

            using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start git.");
            using var output = new MemoryStream();
            process.StandardOutput.BaseStream.CopyTo(output);
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();

            // git merge-file exits with the number of conflicts, or a negative number (255) on error.
            if (process.ExitCode >= 128)
            {
                throw new InvalidOperationException($"git merge-file failed: {error}");
            }

            return (output.ToArray(), process.ExitCode);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    };
}
