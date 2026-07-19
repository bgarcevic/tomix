using Spectre.Console;
using Tomix.App.Script;

namespace Tomix.Cli.Output;

internal static class ScriptRenderer
{
    public static void RenderText(ScriptRunResult result, string format)
    {
        var err = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Out = new AnsiConsoleOutput(Console.Error)
        });

        AnsiConsole.MarkupLine(Styling.Title($"Model: {result.ModelName}"));

        if (result.DryRun)
        {
            foreach (var script in result.Scripts)
            {
                if (script.Success)
                {
                    AnsiConsole.MarkupLine(Styling.Success($"Compilation OK: {script.Source}"));
                    continue;
                }

                err.MarkupLine(Styling.Error($"Compilation failed: {script.Source}"));
                foreach (var error in script.Errors)
                    err.WriteLine(error);
            }

            return;
        }

        for (var i = 0; i < result.Inputs.Count; i++)
        {
            var input = result.Inputs[i];
            AnsiConsole.MarkupLine(result.Inputs.Count == 1
                ? Styling.Value($"Script: {input.Source}")
                : Styling.Value($"Script {i + 1}/{result.Inputs.Count}: {input.Source}"));

            if (OutputFormats.IsTextLike(format))
                AnsiConsole.MarkupLine(Styling.Value($"Running {input.Source}..."));

            if (i < result.Messages.Count)
                AnsiConsole.WriteLine(result.Messages[i].Text);
        }

        if (!result.Success)
        {
            foreach (var error in result.CompileErrors)
                err.WriteLine(error);

            if (!string.IsNullOrWhiteSpace(result.RuntimeError))
                err.WriteLine(result.RuntimeError);

            return;
        }

        AnsiConsole.MarkupLine(Styling.Success(
            $"Done: {result.ScriptsExecuted} script(s) executed."));
        if (result.Saved is bool saved && saved == false)
            AnsiConsole.MarkupLine(Styling.Warning(
                "Changes not saved. Use --save to persist or --stage to stage."));
        else if (result.Staged == true)
            AnsiConsole.MarkupLine(Styling.Success("Mutation staged."));

        if (result.Synced)
            AnsiConsole.MarkupLine(Styling.Success(
                $"Synced: {Styling.MarkupEscape(result.SyncTarget!)}"));
        else if (result.SyncWarning is not null)
            AnsiConsole.MarkupLine(Styling.Warning(Styling.MarkupEscape(result.SyncWarning)));
    }

    public static object ToReferenceJson(ScriptRunResult result)
    {
        if (result.DryRun)
            return new
            {
                dryRun = true,
                scripts = result.Scripts.Select(script => new
                {
                    source = script.Source,
                    success = script.Success,
                    errors = script.Errors
                })
            };

        if (!result.Success)
            return new
            {
                success = false,
                durationMs = result.DurationMs,
                failedScript = result.FailedScript,
                scriptIndex = result.ScriptIndex,
                compileErrors = result.CompileErrors,
                runtimeError = result.RuntimeError,
                messages = result.Messages
            };

        if (result.Staged is null)
        {
            return new
            {
                success = true,
                durationMs = result.DurationMs,
                scriptsExecuted = result.ScriptsExecuted,
                messages = result.Messages,
                saved = result.Saved
            };
        }

        return new
        {
            success = true,
            durationMs = result.DurationMs,
            scriptsExecuted = result.ScriptsExecuted,
            messages = result.Messages,
            saved = result.Saved,
            staged = result.Staged
        };
    }
}
