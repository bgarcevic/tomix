using System.CommandLine;
using System.Text;
using System.Text.Json.Nodes;
using Tomix.Cli.Output;

namespace Tomix.Cli.Serve;

/// <summary>
/// One tool <c>tx mcp</c> lists: a session protocol method (docs/protocol.md) under an MCP name, with
/// the hints a harness uses to decide what to ask the person before it runs.
/// </summary>
/// <param name="Name">The tool's name, for example <c>object_set</c>.</param>
/// <param name="Method">The session protocol method it calls, for example <c>object.set</c>.</param>
/// <param name="Description">What the tool does, written for an agent.</param>
/// <param name="InputSchema">The JSON Schema of its arguments.</param>
/// <param name="ReadOnly">True when it changes neither the model nor its files.</param>
/// <param name="Destructive">For a tool that writes: true when it can change or remove what is there, not only add.</param>
internal sealed record McpTool(string Name, string Method, string Description, JsonObject InputSchema, bool ReadOnly, bool Destructive)
{
    /// <summary>The tool as <c>tools/list</c> describes it.</summary>
    public JsonObject Describe() => new()
    {
        ["name"] = Name,
        ["description"] = Description,
        ["inputSchema"] = InputSchema.DeepClone(),
        ["annotations"] = new JsonObject
        {
            ["readOnlyHint"] = ReadOnly,
            ["destructiveHint"] = !ReadOnly && Destructive,
            ["openWorldHint"] = false
        }
    };

    /// <summary>The argument names the schema allows.</summary>
    public IEnumerable<string> Parameters => InputSchema["properties"]!.AsObject().Select(pair => pair.Key);
}

/// <summary>
/// The tools of <c>tx mcp</c> (#354). Each calls the session protocol method of the same name, so a
/// tool does exactly what that method does for any other client of the session. The arguments of a
/// method that runs a command are that command's options, in camel case, less those that persist,
/// write files or only change how text output looks; the schema is built from the command, so it
/// follows the CLI.
/// </summary>
internal static class McpTools
{
    /// <summary>Options no tool takes: they save or stage, write files, fetch rules from model
    /// annotations, or shape only text output.</summary>
    private static readonly HashSet<string> Left = new(StringComparer.Ordinal)
    {
        "save", "saveTo", "stage", "revert", "fix", "pathsOnly", "ci", "noMultiline", "help",
        "all", "details", "full", "trx", "failOn", "allowExternalRules"
    };

