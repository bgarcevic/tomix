using Tomix.App.State;
using Tomix.Core.Models;
using Tomix.Core.Results;

namespace Tomix.App.Mutations;

public static class MutationRunner
{
    public static async Task<TomixResult<TResult>> RunAsync<TResult>(
        IReadOnlyList<IModelProvider> providers,
        ModelReference model,
        MutationOptions options,
        string command,
        Func<IModelMutationSession, IModelSession, MutationContext, Task<(bool Changed, string Summary, Func<MutationOutcome, TResult> BuildResult)>> mutate,
        TResult revertResult,
        CancellationToken cancellationToken)
    {
        var stagingStore = new StagingStore();
        var connection = new CliStateStore().LoadCurrentSession();

        var begin = await MutationLifecycle.BeginAsync(
            providers, model, options, stagingStore, connection, cancellationToken);
        if (begin.Error is { } error)
            return TomixResult<TResult>.Fail(error.Code, error.Message, error.ExitCode);

        // The staging handle holds the per-model lock; release it on every exit path.
        using var stagingHandle = begin.Context?.Staging;

        if (begin.Mode == MutationMode.Revert)
        {
            if (!stagingStore.Discard(model))
                return TomixResult<TResult>.Fail(
                    "TOMIX_STAGE_NOTHING_STAGED",
                    "Nothing is staged for this model.",
                    hint: "Use --stage to stage a mutation first; 'tx stage' lists staged work.");

            return TomixResult<TResult>.Ok(revertResult);
        }

        var context = begin.Context!;
        var provider = providers.ResolveSingle(context.EffectiveModel);
        if (provider is null)
            return TomixResult<TResult>.Fail(
                "TOMIX_NO_PROVIDER",
                $"No provider can open model: {context.EffectiveModel.Value}",
                exitCode: 2,
                hint: "Supported formats: TMDL folder, .bim file. For remote models, use --server and --database.");

        await using var session = await provider.OpenAsync(context.EffectiveModel, cancellationToken);
        if (session is not IModelMutationSession mutator)
            return TomixResult<TResult>.Fail(
                "TOMIX_MUTATION_UNSUPPORTED_PROVIDER",
                $"Provider cannot mutate model: {context.EffectiveModel.Value}");

        try
        {
            var (changed, summary, buildResult) = await mutate(mutator, session, context);

            if (!changed)
                return TomixResult<TResult>.Ok(buildResult(new MutationOutcome(false, null)));

            var outcome = await MutationLifecycle.CompleteAsync(
                mutator, context, command, summary, cancellationToken);

            // A failed workspace sync leaves the mirror behind the source; render the saved
            // result but exit non-zero so CI catches the drift.
            return TomixResult<TResult>.Ok(buildResult(outcome), outcome.SyncFailed ? 1 : 0);
        }
        catch (UnsupportedAddOptionException ex)
        {
            return TomixResult<TResult>.Fail("TOMIX_ADD_OPTION_UNSUPPORTED", ex.Message);
        }
        catch (RenameBrokenReferencesException ex)
        {
            return TomixResult<TResult>.Fail(
                "TOMIX_RENAME_BREAKS_REFS", ex.Message,
                hint: "Update the references first, or re-run without --strict-refs to rename anyway.");
        }
        catch (RemoveBrokenReferencesException ex)
        {
            return TomixResult<TResult>.Fail(
                "TOMIX_RM_BREAKS_REFS", ex.Message,
                hint: "Inspect with 'tx deps', update with 'tx replace', or re-run with --force to remove anyway.");
        }
        catch (RefreshPolicyValidationException ex)
        {
            return TomixResult<TResult>.Fail(
                "TOMIX_REFRESH_POLICY_INVALID", ex.Message,
                hint: "Fix the reported issues or re-run with --force to save anyway.");
        }
        catch (RefreshPolicyNotFoundException ex)
        {
            return TomixResult<TResult>.Fail(
                "TOMIX_REFRESH_POLICY_NOT_FOUND", ex.Message,
                hint: "Use --if-exists to ignore, or 'tx incremental-refresh show' to inspect.");
        }
        catch (NotSupportedException ex)
        {
            return TomixResult<TResult>.Fail("TOMIX_MUTATION_UNSUPPORTED", ex.Message);
        }
        catch (ArgumentException ex)
        {
            return TomixResult<TResult>.Fail("TOMIX_MUTATION_INVALID_VALUE", ex.Message);
        }
        catch (ObjectNotFoundException ex)
        {
            return TomixResult<TResult>.Fail("TOMIX_OBJECT_NOT_FOUND", ex.Message, hint: ex.Hint);
        }
        catch (AmbiguousObjectException ex)
        {
            return TomixResult<TResult>.Fail("TOMIX_OBJECT_AMBIGUOUS", ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return TomixResult<TResult>.Fail("TOMIX_MUTATION_FAILED", ex.Message);
        }
        catch (OutputExistsException ex)
        {
            return TomixResult<TResult>.Fail("TOMIX_SAVE_OUTPUT_EXISTS", ex.Message, exitCode: 2);
        }
        catch (IOException ex)
        {
            return TomixResult<TResult>.Fail("TOMIX_MUTATION_SAVE_FAILED", ex.Message, exitCode: 2);
        }
    }
}
