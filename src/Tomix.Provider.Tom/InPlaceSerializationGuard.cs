namespace Tomix.Provider.Tom;

/// <summary>
/// Resolves the effective serialization for a mutation save. An in-place save must keep the
/// source format: exporting a TMDL folder "in place" as bim would drop a stray
/// <c>definition.bim</c> next to the real definition and leave the actual model untouched —
/// a silent no-op that still reports "Saved".
/// </summary>
public static class InPlaceSerializationGuard
{
    public static string Resolve(bool inPlace, string requestedSerialization, string sourceFormat)
    {
        var requested = Normalize(requestedSerialization, sourceFormat);
        if (inPlace && !string.Equals(requested, sourceFormat, StringComparison.Ordinal))
            throw new NotSupportedException(
                $"Cannot save a {sourceFormat} model in place as {requested}. "
                + $"Use --save-to <path> to write a {requested} copy.");

        return requested;
    }

    private static string Normalize(string serialization, string sourceFormat)
        => serialization.Trim().ToLowerInvariant() switch
        {
            "" or "auto" => sourceFormat,
            "tmsl" => "bim",
            var other => other
        };
}
