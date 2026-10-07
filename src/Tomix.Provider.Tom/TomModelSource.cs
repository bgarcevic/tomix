using Microsoft.AnalysisServices.Tabular;
using Tomix.Core.Authentication;
using Tomix.Core.Models;
using TabularServer = Microsoft.AnalysisServices.Tabular.Server;

namespace Tomix.Provider.Tom;

/// <summary>
/// Where a live session's model comes from and goes back to: how to load it, name it, save and
/// export it, and how its checkpoints restore (ADR 0003 §1). Everything else a session does is
/// the same for every source.
/// </summary>
internal abstract class TomModelSource : IAsyncDisposable
{
    protected TomModelSource(ModelReference reference, IAccessTokenProvider? tokenProvider)
    {
        Reference = reference;
        TokenProvider = tokenProvider;
    }

    public ModelReference Reference { get; }

    public IAccessTokenProvider? TokenProvider { get; }

    public abstract string SourcePath { get; }

    /// <summary>The source as messages name it.</summary>
    public virtual string DisplayName => SourcePath;

    public abstract TomCheckpointRestore Restore { get; }

    public abstract Database Load();

    public abstract string ModelName(Database database);

    public ModelSummary Summarize(Database database)
        => TomModelSummarizer.Summarize(database, ModelName(database))
            with
        { DatabaseName = string.IsNullOrWhiteSpace(database.Name) ? null : database.Name };

    public abstract Task<ModelExportResult> ExportAsync(Database database, ModelExportRequest request, CancellationToken cancellationToken);

    public abstract Task<ModelExportResult> SaveAsync(Database database, string? outputPath, string serialization, bool overwrite, CancellationToken cancellationToken);

    /// <summary>Whether a save to <paramref name="outputPath"/> writes the source itself, and so
    /// moves the session's save point.</summary>
    public abstract bool IsInPlace(string? outputPath);

    public abstract ValueTask DisposeAsync();

    /// <summary>Whether <see cref="Load"/> reads the source again, so a session can reload it.</summary>
    public virtual bool CanReload => false;

    /// <summary>
    /// What the source holds now, for telling whether it changed outside the session (#351): equal
    /// fingerprints mean equal content. <c>null</c> when the source cannot tell.
    /// </summary>
    public virtual string? Fingerprint() => null;

    /// <summary>Calls <paramref name="changed"/> when something may have changed the source; the
    /// caller compares <see cref="Fingerprint"/>s to know. <c>null</c> when the source cannot be watched.</summary>
    public virtual IDisposable? Watch(Action changed) => null;

    /// <summary>A TMDL folder, <c>.bim</c> or TMSL file, saved in place in
    /// <paramref name="sourceFormat"/>.</summary>
    public static TomModelSource File(ModelReference reference, string path, string sourceFormat, Func<string, Database> load, IAccessTokenProvider? tokenProvider)
        => new FileSource(reference, path, sourceFormat, load, tokenProvider);

    private sealed class FileSource(ModelReference reference, string path, string sourceFormat, Func<string, Database> load, IAccessTokenProvider? tokenProvider)
        : TomModelSource(reference, tokenProvider)
    {
        public override string SourcePath => path;

        public override TomCheckpointRestore Restore => TomCheckpointRestore.Swap;

        public override Database Load() => load(path);

        public override ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public override bool CanReload => true;

        public override string? Fingerprint() => SourceFingerprint.Of(path);

        public override IDisposable? Watch(Action changed) => SourceFingerprint.Watch(path, changed);

        public override bool IsInPlace(string? outputPath)
            => string.IsNullOrWhiteSpace(outputPath) || SamePath(outputPath, path);

        public override string ModelName(Database database) => ModelDisplayName.Resolve(database.Name, path);

        public override Task<ModelExportResult> ExportAsync(Database database, ModelExportRequest request, CancellationToken cancellationToken)
            => TomModelExporter.ExportAsync(
                database,
                SamePath(request.OutputPath, path) ? request with { Overwrite = true } : request,
                cancellationToken);

        public override Task<ModelExportResult> SaveAsync(Database database, string? outputPath, string serialization, bool overwrite, CancellationToken cancellationToken)
        {
            var inPlace = IsInPlace(outputPath);
            var format = InPlaceSerializationGuard.Resolve(inPlace, serialization, sourceFormat);
            return TomModelExporter.ExportAsync(
                database,
                new ModelExportRequest(
                    string.IsNullOrWhiteSpace(outputPath) ? path : outputPath,
                    format,
                    Overwrite: overwrite || inPlace,
                    SupportingFiles: false),
                cancellationToken);
        }

        private static bool SamePath(string a, string b)
        {
            try
            {
                return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
            }
        }
    }
}

