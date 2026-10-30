using System.Text.Json;
using Erkmangelis.Ek;

var catalog = Catalog.Embedded;
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};

try
{
    var arguments = CommandLine.Parse(args, catalog);
    return arguments.Command switch
    {
        "new" => await Commands.NewAsync(catalog, arguments, cancellation.Token),
        "update" => await Commands.UpdateAsync(catalog, arguments, cancellation.Token),
        "check" => Commands.Check(catalog, arguments),
        "list" => Commands.List(catalog),
        "schema" => Commands.Schema(catalog),
        "matrix" => Commands.Matrix(catalog, arguments),
        "version" => Commands.Version(),
        "help" => Commands.Help(),
        var unknown => throw new UsageException($"Unknown command '{unknown}'."),
    };
}
catch (UsageException exception)
{
    Console.Error.WriteLine($"error: {exception.Message}");
    Console.Error.WriteLine("Run 'ek help' for usage.");
    return 2;
}
catch (SpecException exception)
{
    Console.Error.WriteLine($"error: {exception.Message}");
    return 1;
}
catch (OperationCanceledException)
{
    return 130;
}

internal static class Commands
{
    public static async Task<int> NewAsync(Catalog catalog, CommandLine arguments, CancellationToken cancellationToken)
    {
        var spec = arguments.Value("spec") is { } specPath ? ProjectSpec.Read(specPath) : new ProjectSpec();
        var name = arguments.Positional.ElementAtOrDefault(0) ?? spec.Name;
        var preset = arguments.Value("preset") ?? spec.Preset;
        var options = new Dictionary<string, string>(spec.Options);
        foreach (var (option, value) in arguments.TemplateOptions)
        {
            options[option] = value;
        }

        var interactive = !arguments.Flag("yes") && !Console.IsInputRedirected;
        if (interactive)
        {
            (name, preset) = Prompter.Ask(catalog, name, preset, options, arguments.TemplateOptions.Count > 0 || arguments.Value("spec") is not null);
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new UsageException("Give the project a name: ek new <name>.");
        }

        if (spec.Version is { } specVersion && specVersion != TemplatePackages.ToolVersion)
        {
            Console.Error.WriteLine($"note: the spec was written for template {specVersion}; generating with {TemplatePackages.ToolVersion}.");
        }

        var result = SpecResolver.Resolve(catalog, spec with { Name = name, Preset = preset, Options = options });
        if (!result.Succeeded)
        {
            throw new SpecException($"This combination is not supported:\n  {string.Join("\n  ", result.Errors)}");
        }

        var resolved = result.Spec!;
        var output = Path.GetFullPath(arguments.Value("output") ?? name);
        var templateArguments = resolved.TemplateArguments(catalog);
        foreach (var implied in resolved.Implied)
        {
            Console.WriteLine($"{implied.Option}: {implied.Value} ({implied.Rule.Reason})");
        }

        var packages = new TemplatePackages(catalog, arguments.Values("source"), Console.Out);
        var package = arguments.Value("package") is { } packagePath ? await packages.FindAsync(packagePath, cancellationToken)
            : packages.Bundled ?? throw new SpecException("This build of ek carries no template package; pass --package.");
        var version = packages.VersionOf(package);
        var specJson = ProjectSpec.Write(catalog, resolved, version);
        if (arguments.Value("save-spec") is { } savePath)
        {
            await File.WriteAllTextAsync(savePath, specJson, cancellationToken);
            Console.WriteLine($"Wrote the spec to {savePath}");
        }

        var command = DotnetNew.Arguments(catalog.Template.ShortName, name, arguments.Value("output") ?? name, templateArguments);
        Console.WriteLine($"Same as: dotnet {string.Join(' ', command)}");
        if (arguments.Flag("dry-run"))
        {
            Console.Write(specJson);
            return 0;
        }

        if (interactive)
        {
            Console.Write("Generate it? [Y/n] ");
            if (Console.ReadLine()?.Trim().ToLowerInvariant() is "n" or "no")
            {
                return 1;
            }
        }

        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
        {
            throw new SpecException($"{output} is not empty.");
        }

        using var dotnetNew = await DotnetNew.InstallAsync(package, catalog.Template.ShortName, cancellationToken);
        await dotnetNew.GenerateAsync(name, output, templateArguments, cancellationToken);
        Console.WriteLine($"Created {name} with {catalog.Template.ShortName} {version} in {output}");
        Console.WriteLine($"Its spec is {Path.Combine(output, ProjectUpdater.OriginFile)}; 'ek update' reads it to update the project later.");
        return 0;
    }

    public static async Task<int> UpdateAsync(Catalog catalog, CommandLine arguments, CancellationToken cancellationToken)
    {
        var packages = new TemplatePackages(catalog, arguments.Values("source"), Console.Out);
        var request = new UpdateRequest(
            arguments.Value("project") ?? arguments.Positional.ElementAtOrDefault(0) ?? ".",
            arguments.Value("from"),
            arguments.Value("to"),
            arguments.Flag("dry-run"),
            arguments.Flag("allow-dirty"));
        var result = await new ProjectUpdater(catalog, packages, Console.Out).UpdateAsync(request, cancellationToken);
        if (result.Changes.Count == 0)
        {
            Console.WriteLine(result.FromVersion == result.ToVersion
                ? $"Already at {result.ToVersion}."
                : $"Nothing to change from {result.FromVersion} to {result.ToVersion}.");
            return 0;
        }

        Console.WriteLine($"{(request.DryRun ? "Would update" : "Updated")} from {result.FromVersion} to {result.ToVersion}:");
        foreach (var group in result.Changes.GroupBy(change => change.Kind).OrderBy(group => group.Key))
        {
            Console.WriteLine($"  {Describe(group.Key)}:");
            foreach (var change in group)
            {
                Console.WriteLine($"    {change.Path}");
            }
        }

        if (result.Conflicts > 0)
        {
            Console.WriteLine($"{result.Conflicts} file(s) have conflict markers (<<<<<<< your project ... >>>>>>> template {result.ToVersion}). Resolve them, build, test and commit.");
            return 1;
        }

        Console.WriteLine(request.DryRun ? "Nothing was written." : "Review the changes with 'git diff', then build, test and commit.");
        return 0;
    }

