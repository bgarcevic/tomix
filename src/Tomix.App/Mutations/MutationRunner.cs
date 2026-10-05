using Tomix.App.Diagnostics;
using Tomix.App.Models;
using Tomix.App.State;
using Tomix.Core.Models;
using Tomix.Core.Results;

namespace Tomix.App.Mutations;

public static class MutationRunner
{
    public static Task<TomixResult<TResult>> RunAsync<TResult>(
        IReadOnlyList<IModelProvider> providers,
        ModelReference model,
        MutationOptions options,
        string command,
        MutationStores stores,
        Func<IModelMutationSession, IModelSession, MutationContext, Task<(bool Changed, string Summary, Func<MutationOutcome, TResult> BuildResult)>> mutate,
        Func<MutationOutcome, TResult> revertResult,
        CancellationToken cancellationToken)
        => RunAsync(
            new OneShotSessionSource(providers), model, options, command, stores, mutate, revertResult, cancellationToken);

    /// <summary>
    /// Runs one mutation request. On a one-shot source it opens the model (or its staged working
    /// copy), applies, and persists per the resolved mode. On a live source it applies in the
    /// session's transaction and commits, saving only on <c>--save</c>; a request that fails or
    /// throws leaves the live model as it found it.
    /// </summary>
    public static async Task<TomixResult<TResult>> RunAsync<TResult>(
        IModelSessionSource sessions,
        ModelReference model,
        MutationOptions options,
        string command,
        MutationStores stores,
        Func<IModelMutationSession, IModelSession, MutationContext, Task<(bool Changed, string Summary, Func<MutationOutcome, TResult> BuildResult)>> mutate,
        Func<MutationOutcome, TResult> revertResult,
        CancellationToken cancellationToken)
        => await ProviderConnectionGuard.RunAsync(
            model,
            () => RunCoreAsync(
                sessions, model, options, command, stores, mutate, revertResult, cancellationToken));

    private static async Task<TomixResult<TResult>> RunCoreAsync<TResult>(
        IModelSessionSource sessions,
        ModelReference model,
        MutationOptions options,
        string command,
        MutationStores stores,
        Func<IModelMutationSession, IModelSession, MutationContext, Task<(bool Changed, string Summary, Func<MutationOutcome, TResult> BuildResult)>> mutate,
        Func<MutationOutcome, TResult> revertResult,
        CancellationToken cancellationToken)
    {
        var stagingStore = stores.Staging;
        var connection = stores.ResolveSession();
        var target = MutationTarget.For(model, connection);

        var begin = await MutationLifecycle.BeginAsync(
            sessions, model, options, stagingStore, connection, cancellationToken);
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

            return TomixResult<TResult>.Ok(revertResult(MutationOutcome.Reverted with { Target = target }));
        }

        var context = begin.Context!;
        ModelSessionLease lease;
        try
        {
            lease = await sessions.LeaseAsync(context.EffectiveModel, cancellationToken);
        }
        catch (ModelSessionUnavailableException ex)
        {
            return ModelSessionRunner.Unavailable<TResult>(ex);
        }

        // Ending the lease without a commit rolls a live session back; every failure below does.
        await using var _ = lease;
        var session = lease.Session;
        if (session is not IModelMutationSession mutator)
            return TomixResult<TResult>.Fail(
                "TOMIX_MUTATION_UNSUPPORTED_PROVIDER",
                $"Provider cannot mutate model: {context.EffectiveModel.Value}");

        try
        {
            var validationBaseline = await SaveValidation.CaptureAsync(
                session, context, stores.ShouldValidateOnSave(), cancellationToken);
            var (changed, summary, buildResult) = await mutate(mutator, session, context);

            if (!changed)
            {
                await lease.CommitAsync(cancellationToken);
                return TomixResult<TResult>.Ok(buildResult(MutationOutcome.Unchanged with { Target = target }));
            }

            var completed = await MutationLifecycle.CompleteAsync(
                mutator, session, context, validationBaseline, command, summary, cancellationToken);
            var outcome = completed with { Target = MutationTarget.Merge(target, completed.Target) };
            await lease.CommitAsync(cancellationToken);

            // A failed workspace sync leaves the mirror behind the source; render the saved
            // result but exit non-zero so CI catches the drift.
            return TomixResult<TResult>.Ok(
                buildResult(outcome), outcome.SyncFailed ? 1 : 0,
                SaveValidation.ForcedNotice(context.Force ? outcome.Validation : null));
        }
        catch (SaveValidationBlockedException ex)
        {
            return SaveValidation.Blocked<TResult>(ex.Delta);
        }
        catch (Format.ExpressionFormatFailedException ex)
        {
            return Format.FormatFailure.Result<TResult>(ex.Message, ex.SyntaxErrors, ex.ObjectPath);
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
                hint: "Use --if-exists to ignore, or 'tx get <table>/RefreshPolicy' to inspect.");
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
        catch (Mv.MoveNoopException ex)
        {
            return TomixResult<TResult>.Fail("TOMIX_MOVE_NOOP", ex.Message);
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
