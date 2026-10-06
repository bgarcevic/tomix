using System.CommandLine;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Tomix.Cli.Serve;

/// <summary>
/// A protocol method that runs a command of the session's command tree, so its <c>data</c> is that
/// command's <c>--output-format json</c> payload (docs/protocol.md, Requests and results).
/// </summary>
/// <param name="Command">The command's words, for example <c>["bpa", "run"]</c>, then any fixed flags.</param>
/// <param name="Positional">The parameters that are the command's arguments, in order.</param>
/// <param name="Edits">True when the method changes the model, so it is labelled in history.</param>
internal sealed record ProtocolRoute(string[] Command, string[] Positional, bool Edits = false);

/// <summary>
/// The methods <c>tx serve</c> answers by running a command, and how their parameters become that
/// command's arguments: a positional parameter fills an argument, and any other parameter is the
/// option of the same name in kebab case (<c>caseSensitive</c> is <c>--case-sensitive</c>).
/// <c>true</c> passes a flag; <c>false</c> passes the <c>--no-</c> form when there is one
/// (<c>fixRefs: false</c> is <c>--no-fix-refs</c>); an array repeats the option; an object passes
/// <c>name=value</c> pairs (<c>set</c>).
/// </summary>
internal static class ProtocolRoutes
{
    public static readonly IReadOnlyDictionary<string, ProtocolRoute> All = new Dictionary<string, ProtocolRoute>(StringComparer.Ordinal)
    {
        ["session.status"] = new(["status"], []),
        ["session.save"] = new(["save"], []),
        ["session.history"] = new(["history"], []),
        ["session.undo"] = new(["undo"], []),
        ["session.redo"] = new(["redo"], []),
        ["session.reload"] = new(["reload"], []),
        ["transaction.begin"] = new(["begin"], ["label"]),
        ["transaction.commit"] = new(["commit"], []),
        ["transaction.rollback"] = new(["rollback"], []),
        ["model.summary"] = new(["summary"], []),
        ["object.get"] = new(["get"], ["path"]),
        ["object.find"] = new(["find"], ["pattern"]),
        ["deps.get"] = new(["deps"], ["path"]),
        ["object.add"] = new(["add"], ["path"], Edits: true),
        ["object.set"] = new(["set"], ["path"], Edits: true),
        ["object.remove"] = new(["rm"], ["path"], Edits: true),
        ["object.move"] = new(["mv"], ["path", "to"], Edits: true),
        ["model.replace"] = new(["replace"], ["pattern", "replacement"], Edits: true),
        ["bpa.run"] = new(["bpa", "run"], []),
        ["bpa.fix"] = new(["bpa", "run", "--fix"], [], Edits: true),
        ["dax.format"] = new(["format"], [], Edits: true),
        ["dax.check"] = new(["validate"], []),
    };

    /// <summary>
    /// Parameters a client cannot pass: every edit applies to the session and <c>session.save</c>
    /// persists it, and the rest change what the command writes to stdout.
    /// </summary>
    private static readonly HashSet<string> Refused = new(StringComparer.Ordinal)
    {
        "save", "saveTo", "stage", "revert", "fix", "pathsOnly", "ci", "noMultiline", "help"
    };

    /// <summary>Accepted by every method and not passed on: <c>$/progress</c> reporting is not built yet.</summary>
    private const string ProgressToken = "progressToken";

