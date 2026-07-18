using System.CommandLine;
using Spectre.Console;
using Tomix.App;
using Tomix.App.Session;
using Tomix.Cli.Output;

namespace Tomix.Cli.Commands;

internal sealed class SessionCommand : ICommandModule
{
    private readonly AppServices _services;

    public SessionCommand(AppServices services) => _services = services;

    public Command Build()
    {
        var command = new Command("session", "Show or manage the current terminal session");
        command.SetAction(parseResult => RenderShow(parseResult));
        command.Subcommands.Add(BuildClear());
        command.Subcommands.Add(BuildList());
        command.Subcommands.Add(BuildPrune());
        command.Subcommands.Add(BuildShow());
        return command;
    }

    private Command BuildShow()
    {
        var command = new Command("show", "Show current session details (ID, file path, active state)");
        command.SetAction(parseResult => RenderShow(parseResult));
        return command;
    }

    private Command BuildList()
    {
        var command = new Command("list", "List all session files");
        command.SetAction(parseResult =>
        {
            var format = GlobalOptions.OutputFormatValue(parseResult);
            if (!CommandOutput.TryValidateFormat(parseResult, format, "session list", OutputFormats.Text, OutputFormats.Json))
                return 2;

            return CommandOutput.Render(new SessionHandler(_services.State).List(), format, RenderList);
        });
        return command;
    }

    private Command BuildClear()
    {
        var command = new Command("clear", "Clear active state for the current session");
        command.SetAction(parseResult =>
        {
            var format = GlobalOptions.OutputFormatValue(parseResult);
            if (!CommandOutput.TryValidateFormat(parseResult, format, "session clear", OutputFormats.Text, OutputFormats.Json))
                return 2;

            return CommandOutput.Render(
                new SessionHandler(_services.State).Clear(),
                format,
                result => AnsiConsole.MarkupLine(result.Cleared ? Styling.Success("Cleared current session.") : Styling.Muted("No active session.")));
        });
        return command;
    }

    private Command BuildPrune()
    {
        var allOption = new Option<bool>("--all")
        {
            Description = "Also remove named and live process sessions. The current session is kept."
        };
        var dryRunOption = new Option<bool>("--dry-run")
        {
            Description = "Show what would be removed without doing it"
        };
        var command = new Command("prune", "Delete session files whose shell process is no longer running")
        {
            allOption,
            dryRunOption
        };

        command.SetAction(parseResult =>
        {
            var format = GlobalOptions.OutputFormatValue(parseResult);
            if (!CommandOutput.TryValidateFormat(parseResult, format, "session prune", OutputFormats.Text, OutputFormats.Json))
                return 2;

            return CommandOutput.Render(
                new SessionHandler(_services.State).Prune(parseResult.GetValue(allOption), parseResult.GetValue(dryRunOption)),
                format,
                result => AnsiConsole.MarkupLine(result.DryRun
                    ? Styling.Warning($"Would remove {result.Removed} session(s).")
                    : Styling.Success($"Removed {result.Removed} session(s).")));
        });
        return command;
    }

    private int RenderShow(ParseResult parseResult)
    {
        var format = GlobalOptions.OutputFormatValue(parseResult);
        if (!CommandOutput.TryValidateFormat(parseResult, format, "session", OutputFormats.Text, OutputFormats.Json))
            return 2;

        return CommandOutput.Render(new SessionHandler(_services.State).Show(), format, RenderShowResult);
    }

    private static void RenderShowResult(SessionShowResult result)
    {
        AnsiConsole.MarkupLine(Styling.KeyValue("sessionId: ", result.SessionId));
        AnsiConsole.MarkupLine(Styling.KeyValue("kind:      ", result.Kind));
        AnsiConsole.MarkupLine(Styling.KeyValue("path:      ", result.Path));
        AnsiConsole.MarkupLine(Styling.KeyValue("exists:    ", result.Exists.ToString()));
        if (result.Active is not null)
            AnsiConsole.MarkupLine(Styling.KeyValue("model:     ", result.Active.Model ?? ""));
    }

    private static void RenderList(SessionListResult result)
    {
        foreach (var session in result.Sessions)
            AnsiConsole.WriteLine($"{session.SessionId}\t{(session.Current ? "current" : "")}\t{session.Path}");
    }
}
