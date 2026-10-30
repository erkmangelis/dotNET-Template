using System.Diagnostics;

namespace Erkmangelis.Ek;

public sealed record UpdateRequest(string ProjectDirectory, string? From, string? To, bool DryRun, bool AllowDirty);

public sealed record UpdateResult(string FromVersion, string ToVersion, IReadOnlyList<FileChange> Changes)
{
    public int Conflicts => Changes.Count(change => change.Kind == ChangeKind.Conflict);
}

/// <summary>
/// <c>ek update</c>: reads the project's template-origin.json, generates the project again at the
/// version it came from and at the new version (each in a private template hive), and three-way
/// merges the difference into the project.
/// </summary>
public sealed class ProjectUpdater(Catalog catalog, TemplatePackages packages, TextWriter log)
{
    public const string OriginFile = "template-origin.json";

    public async Task<UpdateResult> UpdateAsync(UpdateRequest request, CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(request.ProjectDirectory);
        var originPath = Path.Combine(root, OriginFile);
        if (!File.Exists(originPath))
        {
            throw new SpecException($"{root} has no {OriginFile}, so it was not generated from this template (or the file was deleted).");
        }

        var origin = ProjectSpec.Read(originPath);
        if (origin.Template is not null && !catalog.IsTemplate(origin.Template))
        {
            throw new SpecException($"{OriginFile} names template '{origin.Template}'; this tool updates {catalog.Template.Identity}.");
        }

        var name = origin.Name ?? InferName(root);
        var fromPackage = await packages.FindAsync(request.From ?? origin.Version
            ?? throw new SpecException($"{OriginFile} does not record a version; pass --from."), cancellationToken);
        var toPackage = request.To is not null ? await packages.FindAsync(request.To, cancellationToken)
            : packages.Bundled ?? throw new SpecException("This build of ek carries no template package; pass --to.");
        var fromVersion = packages.VersionOf(fromPackage);
        var toVersion = packages.VersionOf(toPackage);
        if (Compare(toVersion, fromVersion) < 0)
        {
            throw new SpecException($"The project is at {fromVersion}, newer than {toVersion}. ek update only moves forward.");
        }

        if (!request.AllowDirty)
        {
            RequireCleanGitTree(root);
        }

        // The old version takes exactly the options it recorded. The new one resolves them through
        // the current rules, so options added since then take their defaults.
        var known = origin.Options.Where(pair => catalog.FindOption(pair.Key) is not null).ToDictionary();
        foreach (var dropped in origin.Options.Keys.Except(known.Keys))
        {
            log.WriteLine($"warning: option '{dropped}' no longer exists in {toVersion}; it is dropped.");
        }

        var resolved = SpecResolver.Resolve(catalog, name, null, known);
        if (!resolved.Succeeded)
        {
            throw new SpecException($"The project's options are not supported by {toVersion}:\n  {string.Join("\n  ", resolved.Errors)}");
        }

        var work = Directory.CreateTempSubdirectory("ek-update-").FullName;
        try
        {
            log.WriteLine($"Generating {name} at {fromVersion} and {toVersion}");
            var before = await GenerateAsync(fromPackage, name, Path.Combine(work, "before"),
                origin.Options.SelectMany(pair => new[] { $"--{pair.Key}", pair.Value }), cancellationToken);
            var after = await GenerateAsync(toPackage, name, Path.Combine(work, "after"),
                resolved.Spec!.TemplateArguments(catalog), cancellationToken);

            byte[]? Project(string path)
            {
                var file = Path.Combine(root, path);
                return File.Exists(file) ? File.ReadAllBytes(file) : null;
            }

            ThreeWayMerge.AlignGuids(before, after, Project);
            var changes = ThreeWayMerge.Plan(before, after, Project, ThreeWayMerge.GitMergeFile(fromVersion, toVersion));
            if (!request.DryRun)
            {
                Apply(root, changes);
            }

            return new UpdateResult(fromVersion, toVersion, changes);
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    private async Task<Dictionary<string, GeneratedFile>> GenerateAsync(
        string package, string name, string output, IEnumerable<string> arguments, CancellationToken cancellationToken)
    {
        using var dotnetNew = await DotnetNew.InstallAsync(package, catalog.Template.ShortName, cancellationToken);
        await dotnetNew.GenerateAsync(name, output, arguments, cancellationToken);
        return Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories).ToDictionary(
            file => Path.GetRelativePath(output, file).Replace('\\', '/'),
            file => new GeneratedFile(File.ReadAllBytes(file), OperatingSystem.IsWindows() ? null : File.GetUnixFileMode(file)),
            StringComparer.Ordinal);
    }

    private static void Apply(string root, IReadOnlyList<FileChange> changes)
    {
        foreach (var change in changes)
        {
            var path = Path.Combine(root, change.Path);
            if (change.Kind == ChangeKind.Removed)
            {
                File.Delete(path);
                RemoveEmptyParents(root, Path.GetDirectoryName(path)!);
            }
            else if (change.Content is not null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, change.Content);
                if (change.Mode is { } mode && !OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(path, mode);
                }
            }
        }
    }

    private static void RemoveEmptyParents(string root, string directory)
    {
        while (directory.Length > root.Length && Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
        {
            Directory.Delete(directory);
            directory = Path.GetDirectoryName(directory)!;
        }
    }

    /// <summary>Projects from template versions before 0.5 do not record their name; the solution file has it.</summary>
    private static string InferName(string root)
    {
        var solutions = Directory.GetFiles(root, "*.slnx").Concat(Directory.GetFiles(root, "*.sln")).ToArray();
        return solutions.Length == 1
            ? Path.GetFileNameWithoutExtension(solutions[0])
            : throw new SpecException($"Cannot tell the project name from {solutions.Length} solution files in {root}. Add \"name\" to {OriginFile}.");
    }

    private static void RequireCleanGitTree(string root)
    {
        var start = new ProcessStartInfo("git", ["status", "--porcelain"]) { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true };
        string output;
        int exitCode;
        try
        {
            using var process = Process.Start(start)!;
            output = process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit();
            exitCode = process.ExitCode;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new SpecException("ek update needs git, to show what it changed. Install git, or pass --allow-dirty.");
        }

        if (exitCode != 0)
        {
            throw new SpecException($"{root} is not a git repository. Commit the project first so the update can be reviewed and undone, or pass --allow-dirty.");
        }

        if (output.Length > 0)
        {
            throw new SpecException("The project has uncommitted changes. Commit or stash them first, so the update can be reviewed and undone, or pass --allow-dirty.");
        }
    }

    private static int Compare(string left, string right) =>
        Version.TryParse(left.Split('-')[0], out var l) && Version.TryParse(right.Split('-')[0], out var r) ? l.CompareTo(r) : 0;
}
