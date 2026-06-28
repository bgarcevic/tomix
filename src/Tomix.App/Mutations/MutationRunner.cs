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

        if (begin.Mode == MutationMode.Revert)
        {
            stagingStore.Discard(model);
            return TomixResult<TResult>.Ok(revertResult);
        }

        var context = begin.Context!;
        var provider = providers.FirstOrDefault(p => p.CanOpen(context.EffectiveModel));
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

            return TomixResult<TResult>.Ok(buildResult(outcome));
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
