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
}