/// <summary>
/// A database on an XMLA server or in Power BI Desktop. The session owns the connection. The
/// <see cref="Database"/> belongs to its <see cref="TabularServer"/> and cannot be swapped, so
/// checkpoints restore with <c>CopyTo</c>, and a session cannot reload it.
/// </summary>
/// <remarks>
/// The server's model is watched by polling its <c>TMSCHEMA_*</c> rowsets (#351): a model-level
/// timestamp does not move when a child object such as an annotation or a measure changes.
/// Every call that reaches the server first checks that a Power BI Desktop instance is still
/// listening, so a session that outlived Desktop fails with <see cref="ModelSourceUnavailableException"/>.
/// </remarks>
internal sealed class TomServerModelSource(TabularServer server, Database database, ModelReference reference, IAccessTokenProvider? tokenProvider)
    : TomModelSource(reference, tokenProvider)
{
    /// <summary>
    /// The rowsets of objects with a <c>ModifiedTime</c>: a change to one moves its time, and
    /// adding or removing one changes the row count.
    /// </summary>
    internal static readonly IReadOnlyList<string> TimedRowsets =
    [
        "MODEL", "DATA_SOURCES", "TABLES", "COLUMNS", "PARTITIONS", "RELATIONSHIPS", "MEASURES",
        "HIERARCHIES", "LEVELS", "ANNOTATIONS", "KPIS", "CULTURES", "OBJECT_TRANSLATIONS",
        "LINGUISTIC_METADATA", "PERSPECTIVES", "PERSPECTIVE_TABLES", "PERSPECTIVE_COLUMNS",
        "PERSPECTIVE_HIERARCHIES", "PERSPECTIVE_MEASURES", "ROLES", "ROLE_MEMBERSHIPS",
        "TABLE_PERMISSIONS", "COLUMN_PERMISSIONS", "EXPRESSIONS", "DETAIL_ROWS_DEFINITIONS",
        "EXTENDED_PROPERTIES", "FORMAT_STRING_DEFINITIONS", "CALCULATION_GROUPS",
        "CALCULATION_ITEMS", "DATA_COVERAGE_DEFINITIONS"
    ];

    /// <summary>The rowsets without a <c>ModifiedTime</c>, compared by content. They are small.</summary>
    internal static readonly IReadOnlyList<string> UntimedRowsets =
    [
        "VARIATIONS", "QUERY_GROUPS", "REFRESH_POLICIES", "ALTERNATE_OF", "FUNCTIONS", "CALENDARS",
        "CALENDAR_COLUMN_GROUPS", "CALENDAR_COLUMN_REFERENCES", "BINDING_INFOS"
    ];

    // The rowsets this server has, found on the first fingerprint: older servers and lower
    // compatibility levels lack some, and they are skipped from then on.
    private IReadOnlyList<(string Rowset, bool Timed)>? _rowsets;

    public TabularServer Server => server;

    public override string SourcePath => "";

    public override string DisplayName => $"'{ModelName(database)}' on {Reference.Value}";

    public override TomCheckpointRestore Restore => TomCheckpointRestore.CopyTo;

    public override Database Load() => database;

    /// <summary>A save always sends the edits to the server, whatever else it writes.</summary>
    public override bool IsInPlace(string? outputPath) => true;

    public override string ModelName(Database database)
        => ModelDisplayName.Resolve(database.Name, sourcePath: null, fallback: database.ID);

    public override Task<ModelExportResult> ExportAsync(Database database, ModelExportRequest request, CancellationToken cancellationToken)
        => TomModelExporter.ExportAsync(database, request, cancellationToken);

    public override Task<ModelExportResult> SaveAsync(Database database, string? outputPath, string serialization, bool overwrite, CancellationToken cancellationToken)
        => Reach(() => TomServerModelSession.SaveAsync(database, ModelName(database), outputPath, serialization, overwrite, cancellationToken));

    /// <summary>
    /// A hash of every rowset's row count and latest <c>ModifiedTime</c> (or content, for the
    /// rowsets without one). A refresh can move it. Callers serialize it with every other use of
    /// <see cref="Server"/>.
    /// </summary>
    public override string? Fingerprint()
        => Reach(() =>
        {
            var probing = _rowsets is null;
            var rowsets = _rowsets ?? [.. TimedRowsets.Select(r => (r, true)), .. UntimedRowsets.Select(r => (r, false))];
            var found = new List<(string, bool)>();
            var properties = new Dictionary<string, string> { ["Catalog"] = database.Name };
            using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
            foreach (var (rowset, timed) in rowsets)
            {
                var query = $"<Statement xmlns=\"urn:schemas-microsoft-com:xml-analysis\">SELECT {(timed ? "[ModifiedTime]" : "*")} FROM $SYSTEM.TMSCHEMA_{rowset}</Statement>";
                using var rows = server.ExecuteReader(query, out var results, properties, true);
                if (rows is null)
                {
                    if (probing)
                        continue;
                    throw new InvalidOperationException($"The server did not say whether {DisplayName} changed: {XmlaMessages(results)}");
                }

                found.Add((rowset, timed));
                hash.AppendData(System.Text.Encoding.UTF8.GetBytes($"{rowset}:{(timed ? TimedPartOf(rows) : ContentPartOf(rows))}\n"));
            }

            _rowsets ??= found;
            return Convert.ToHexString(hash.GetHashAndReset());
        });

    public override IDisposable? Watch(Action changed)
    {
        var every = PollInterval(Reference);
        return new Timer(_ => changed(), null, every, every);
    }

    /// <summary>How often the session asks the server whether its model changed: often for a
    /// local Power BI Desktop, less for a remote server, which answers each poll with ~40 queries.</summary>
    internal static TimeSpan PollInterval(ModelReference reference)
        => reference.IsLocalInstance ? TimeSpan.FromSeconds(10) : TimeSpan.FromSeconds(30);

    /// <summary>Runs <paramref name="call"/> against the server, failing fast with
    /// <see cref="ModelSourceUnavailableException"/> when it cannot be reached.</summary>
    public T Reach<T>(Func<T> call)
    {
        if (LocalInstanceGone(Reference.Value, IsPortListening))
            throw Unavailable(null);

        try
        {
            return call();
        }
        catch (Microsoft.AnalysisServices.ConnectionException ex)
        {
            throw Unavailable(ex);
        }
    }

    public override ValueTask DisposeAsync()
    {
        if (server.Connected)
            server.Disconnect();
        server.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>A rowset of timed objects as its row count and latest <c>ModifiedTime</c>.</summary>
    internal static string TimedPartOf(System.Data.IDataReader rows)
    {
        var count = 0;
        DateTime? latest = null;
        while (rows.Read())
        {
            count++;
            if (rows["ModifiedTime"] is DateTime at && (latest is null || at > latest))
                latest = at;
        }

        return $"{count}|{latest?.ToString("O", System.Globalization.CultureInfo.InvariantCulture)}";
    }

    /// <summary>A rowset as its rows' values, in an order that does not depend on the server's.</summary>
    internal static string ContentPartOf(System.Data.IDataReader rows)
    {
        var lines = new List<string>();
        var values = new object[rows.FieldCount];
        while (rows.Read())
        {
            rows.GetValues(values);
            lines.Add(string.Join("\u001f", values.Select(value => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture))));
        }

        lines.Sort(StringComparer.Ordinal);
        return string.Join("\u001e", lines);
    }

    /// <summary>Whether <paramref name="endpoint"/> is a local instance (Power BI Desktop) that no
    /// longer listens: nothing is left to answer, so a call would only fail or wait.</summary>
    internal static bool LocalInstanceGone(string endpoint, Func<int, bool> isListening)
    {
        if (!ModelReference.IsLocalInstanceEndpoint(endpoint))
            return false;

        var separator = endpoint.LastIndexOf(':');
        return int.TryParse(endpoint.AsSpan(separator + 1), out var port) && !isListening(port);
    }

    private static string XmlaMessages(Microsoft.AnalysisServices.XmlaResultCollection? results)
    {
        var messages = new List<string>();
        if (results is not null)
            XmlaResultHelper.ExtractMessages(results, messages);
        return messages.Count > 0 ? string.Join("; ", messages) : "no rows.";
    }

    private ModelSourceUnavailableException Unavailable(Exception? inner)
        => new(
            DisplayName,
            ModelReference.IsLocalInstanceEndpoint(Reference.Value)
                ? $"The Power BI Desktop instance at {Reference.Value} is no longer running."
                : $"The server at {Reference.Value} cannot be reached: {inner?.Message}",
            inner);

    internal static bool IsPortListening(int port)
    {
        try
        {
            return System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties()
                .GetActiveTcpListeners().Any(listener => listener.Port == port);
        }
        catch (Exception ex) when (ex is System.Net.NetworkInformation.NetworkInformationException or PlatformNotSupportedException)
        {
            // Cannot tell: let the call find out.
            return true;
        }
    }
}