    /// <summary>
    /// The command line for <paramref name="route"/>: the command's words, one option per
    /// parameter, then <c>--</c> and the positional values, so a value that starts with <c>-</c>
    /// is never read as an option.
    /// </summary>
    /// <exception cref="ProtocolException">A parameter the command does not take, or a value of the wrong type.</exception>
    public static List<string> Arguments(string method, ProtocolRoute route, JsonObject parameters, RootCommand root)
    {
        var command = Find(root, route.Command);
        var options = new List<string>(route.Command);
        var positional = new string?[route.Positional.Length];
        foreach (var (name, value) in parameters)
        {
            if (name == ProgressToken)
                continue;

            var index = Array.IndexOf(route.Positional, name);
            if (index >= 0)
            {
                positional[index] = Scalar(method, name, value);
                continue;
            }

            if (Refused.Contains(name))
                throw ProtocolException.InvalidParams($"'{name}' is not a parameter of {method}.");
            AddOption(method, command, name, value, options);
        }

        var last = Array.FindLastIndex(positional, value => value is not null);
        if (Array.FindIndex(positional, value => value is null) is var gap and >= 0 && gap < last)
            throw ProtocolException.InvalidParams($"{method} needs '{route.Positional[gap]}' when it is given '{route.Positional[last]}'.");
        if (last >= 0)
            options.AddRange(["--", .. positional[..(last + 1)]!]);
        return options;
    }

    private static Command Find(Command root, string[] words)
    {
        var command = root;
        foreach (var word in words.TakeWhile(word => !word.StartsWith('-')))
            command = command.Subcommands.First(sub => sub.Name == word);
        return command;
    }

    private static void AddOption(string method, Command command, string name, JsonNode? value, List<string> arguments)
    {
        var kebab = "--" + Kebab(name);
        var option = Option(command, kebab);
        if (value is JsonValue flag && flag.GetValueKind() is JsonValueKind.True or JsonValueKind.False)
        {
            var on = flag.GetValueKind() == JsonValueKind.True;
            var negated = Option(command, "--no-" + Kebab(name));
            if (on && option is not null && IsFlag(option))
                arguments.Add(kebab);
            else if (!on && negated is not null && IsFlag(negated))
                arguments.Add(negated.Name);
            else if (option is null && negated is null || option is not null && !IsFlag(option))
                throw Unknown(method, name, option is null ? null : "a value");
            return;
        }

        if (option is null || IsFlag(option))
            throw Unknown(method, name, option is null ? null : "true or false");

        switch (value)
        {
            case JsonArray items:
                foreach (var item in items)
                    arguments.Add($"{kebab}={Scalar(method, name, item)}");
                break;
            case JsonObject pairs:
                foreach (var (key, pair) in pairs)
                    arguments.Add($"{kebab}={key}={Scalar(method, $"{name}.{key}", pair)}");
                break;
            default:
                arguments.Add($"{kebab}={Scalar(method, name, value)}");
                break;
        }
    }

    private static Option? Option(Command command, string name)
        => command.Options.FirstOrDefault(option => option.Name == name || option.Aliases.Contains(name));

    private static bool IsFlag(Option option) => option.ValueType == typeof(bool);

    private static ProtocolException Unknown(string method, string name, string? expected)
        => ProtocolException.InvalidParams(expected is null
            ? $"'{name}' is not a parameter of {method}."
            : $"'{name}' of {method} must be {expected}.");

    /// <summary>A string, number or boolean parameter as command-line text.</summary>
    private static string? Scalar(string method, string name, JsonNode? value)
        => value switch
        {
            null => null,
            JsonValue text when text.GetValueKind() == JsonValueKind.String => text.GetValue<string>(),
            JsonValue number when number.GetValueKind() == JsonValueKind.Number => number.ToJsonString(),
            JsonValue boolean when boolean.GetValueKind() is JsonValueKind.True or JsonValueKind.False
                => boolean.GetValueKind() == JsonValueKind.True ? "true" : "false",
            _ => throw ProtocolException.InvalidParams($"'{name}' of {method} must be a string, a number or a boolean.")
        };

    /// <summary><c>caseSensitive</c> → <c>case-sensitive</c>.</summary>
    internal static string Kebab(string name)
    {
        var builder = new StringBuilder(name.Length + 4);
        foreach (var c in name)
        {
            if (char.IsUpper(c))
            {
                if (builder.Length > 0)
                    builder.Append('-');
                builder.Append(char.ToLower(c, CultureInfo.InvariantCulture));
            }
            else
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }
}