    private static readonly Definition[] Definitions =
    [
        new("session_open", "session.open", Kind.Read,
            "Open a model in the session: a TMDL folder or .bim file, or a server and database. A model that tx ui has open is joined, so the person sees your edits there.",
            Schema: Schema(
                ("model", Text("A TMDL folder or .bim file")),
                ("server", Text("A server: an XMLA endpoint, or localhost:<port> for Power BI Desktop")),
                ("database", Text("The database on that server")))),
        new("session_status", "session.status", Kind.Read,
            "The open model, whether it has unsaved changes, the undo and redo depth and any open transaction.",
            Schema: Schema()),
        new("session_history", "session.history", Kind.Read,
            "The changes undo can revert and redo can reapply, newest last, with who made them.",
            Schema: Schema()),
        new("model_summary", "model.summary", Kind.Read,
            "Counts of the model's tables, columns, measures, relationships and other objects."),
        new("model_tree", "model.tree", Kind.Read,
            "The model's objects one level at a time: the tables without a path, the children of the object at a path.",
            Schema: Schema(("path", Text("The object whose children to list, for example 'Sales'; leave out for the top level")))),
        new("object_get", "object.get", Kind.Read,
            "The properties of one object ('Sales/Amount'), or a list of the objects a wildcard or container selects ('Sa*', 'Sales/Measures')."),
        new("object_find", "object.find", Kind.Read,
            "Find objects whose names, expressions, descriptions, display folders or format strings contain a text or regex."),
        new("deps_get", "deps.get", Kind.Read,
            "What an object uses and what uses it, or the measures and columns nothing uses."),
        new("dax_check", "dax.check", Kind.Read,
            "Check every DAX expression in the model for syntax errors and broken references."),
        new("bpa_run", "bpa.run", Kind.Read,
            "Run the Best Practice Analyzer rules on the model and list the violations.",
            Without: ["allowDelete"]),
        new("object_add", "object.add", Kind.Add,
            "Add an object: a measure, table, column, calculated column, relationship, role, perspective and more."),
        new("object_set", "object.set", Kind.Change,
            "Set properties of an object, for example its expression, format string, description or name. A rename rewrites the DAX that refers to it."),
        new("object_move", "object.move", Kind.Change,
            "Rename an object or move it to another table, rewriting the DAX that refers to it."),
        new("object_remove", "object.remove", Kind.Change,
            "Remove an object. Fails while DAX still refers to it, unless forced."),
        new("model_replace", "model.replace", Kind.Change,
            "Replace a text or regex in names, expressions, descriptions, display folders or format strings across the model."),
        new("dax_format", "dax.format", Kind.Change,
            "Format DAX: one expression given inline (nothing changes), the expression of the object at a path, or every expression in the model."),
        new("bpa_fix", "bpa.fix", Kind.Change,
            "Apply the fixes of the Best Practice Analyzer rules that have one.",
            Without: ["allowDelete"]),
        new("transaction_begin", "transaction.begin", Kind.Add,
            "Group the edits that follow into one undo step, until transaction_commit or transaction_rollback. Give it a label the person will recognise in history.",
            Schema: Schema(("label", Text("A name for the step, shown in history, for example 'Margin measures'")))),
        new("transaction_commit", "transaction.commit", Kind.Add,
            "Keep the open transaction's edits as one undo step.",
            Schema: Schema()),
        new("transaction_rollback", "transaction.rollback", Kind.Change,
            "Discard the open transaction's edits.",
            Schema: Schema()),
        new("session_undo", "session.undo", Kind.Change,
            "Revert the last change in the session as one step, whoever made it.",
            Schema: Schema()),
        new("session_redo", "session.redo", Kind.Change,
            "Reapply the last undone change.",
            Schema: Schema()),
        new("session_save", "session.save", Kind.Change,
            "Write the session's changes to the model's files or server. Save only when the person asks you to.",
            Schema: Schema()),
    ];

    /// <summary>
    /// The tools, their arguments read from the commands of <paramref name="commands"/>, the
    /// one-shot command tree; with <paramref name="readOnly"/>, only those that change nothing.
    /// </summary>
    public static IReadOnlyList<McpTool> Build(RootCommand commands, bool readOnly)
        => [.. Definitions
            .Where(definition => !readOnly || definition.Kind == Kind.Read)
            .Select(definition => new McpTool(
                definition.Name,
                definition.Method,
                definition.Description,
                definition.Schema ?? FromCommand(commands, definition),
                definition.Kind == Kind.Read,
                definition.Kind == Kind.Change))];

    private static JsonObject FromCommand(RootCommand commands, Definition definition)
    {
        if (!ProtocolRoutes.All.TryGetValue(definition.Method, out var route) || Find(commands, route.Command) is not { } command)
            return Schema();

        var properties = new List<(string, JsonObject)>();
        var required = new List<string>();
        for (var i = 0; i < route.Positional.Length; i++)
        {
            var argument = command.Arguments[i];
            properties.Add((route.Positional[i], Text(argument.Description)));
            if (argument.Arity.MinimumNumberOfValues > 0)
                required.Add(route.Positional[i]);
        }

        var names = command.Options.Select(option => option.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var option in command.Options)
        {
            if (option.Hidden || HelpGroups.Of(option) == HelpGroups.Save || route.Command.Contains(option.Name))
                continue;

            // '--no-fix-refs' is 'fixRefs: false', as the protocol maps it.
            var negated = option.Name.StartsWith("--no-", StringComparison.Ordinal) && !names.Contains("--" + option.Name[5..]);
            var name = Camel(negated ? option.Name[5..] : option.Name[2..]);
            if (Left.Contains(Camel(option.Name[2..])) || Left.Contains(name) || definition.Without.Contains(name))
                continue;

            properties.Add((name, negated
                ? Typed("boolean", $"Default: true. false: {option.Description}")
                : OptionSchema(option)));
        }

        return Schema([.. properties], [.. required]);
    }

