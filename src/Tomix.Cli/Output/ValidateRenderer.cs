using Tomix.App.Validate;

namespace Tomix.Cli.Output;

/// <summary>
/// CI logging commands for <c>validate</c>. Validation issues carry no severity, so every
/// annotation is error-level; warnings are never emitted to CI.
/// </summary>
internal static class ValidateRenderer
{
    public static void EmitCi(string? ci, ValidateModelResult result)
    {
        if (result.Valid)
            return;

        var annotations = result.Errors
            .Select(e => new CiAnnotation(IsError: true, $"{e.Message} [{e.ObjectName}] ({e.Code})"))
            .ToList();

        CiAnnotations.Emit(ci, annotations, Console.Error);
    }

    /// <summary>
    /// TRX projection: one Failed test per error, one Warning test per warning, or a single
    /// Passed test when the model is clean, so an all-green run still shows up in CI.
    /// </summary>
    public static IReadOnlyList<TrxWriter.TrxTest> ToTrxTests(ValidateModelResult result)
    {
        var tests = new List<TrxWriter.TrxTest>();

        foreach (var error in result.Errors)
            tests.Add(new TrxWriter.TrxTest(
                $"{error.ObjectName} ({error.Code})",
                TrxWriter.TrxOutcome.Failed,
                Describe(error)));

        foreach (var warning in result.Warnings)
            tests.Add(new TrxWriter.TrxTest(
                $"{warning.ObjectName} ({warning.Code})",
                TrxWriter.TrxOutcome.Warning,
                Describe(warning)));

        if (tests.Count == 0)
            tests.Add(new TrxWriter.TrxTest("Model validation", TrxWriter.TrxOutcome.Passed));

        return tests;
    }

    private static string Describe(ValidationIssue issue)
        => string.IsNullOrEmpty(issue.Expression)
            ? issue.Message
            : $"{issue.Message}{Environment.NewLine}{issue.Expression}";
}
