using Spectre.Console;
using Tomix.App.Mutations;
using Tomix.App.Script;

namespace Tomix.Cli.Output;

internal static class ScriptRenderer
{
    public static void RenderText(ScriptRunResult result, string format)
    {
        var err = StdErr.Console();

        err.MarkupLine(Styling.Title($"Model: {result.ModelName}"));

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
                err.MarkupLine(Styling.Value($"Running {input.Source}..."));

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
        if (result.Status == MutationStatus.Preview)
            err.MarkupLine(Styling.Warning(MutationOutput.NotSavedHint));
        else if (result.Status == MutationStatus.Staged)
            AnsiConsole.MarkupLine(Styling.Success("Mutation staged."));
        else if (result.Saved)
            MutationOutput.RenderSaved(result.Outcome);

        MutationOutput.RenderSync(result.Outcome);
    }

    public static object ToReferenceJson(ScriptRunResult result)
    {
        if (result.DryRun)
            return new
            {
                status = result.Status,
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
                status = result.Status,
                durationMs = result.DurationMs,
                failedScript = result.FailedScript,
                scriptIndex = result.ScriptIndex,
                compileErrors = result.CompileErrors,
                runtimeError = result.RuntimeError,
                messages = result.Messages
            };

        return new
        {
            success = true,
            durationMs = result.DurationMs,
            scriptsExecuted = result.ScriptsExecuted,
            messages = result.Messages,
            status = result.Status,
            saved = result.Saved,
            savedTo = result.SavedTo,
            persistence = result.Persistence,
            target = result.Target,
            sync = result.Sync,
            newValidationErrors = result.NewValidationErrors
        };
    }
}
