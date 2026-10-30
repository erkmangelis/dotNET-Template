namespace Erkmangelis.Ek;

public sealed class UsageException(string message) : Exception(message);

/// <summary>
/// The parsed command line. Template options use <c>dotnet new</c>'s spelling (<c>--auth identity</c>,
/// <c>--worker</c> or <c>--worker true</c>), and the options it knows come from the catalog.
/// </summary>
public sealed class CommandLine
{
    private static readonly string[] ValueOptions = ["preset", "spec", "output", "save-spec", "package", "project", "from", "to", "source"];
    private static readonly string[] FlagOptions = ["yes", "dry-run", "allow-dirty", "github", "help"];

    private readonly Dictionary<string, List<string>> _values = [];
    private readonly HashSet<string> _flags = [];

    public string Command { get; private set; } = "help";

    public List<string> Positional { get; } = [];

    public Dictionary<string, string> TemplateOptions { get; } = [];

    public string? Value(string name) => _values.TryGetValue(name, out var values) ? values[^1] : null;

    public IReadOnlyList<string> Values(string name) => _values.TryGetValue(name, out var values) ? values : [];

    public bool Flag(string name) => _flags.Contains(name);

    public static CommandLine Parse(string[] args, Catalog catalog)
    {
        var line = new CommandLine();
        var index = 0;
        if (args.Length > 0 && !args[0].StartsWith('-'))
        {
            line.Command = args[0];
            index = 1;
        }
        else if (args.Length > 0 && args[0] is "--version")
        {
            line.Command = "version";
            return line;
        }

        for (; index < args.Length; index++)
        {
            var argument = args[index];
            if (!argument.StartsWith('-'))
            {
                line.Positional.Add(argument);
                continue;
            }

            var parts = argument.TrimStart('-').Split('=', 2);
            var name = parts[0];
            var inline = parts.Length == 2 ? parts[1] : null;
            name = name switch { "o" => "output", "n" => "name", "y" => "yes", "h" => "help", _ => name };

            if (name == "name")
            {
                line.Positional.Insert(0, inline ?? Next(args, ref index, name));
            }
            else if (FlagOptions.Contains(name))
            {
                line._flags.Add(name);
            }
            else if (ValueOptions.Contains(name))
            {
                var value = inline ?? Next(args, ref index, name);
                if (!line._values.TryGetValue(name, out var list))
                {
                    line._values[name] = list = [];
                }

                list.Add(value);
            }
            else if (catalog.FindOption(name) is { } option)
            {
                // A bool option may stand alone, as in dotnet new: --worker means --worker true.
                var standsAlone = option.IsBool && inline is null && !(index + 1 < args.Length && args[index + 1] is "true" or "false");
                line.TemplateOptions[option.Name] = standsAlone ? "true" : inline ?? Next(args, ref index, name);
            }
            else
            {
                throw new UsageException($"Unknown option '{argument}'.");
            }
        }

        if (line.Flag("help"))
        {
            line.Command = "help";
        }

        return line;
    }

    private static string Next(string[] args, ref int index, string name) =>
        ++index < args.Length ? args[index] : throw new UsageException($"--{name} needs a value.");
}
