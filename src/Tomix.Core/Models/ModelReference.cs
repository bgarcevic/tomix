namespace Tomix.Core.Models;

/// <summary>
/// Points at a model to open. <see cref="Value"/> is either a local path (TMDL folder,
/// .bim/.tmsl file) or a remote XMLA endpoint (<c>powerbi://</c>, <c>asazure://</c>, or a
/// local <c>localhost:&lt;port&gt;</c> Power BI Desktop instance). <see cref="Database"/>
/// names the catalog/dataset for remote endpoints and is ignored for local paths.
/// </summary>
public sealed record ModelReference(string Value, string? Database = null)
{
    /// <summary>True when <see cref="Value"/> is an XMLA endpoint rather than a file path.</summary>
    public bool IsRemote => IsRemoteEndpoint(Value);

    /// <summary>True for a remote endpoint that needs no access token (local Power BI Desktop).</summary>
    public bool IsLocalInstance => IsLocalInstanceEndpoint(Value);

    public bool IsLocalPath => !IsRemote && !string.IsNullOrWhiteSpace(Value);

    public static ModelReference Remote(string endpoint, string? database = null)
        => new(endpoint, database);

    public static bool IsRemoteEndpoint(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        return value.StartsWith("powerbi://", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("asazure://", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("link://", StringComparison.OrdinalIgnoreCase)
            || IsLocalInstanceEndpoint(value);
    }

    public static bool IsLocalInstanceEndpoint(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        return value.StartsWith("localhost:", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("127.0.0.1:", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Normalizes a server/workspace value into a fully-qualified XMLA endpoint. Values that are
    /// already endpoints — an XMLA scheme (<c>powerbi://</c>, <c>asazure://</c>, <c>link://</c>),
    /// a local instance (<c>localhost:&lt;port&gt;</c> / <c>127.0.0.1:&lt;port&gt;</c>), or anything
    /// containing <c>://</c> — and empty values are returned unchanged. A bare workspace name such
    /// as <c>MyWorkspace</c> becomes <c>powerbi://api.powerbi.com/v1.0/myorg/MyWorkspace</c> so it
    /// can be opened by remote providers and stored as an active connection.
    /// </summary>
    /// <remarks>
    /// Bare workspace names pasted from browser URLs are unescaped first (e.g.
    /// <c>sandbox%20bkg</c> becomes <c>sandbox bkg</c>) so they resolve to the real name the
    /// XMLA endpoint expects. Already-formed endpoints (an XMLA scheme, a local instance, or
    /// anything containing <c>://</c>) are returned verbatim, which keeps this method
    /// idempotent: a value like <c>powerbi://.../Sales%20Archive</c> survives a second
    /// normalization pass at connect time instead of being decoded into <c>Sales Archive</c>.
    /// </remarks>
    public static string NormalizeEndpoint(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return value ?? string.Empty;

        // Already-formed endpoints are returned verbatim so the function is idempotent:
        // a percent-escaped workspace name (e.g. "Sales%20Archive") survives a second
        // normalization pass at connect time. Only bare workspace names pasted from
        // browser URLs are unescaped before being prefixed.
        if (value.Contains("://", StringComparison.Ordinal) ||
            value.StartsWith("localhost:", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("127.0.0.1:", StringComparison.OrdinalIgnoreCase))
            return value;

        var decoded = Uri.UnescapeDataString(value);
        return $"powerbi://api.powerbi.com/v1.0/myorg/{decoded}";
    }
}
