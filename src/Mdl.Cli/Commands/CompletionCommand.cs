using System.CommandLine;
using Mdl.App.Completion;

namespace Mdl.Cli.Commands;

internal sealed class CompletionCommand : ICommandModule
{
    private readonly Func<IReadOnlyList<string>> _commandNames;

    /// <param name="commandNames">
    /// Resolves the live top-level command names at invocation time, so generated scripts
    /// stay in sync with the command tree as it grows.
    /// </param>
    public CompletionCommand(Func<IReadOnlyList<string>> commandNames)
        => _commandNames = commandNames;

    public Command Build()
    {
        var shellArgument = new Argument<string>("shell")
        {
            Description = "Target shell: bash, zsh, fish, or powershell."
        };

        var command = new Command("completion", "Generate a shell completion script.")
        {
            shellArgument
        };

        command.SetAction(parseResult =>
        {
            var shell = parseResult.GetValue(shellArgument) ?? "";
            var result = new CompletionHandler().Generate(shell, _commandNames());

            if (result.Data is null)
            {
                foreach (var diagnostic in result.Diagnostics)
                    Console.Error.WriteLine(diagnostic.Message);

                return result.ExitCode == 0 ? 1 : result.ExitCode;
            }

            Console.WriteLine(result.Data.Script);
            return result.ExitCode;
        });

        return command;
    }
}
