using System.CommandLine;

namespace Tomix.Cli.Output;

/// <summary>
/// The value placeholder each option shows in help (<c>--save-to &lt;path&gt;</c>), assigned to
/// <see cref="Option.HelpName"/> once the tree is built. Without it the placeholder repeats the
/// option name (<c>--partition-expression &lt;partition-expression&gt;</c>), which says nothing
/// and doubles the label width. Keys are the option name, or <c>"&lt;command path&gt; &lt;option&gt;"</c>
/// where one name takes a different kind of value on one command. An option that sets its own
/// <see cref="Option.HelpName"/> keeps it.
/// </summary>
internal static class HelpPlaceholders
{
    internal static readonly Dictionary<string, string> Names = new(StringComparer.Ordinal)
    {
        ["--auth"] = "method",
        ["--bpa-fail-on"] = "level",
        ["--bpa-rules"] = "file",
        ["--certificate"] = "file",
        ["--certificate-password"] = "-",
        ["--certificate-password-file"] = "file",
        ["--ci"] = "system",
        ["--client-id"] = "id",
        ["--columns"] = "names",
        ["--compatibility-level"] = "level",
        ["--compatibility-mode"] = "mode",
        ["--connection-string"] = "string",
        ["--database"] = "name",
        ["--description"] = "text",
        ["--effective-date"] = "date",
        ["--endpoint"] = "address",
        ["--error-format"] = "format",
        ["--export"] = "file",
        ["--expression"] = "expr",
        ["script --expression"] = "code",
        ["--fail-on"] = "level",
        ["--fields"] = "list",
        ["--file"] = "file",
        ["--filter"] = "pattern",
        ["-i"] = "value",
        ["--import"] = "file",
        ["--in"] = "scope",
        ["--lang"] = "language",
        ["--limit"] = "n",
        ["--max-depth"] = "n",
        ["--max-parallelism"] = "n",
        ["--max-rows"] = "n",
        ["--mode"] = "mode",
        ["--model"] = "path",
        ["--name"] = "name",
        ["--output-file"] = "file",
        ["--output-format"] = "format",
        ["--param"] = "name=value",
        ["--partition"] = "table.partition",
        ["--partition-expression"] = "expr",
        ["--password"] = "-",
        ["--password-file"] = "file",
        ["--path"] = "path",
        ["bpa run --path"] = "pattern",
        ["--profile"] = "name",
        ["-q"] = "property",
        ["get --query"] = "property",
        ["query --query"] = "text",
        ["--range-end"] = "date",
        ["--range-granularity"] = "unit",
        ["--range-start"] = "date",
        ["--recent"] = "n",
        ["--refresh-type"] = "type",
        ["--rule"] = "id",
        ["--rules"] = "file|url",
        ["--rules-file"] = "file",
        ["--ruleset"] = "name",
        ["--runs"] = "n",
        ["--save-to"] = "path",
        ["--serialization"] = "format",
        ["--server"] = "workspace",
        ["--set"] = "name=value",
        ["--source"] = "provider",
        ["--source-database"] = "name",
        ["--source-schema"] = "name",
        ["--source-table"] = "name",
        ["--source-type"] = "protocol",
        ["--table"] = "name",
        ["--tenant"] = "id",
        ["--top"] = "n",
        ["--trace"] = "file",
        ["--trx"] = "file",
        ["--type"] = "type",
        ["--username"] = "app-id",
        ["--version"] = "version",
        ["--workspace"] = "target",
        ["--workspace-auth"] = "method",
        ["--workspace-format"] = "format",
        ["--xmla"] = "file",
    };

    /// <summary>Sets <see cref="Option.HelpName"/> on every option in the tree that has a mapping.</summary>
    public static void Apply(Command command, string path = "")
    {
        foreach (var option in command.Options)
        {
            if (option.HelpName is not null || !TakesValue(option))
                continue;

            if ((path.Length > 0 && Names.TryGetValue($"{path} {option.Name}", out var name))
                || Names.TryGetValue(option.Name, out name))
            {
                option.HelpName = name;
            }
        }

        foreach (var sub in command.Subcommands)
            Apply(sub, path.Length == 0 ? sub.Name : $"{path} {sub.Name}");
    }

    /// <summary>True when the option accepts a value (booleans and help/version are flags).</summary>
    public static bool TakesValue(Option option)
        => option is not System.CommandLine.Help.HelpOption and not VersionOption
           && option.ValueType != typeof(bool) && option.ValueType != typeof(bool?)
           && option.Arity.MaximumNumberOfValues > 0;
}
