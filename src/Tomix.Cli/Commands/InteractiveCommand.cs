using System.CommandLine;
using System.Diagnostics;
using Tomix.App.Interactive;
using Tomix.Cli.Output;
using Spectre.Console;

namespace Tomix.Cli.Commands;

internal sealed class InteractiveCommand : ICommandModule
{
    public Command Build()
    {
        var modelArgument = new Argument<string>("model")
        {
            Description = "Path to semantic model, or omit to connect later inside the session",
            Arity = ArgumentArity.ZeroOrOne
        };

        var command = new Command("interactive", "Start an interactive REPL session for running multiple commands against a model")
        {
            modelArgument
        };

        command.SetAction(parseResult =>
        {
            var format = GlobalOptions.OutputFormatValue(parseResult);
            if (!CommandOutput.TryValidateFormat(format, "interactive", OutputFormats.Text, OutputFormats.Json))
                return 2;

            var result = new InteractiveHandler().Start(new InteractiveStartRequest(
                GlobalOptions.ModelValue(parseResult) ?? parseResult.GetValue(modelArgument),
                parseResult.GetValue(GlobalOptions.Server),
                parseResult.GetValue(GlobalOptions.Database),
                GlobalOptions.AuthValue(parseResult),
                parseResult.GetValue(GlobalOptions.Local)));

            var exitCode = CommandOutput.Render(
                result,
                format,
                RenderStart,
                ProjectJson,
                errorFormat: parseResult.GetValue(GlobalOptions.ErrorFormat));

            if (exitCode != 0 || parseResult.GetValue(GlobalOptions.NonInteractive))
                return exitCode;

            return RunRepl();
        });

        return command;
    }

    private static void RenderStart(InteractiveStartResult result)
    {
        AnsiConsole.MarkupLine(Styling.Title("tx interactive"));
        AnsiConsole.MarkupLine(Styling.KeyValue("sessionId:", result.SessionId));
        if (result.Connection?.Model is not null)
            AnsiConsole.MarkupLine(Styling.KeyValue("model:", Styling.Path(result.Connection.Model)));
        else if (result.Connection?.Server is not null)
            AnsiConsole.MarkupLine(Styling.KeyValue("server:", Styling.Path(result.Connection.Server)));
        else
            AnsiConsole.MarkupLine(Styling.Warning("No active model. Use connect to set one."));

        AnsiConsole.MarkupLine(Styling.Guidance("Type 'exit' to leave."));
    }

    private static object ProjectJson(InteractiveStartResult result)
        => new
        {
            active = result.Active,
            sessionId = result.SessionId,
            path = result.Path,
            connection = result.Connection
        };

    private static int RunRepl()
    {
        while (true)
        {
            AnsiConsole.Markup(Styling.Title("tx") + "> ");
            var line = Console.ReadLine();
            if (line is null)
            {
                AnsiConsole.WriteLine();
                return 0;
            }

            var trimmed = line.Trim();
            if (trimmed.Length == 0)
                continue;

            if (string.Equals(trimmed, "exit", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(trimmed, "quit", StringComparison.OrdinalIgnoreCase))
                return 0;

            var args = SplitCommandLine(trimmed);
            if (args.Count == 0)
                continue;

            InvokeChild(args);
        }
    }

    private static void InvokeChild(IReadOnlyList<string> args)
    {
        var currentArgs = Environment.GetCommandLineArgs();
        var current = currentArgs.Length > 0 ? currentArgs[0] : "";
        using var process = new Process();
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;

        if (current.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            process.StartInfo.FileName = Environment.ProcessPath ?? "dotnet";
            process.StartInfo.ArgumentList.Add(current);
        }
        else
        {
            process.StartInfo.FileName = string.IsNullOrWhiteSpace(current)
                ? Environment.ProcessPath ?? "tx"
                : current;
        }

        foreach (var arg in args)
            process.StartInfo.ArgumentList.Add(arg);

        process.Start();
        Console.Write(process.StandardOutput.ReadToEnd());
        Console.Error.Write(process.StandardError.ReadToEnd());
        process.WaitForExit();
    }

    private static IReadOnlyList<string> SplitCommandLine(string line)
    {
        var args = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (char.IsWhiteSpace(c) && !inQuotes)
            {
                AddCurrent();
                continue;
            }

            current.Append(c);
        }

        AddCurrent();
        return args;

        void AddCurrent()
        {
            if (current.Length == 0)
                return;

            args.Add(current.ToString());
            current.Clear();
        }
    }
}