    public static int Check(Catalog catalog, CommandLine arguments)
    {
        var path = arguments.Positional.ElementAtOrDefault(0) ?? throw new UsageException("ek check <spec.json>");
        var result = SpecResolver.Resolve(catalog, ProjectSpec.Read(path), arguments.TemplateOptions);
        if (!result.Succeeded)
        {
            Console.Error.WriteLine($"{path} is not supported:");
            foreach (var error in result.Errors)
            {
                Console.Error.WriteLine($"  {error}");
            }

            return 1;
        }

        Console.WriteLine($"{path} is supported. Resolved options:");
        foreach (var (option, value) in result.Spec!.Options)
        {
            var implied = result.Spec.Implied.FirstOrDefault(item => item.Option == option);
            Console.WriteLine($"  {option}: {value}{(implied is null ? "" : $" (set by {implied.Rule.Id})")}");
        }

        return 0;
    }

    public static int List(Catalog catalog)
    {
        Console.WriteLine($"Template {catalog.Template.ShortName} ({catalog.Template.Identity}) {TemplatePackages.ToolVersion}");
        Console.WriteLine();
        Console.WriteLine("Options:");
        foreach (var option in catalog.Options)
        {
            Console.WriteLine($"  --{option.Name} (default {option.Default})  {option.Description}");
            foreach (var choice in option.Choices.Where(_ => !option.IsBool))
            {
                Console.WriteLine($"      {choice.Value,-10} {choice.Description}");
            }
        }

        Console.WriteLine();
        Console.WriteLine("Rules:");
        foreach (var rule in catalog.Rules)
        {
            Console.WriteLine($"  {rule.Id}: {rule.Reason}");
        }

        Console.WriteLine();
        Console.WriteLine("Presets (ek new <name> --preset <preset>):");
        foreach (var preset in catalog.Presets)
        {
            var aliases = preset.Aliases.Count > 0 ? $" (also {string.Join(", ", preset.Aliases)})" : "";
            Console.WriteLine($"  {preset.Name}{aliases}: {preset.Description}");
            Console.WriteLine($"      {string.Join(' ', preset.Options.Select(pair => $"--{pair.Key} {pair.Value}"))}");
        }

        return 0;
    }

    public static int Schema(Catalog catalog)
    {
        Console.Write(SpecSchema.Generate(catalog));
        return 0;
    }

    /// <summary>The CI acceptance matrix as JSON; with --github, as a step output line.</summary>
    public static int Matrix(Catalog catalog, CommandLine arguments)
    {
        var rows = TestMatrix.Build(catalog).Select(row =>
        {
            var entry = new Dictionary<string, object> { ["name"] = row.Name };
            foreach (var (option, value) in row.Options)
            {
                entry[option] = value;
            }

            return entry;
        });
        var json = JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = !arguments.Flag("github") });
        Console.WriteLine(arguments.Flag("github") ? $"include={json}" : json);
        return 0;
    }

    public static int Version()
    {
        Console.WriteLine(TemplatePackages.ToolVersion);
        return 0;
    }

    public static int Help()
    {
        Console.WriteLine($"""
            ek {TemplatePackages.ToolVersion}: generate and update projects from the Erkmangelis .NET templates.

            ek new [<name>] [--preset <preset>] [--spec <file>] [--<option> <value>]... [-o <dir>]
                   [--save-spec <file>] [--package <nupkg>] [--dry-run] [-y]
                Asks for anything not given (unless -y or input is redirected), checks the
                combination against the rules, and runs dotnet new. --dry-run prints the spec
                and the equivalent dotnet new command instead.
            ek update [<project dir>] [--to <version|nupkg>] [--from <version|nupkg>] [--source <dir>]
                      [--dry-run] [--allow-dirty]
                Three-way merges the template's changes since the project's version (from its
                template-origin.json) into the project. Conflicts get markers; nothing you changed
                is overwritten. Needs a clean git working tree.
            ek check <spec> [--<option> <value>]...   Validates a spec and prints the resolved options.
            ek list                                   Options, rules and presets.
            ek schema                                 The JSON Schema of a spec.
            ek matrix [--github]                      The CI acceptance matrix (presets plus pairwise).
            ek version
            """);
        return 0;
    }

    private static string Describe(ChangeKind kind) => kind switch
    {
        ChangeKind.Added => "added",
        ChangeKind.Updated => "updated",
        ChangeKind.Removed => "removed",
        ChangeKind.Merged => "merged with your changes",
        ChangeKind.Conflict => "CONFLICT, resolve the markers",
        ChangeKind.Kept => "kept your version; the template removed or replaced it, check by hand",
        ChangeKind.StaysDeleted => "you deleted it; the template changed it, check by hand",
        _ => kind.ToString(),
    };
}
