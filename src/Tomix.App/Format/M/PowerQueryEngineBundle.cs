namespace Tomix.App.Format.M;

/// <summary>
/// The offline Power Query (M) engine: Microsoft's powerquery-parser and powerquery-formatter,
/// bundled by <c>engines/powerquery</c> into one script embedded in this assembly. Evaluating it
/// defines <c>globalThis.TomixM</c> with <c>version</c> (a JSON string) and <c>format</c> /
/// <c>diagnose</c>, each taking a JSON request string and resolving to a JSON response string.
/// See <c>engines/powerquery/README.md</c> for the contract and how to regenerate the bundle.
/// </summary>
internal static class PowerQueryEngineBundle
{
    internal const string ResourceName = "Tomix.App.Format.M.powerquery-engine.js";

    /// <summary>The global the bundle defines when evaluated.</summary>
    internal const string GlobalName = "TomixM";

    internal static string Load()
    {
        using var stream = typeof(PowerQueryEngineBundle).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded Power Query engine is unavailable: {ResourceName}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
