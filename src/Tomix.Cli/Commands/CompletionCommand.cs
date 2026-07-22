using System.CommandLine;
using Tomix.App.Completion;
using Tomix.Cli.Output;

namespace Tomix.Cli.Commands;

internal sealed class CompletionCommand : ICommandModule
{
    public Command Build()
    {
        var shellArgument = new Argument<string>("shell")
        {
            Description = "Target shell: bash, zsh, fish, or powershell.",
            Arity = ArgumentArity.ZeroOrOne
        };
        // Completion scripts are always text. This local option intentionally shadows the
        // recursive global option so a configured JSON default cannot corrupt shell code.
        var formatOption = OutputFormats.CreateOption(OutputFormats.Text);

        var command = new Command("completion", "Generate a shell completion script.")
        {
            shellArgument,
            formatOption
        };

        command.SetAction(parseResult =>
        {
            var format = GlobalOptions.OutputFormatValue(parseResult, formatOption);
            if (!CommandOutput.TryValidateFormat(parseResult, format, "completion", OutputFormats.Text))
                return 2;

            var shell = parseResult.GetValue(shellArgument) ?? "";
            var result = new CompletionHandler().Generate(shell);
            return CommandOutput.Render(
                result,
                format,
                parseResult.GetValue(GlobalOptions.ErrorFormat),
                data => Console.WriteLine(data.Script));
        });

        return command;
    }
}