    private static JsonObject OptionSchema(Option option)
    {
        var type = option.ValueType;
        if (option.Name == "--set")
            return new JsonObject
            {
                ["type"] = "object",
                ["description"] = "Properties to set, by name: { \"expression\": \"SUM(Sales[Amount])\", \"formatString\": \"0.0%\" }. Names can use dotted paths, bracket indexers or a display name.",
                ["additionalProperties"] = new JsonObject { ["type"] = new JsonArray("string", "number", "boolean") }
            };
        if (type == typeof(bool))
            return Typed("boolean", option.Description);
        if (type == typeof(int) || type == typeof(int?) || type == typeof(long))
            return Typed("integer", option.Description);
        if (type.IsArray || type != typeof(string) && type.IsAssignableTo(typeof(System.Collections.IEnumerable)))
            return new JsonObject
            {
                ["type"] = "array",
                ["description"] = option.Description,
                ["items"] = new JsonObject { ["type"] = "string" }
            };
        if ((Nullable.GetUnderlyingType(type) ?? type) is { IsEnum: true } enumType)
            return new JsonObject
            {
                ["type"] = "string",
                ["description"] = option.Description,
                ["enum"] = new JsonArray([.. Enum.GetNames(enumType).Select(value => (JsonNode)value)])
            };
        return Text(option.Description);
    }

    private static Command? Find(Command root, string[] words)
    {
        Command? command = root;
        foreach (var word in words.TakeWhile(word => !word.StartsWith('-')))
            command = command?.Subcommands.FirstOrDefault(sub => sub.Name == word);
        return command;
    }

    /// <summary><c>case-sensitive</c> → <c>caseSensitive</c>, the inverse of <see cref="ProtocolRoutes.Kebab"/>.</summary>
    private static string Camel(string kebab)
    {
        var builder = new StringBuilder(kebab.Length);
        var upper = false;
        foreach (var c in kebab)
        {
            if (c == '-')
            {
                upper = true;
                continue;
            }

            builder.Append(upper ? char.ToUpperInvariant(c) : c);
            upper = false;
        }

        return builder.ToString();
    }

    private static JsonObject Schema(params (string Name, JsonObject Schema)[] properties) => Schema(properties, []);

    private static JsonObject Schema((string Name, JsonObject Schema)[] properties, string[] required)
    {
        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject(properties.Select(property => KeyValuePair.Create(property.Name, (JsonNode?)property.Schema))),
            ["additionalProperties"] = false
        };
        if (required.Length > 0)
            schema["required"] = new JsonArray([.. required.Select(name => (JsonNode)name)]);
        return schema;
    }

    private static JsonObject Text(string? description) => Typed("string", description);

    private static JsonObject Typed(string type, string? description)
    {
        var schema = new JsonObject { ["type"] = type };
        if (!string.IsNullOrWhiteSpace(description))
            schema["description"] = description;
        return schema;
    }

    private enum Kind
    {
        /// <summary>Changes nothing.</summary>
        Read,

        /// <summary>Adds to the session without changing or removing what is there.</summary>
        Add,

        /// <summary>Can change or remove what is there.</summary>
        Change
    }

    private sealed record Definition(string Name, string Method, Kind Kind, string Description, JsonObject? Schema = null, string[]? Without = null)
    {
        public string[] Without { get; } = Without ?? [];
    }
}
