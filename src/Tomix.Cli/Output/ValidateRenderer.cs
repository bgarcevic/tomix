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
}
