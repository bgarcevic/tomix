using System.Data.Common;
using System.Net;
using System.Net.Sockets;

namespace Tomix.Core.Models;

/// <summary>
/// Points at a model to open. <see cref="Value"/> is either a local path (TMDL folder,
/// .bim/.tmsl file) or a remote endpoint: an XMLA URL (<c>powerbi://</c>, <c>asazure://</c>),
/// a local <c>localhost:&lt;port&gt;</c> Power BI Desktop instance, or an Analysis Services
/// server (<c>ssas01.contoso.com</c>, <c>10.0.0.5:2383</c>, <c>ssas01.contoso.com\TABULAR</c>,
/// or a full MSOLAP connection string). <see cref="Database"/> names the catalog/dataset for
/// remote endpoints and is ignored for local paths.
/// </summary>
public sealed record ModelReference(string Value, string? Database = null)
{
    /// <summary>True when <see cref="Value"/> is an XMLA endpoint rather than a file path.</summary>
    public bool IsRemote => IsRemoteEndpoint(Value);

    /// <summary>True for a remote endpoint that needs no access token (local Power BI Desktop).</summary>
    public bool IsLocalInstance => IsLocalInstanceEndpoint(Value);

    /// <summary>True when connecting to <see cref="Value"/> takes an Entra ID access token.</summary>
    public bool RequiresAccessToken => RequiresAccessTokenFor(Value);

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
            || IsLocalInstanceEndpoint(value)
            || IsAnalysisServicesServer(value);
    }

    public static bool IsLocalInstanceEndpoint(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        return value.StartsWith("localhost:", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("127.0.0.1:", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True for an explicit Analysis Services target that MSOLAP connects to as given: a full
    /// connection string (one with a data source or provider key), or a server written as a
    /// dotted host name, an IP address, <c>host:port</c>, or <c>host\instance</c>.
    /// </summary>
    /// <remarks>
    /// The classification is lexical, so it errs towards the two neighbours a server value can be
    /// confused with. A single-word name stays a Power BI workspace (<c>MyWorkspace</c>), and a
    /// dotted name ending in a model file or folder extension stays a path (<c>model.bim</c>,
    /// <c>Sales.SemanticModel</c>). A named instance on a single-word host
    /// (<c>SSAS01\TABULAR</c>) reads exactly like a relative path, so it needs the host's full
    /// name or a connection string.
    /// </remarks>
    public static bool IsAnalysisServicesServer(string? value)
        => !string.IsNullOrWhiteSpace(value) && (IsConnectionString(value) || IsServerName(value));

    /// <summary>
    /// True when <paramref name="value"/> is a connection string naming its server (a
    /// <c>Provider</c> or data source key) rather than an endpoint on its own.
    /// </summary>
    public static bool IsConnectionString(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || !value.Contains('='))
            return false;

        try
        {
            var builder = new DbConnectionStringBuilder { ConnectionString = value };
            return ConnectionStringServerKeys.Any(builder.ContainsKey);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// The server <paramref name="value"/> points at: the data source of a connection string, or
    /// the value itself for every other endpoint.
    /// </summary>
    public static string DataSourceOf(string value)
    {
        if (!IsConnectionString(value))
            return value;

        var builder = new DbConnectionStringBuilder { ConnectionString = value };
        foreach (var key in ConnectionStringDataSourceKeys)
        {
            if (builder.TryGetValue(key, out var dataSource) && dataSource?.ToString() is { Length: > 0 } text)
                return text;
        }

        return value;
    }

    /// <summary>
    /// True when connecting to <paramref name="value"/> takes an Entra ID access token. A local
    /// Power BI Desktop instance needs none, and neither does an Analysis Services server, which
    /// MSOLAP reaches with the caller's Windows identity (or credentials in its connection string).
    /// A connection string takes a token only when its data source is a cloud XMLA endpoint.
    /// </summary>
    public static bool RequiresAccessTokenFor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || IsLocalInstanceEndpoint(value))
            return false;

        if (IsConnectionString(value))
        {
            var dataSource = DataSourceOf(value);
            return dataSource.StartsWith("powerbi://", StringComparison.OrdinalIgnoreCase)
                || dataSource.StartsWith("asazure://", StringComparison.OrdinalIgnoreCase)
                || dataSource.StartsWith("link://", StringComparison.OrdinalIgnoreCase);
        }

        return !IsServerName(value);
    }

    /// <summary>
    /// True for a dotted two-part name such as <c>Sales.Prod</c>, which is read as an Analysis
    /// Services host but is just as plausibly a Power BI workspace name. Commands that resolve a
    /// server warn on it so a misrouted deploy is easy to spot.
    /// </summary>
    public static bool IsAmbiguousServerName(string? value)
        => !string.IsNullOrWhiteSpace(value)
            && IsDottedHostName(value)
            && value.Split('.').Length == 2;

    /// <summary>
    /// Normalizes a server/workspace value into a fully-qualified XMLA endpoint. Values that are
    /// already endpoints — an XMLA scheme (<c>powerbi://</c>, <c>asazure://</c>, <c>link://</c>),
    /// a local instance (<c>localhost:&lt;port&gt;</c> / <c>127.0.0.1:&lt;port&gt;</c>), an
    /// Analysis Services server (see <see cref="IsAnalysisServicesServer"/>), or anything
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
        // browser URLs are unescaped before being prefixed. An Analysis Services server goes
        // to MSOLAP as typed: prefixing it would send an on-premises deploy to Power BI.
        if (value.Contains("://", StringComparison.Ordinal) ||
            IsLocalInstanceEndpoint(value) ||
            IsAnalysisServicesServer(value))
            return value;

        var decoded = Uri.UnescapeDataString(value);
        return $"powerbi://api.powerbi.com/v1.0/myorg/{decoded}";
    }

    // Keys that make a key=value string a server connection string rather than, say, a
    // workspace name that happens to contain '='.
    private static readonly string[] ConnectionStringServerKeys = ["Data Source", "DataSource", "Provider", "Location"];

    private static readonly string[] ConnectionStringDataSourceKeys = ["Data Source", "DataSource", "Location"];

    // A dotted name ending in one of these is a model file or folder, not a host.
    private static readonly HashSet<string> ModelPathExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "bim", "tmsl", "tmdl", "json", "pbip", "pbix", "pbit", "vpax", "xmla", "zip",
        "SemanticModel", "Dataset", "Report",
    };

    // host, host:port or host\instance, where host is an IP address, a dotted host name, or
    // (with a port) any single host name label.
    private static bool IsServerName(string value)
    {
        if (value.Contains("://", StringComparison.Ordinal) || IsLocalInstanceEndpoint(value))
            return false;

        var backslash = value.IndexOf('\\');
        if (backslash >= 0)
        {
            var host = value[..backslash];
            var instance = value[(backslash + 1)..];
            return IsInstanceName(instance)
                && (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || IsIPv4Address(host) || IsDottedHostName(host));
        }

        var colon = value.LastIndexOf(':');
        if (colon >= 0)
        {
            var host = value[..colon];
            // A one-letter host is a drive letter, never a server.
            return host.Length > 1
                && ushort.TryParse(value.AsSpan(colon + 1), out var port) && port > 0
                && (IsIPv4Address(host) || IsDottedHostName(host) || IsHostNameLabel(host));
        }

        return IsIPv4Address(value) || IsDottedHostName(value);
    }

    private static bool IsIPv4Address(string value)
        => value.Count(c => c == '.') == 3
            && IPAddress.TryParse(value, out var address)
            && address.AddressFamily == AddressFamily.InterNetwork;

    private static bool IsDottedHostName(string value)
    {
        var labels = value.Split('.');
        return labels.Length >= 2
            && labels.All(IsHostNameLabel)
            && !labels[^1].All(char.IsAsciiDigit)
            && !ModelPathExtensions.Contains(labels[^1]);
    }

    private static bool IsHostNameLabel(string label)
        => label.Length is > 0 and <= 63
            && label[0] != '-' && label[^1] != '-'
            && label.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');

    // SSAS instance names: a letter, then letters, digits, '_' or '$', at most 16 characters.
    private static bool IsInstanceName(string instance)
        => instance.Length is > 0 and <= 16
            && char.IsAsciiLetter(instance[0])
            && instance.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '$');
}
