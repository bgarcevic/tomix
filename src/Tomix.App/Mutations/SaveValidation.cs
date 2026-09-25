using Tomix.App.Validate;
using Tomix.Core.Diagnostics;
using Tomix.Core.Models;
using Tomix.Core.Results;

namespace Tomix.App.Mutations;

/// <summary>The source snapshot for a requested save and whether new errors block it.</summary>
public sealed record SaveValidationBaseline(ModelSnapshot Snapshot, bool Enforce);

/// <summary>The validation delta between a source model and a proposed saved model.</summary>
public sealed record SaveValidationDelta(int ErrorCount, IReadOnlyList<ValidationErrorDetail> NewErrors)
{
    public int NewErrorCount => NewErrors.Count;
}

/// <summary>Raised before persistence when the proposed model introduces validation errors.</summary>
public sealed class SaveValidationBlockedException(SaveValidationDelta delta) : Exception(
    $"Save blocked: this mutation introduced {delta.NewErrorCount} validation error(s). Use --force to save anyway.")
{
    public SaveValidationDelta Delta { get; } = delta;
}

/// <summary>Compares models with the same offline analyzer used by <c>tx validate</c>.</summary>
public static class SaveValidation
{
    public static async Task<SaveValidationBaseline?> CaptureAsync(
        IModelSession session,
        MutationContext context,
        bool validateOnSave,
        CancellationToken cancellationToken)
    {
        if (context.Mode != MutationMode.Save || (!validateOnSave && !context.Force))
            return null;

        return ForSnapshot(
            await session.GetSnapshotAsync(cancellationToken), context, validateOnSave);
    }

    public static SaveValidationBaseline? ForSnapshot(
        ModelSnapshot snapshot,
        MutationContext context,
        bool validateOnSave)
        => context.Mode == MutationMode.Save && (validateOnSave || context.Force)
            ? new SaveValidationBaseline(snapshot, Enforce: validateOnSave && !context.Force)
            : null;

    public static SaveValidationDelta Compare(ModelSnapshot before, ModelSnapshot after)
    {
        static (string Code, string Object, string Message) Key(ValidationIssue issue)
            => (issue.Code, issue.ObjectName, issue.Message);

        var oldErrors = ModelValidation.Analyze(before).Issues
            .Where(issue => issue.Severity == ValidationSeverity.Error)
            .Select(Key)
            .ToHashSet();
        var errors = ModelValidation.Analyze(after).Issues
            .Where(issue => issue.Severity == ValidationSeverity.Error)
            .ToList();
        var introduced = errors
            .Where(issue => !oldErrors.Contains(Key(issue)))
            .Select(issue => new ValidationErrorDetail(issue.Code, issue.Message, issue.ObjectName))
            .ToList();
        return new SaveValidationDelta(errors.Count, introduced);
    }

    public static TomixResult<T> Blocked<T>(SaveValidationDelta delta)
        => new(
            Success: false,
            Data: default,
            Diagnostics:
            [
                new TomixDiagnostic(
                    "TOMIX_SAVE_VALIDATION_BLOCKED",
                    DiagnosticSeverity.Error,
                    $"Save blocked: this mutation introduced {delta.NewErrorCount} validation error(s). Use --force to save anyway.",
                    Blocked: true,
                    Reason: "validation",
                    NewValidationErrorCount: delta.NewErrorCount,
                    NewErrors: delta.NewErrors)
            ],
            ExitCode: 1);

    public static IReadOnlyList<TomixDiagnostic> ForcedNotice(SaveValidationDelta? delta)
        => delta is { NewErrorCount: > 0 }
            ?
            [
                new TomixDiagnostic(
                    "TOMIX_SAVE_VALIDATION_FORCED",
                    DiagnosticSeverity.Warning,
                    $"Saved with --force despite {delta.NewErrorCount} newly introduced validation error(s).",
                    NewValidationErrorCount: delta.NewErrorCount,
                    NewErrors: delta.NewErrors)
            ]
            : [];
}
