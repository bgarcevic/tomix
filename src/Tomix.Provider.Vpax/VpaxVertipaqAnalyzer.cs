using Dax.Model.Extractor;
using Dax.Vpax.Obfuscator;
using Dax.Vpax.Tools;
using Tomix.Core.Authentication;
using Tomix.Core.Models;
using Tomix.Core.Vertipaq;
using Tomix.Provider.Tom;
using AsAccessToken = Microsoft.AnalysisServices.AccessToken;

namespace Tomix.Provider.Vpax;

/// <summary>
/// <see cref="IVertipaqAnalyzer"/> backed by the VertiPaq-Analyzer libraries. Extraction opens
/// its own engine connection (TOM metadata + DMV statistics) from a connection string, mirroring
/// the endpoint/token handling of <c>TomServerModelProvider</c>. Import reads a <c>.vpax</c>
/// package offline. All Dax.* and ADOMD types stay inside this project.
/// </summary>
public sealed class VpaxVertipaqAnalyzer : IVertipaqAnalyzer
{
    private const string ExtractorAppName = "tomix";

    private readonly IAccessTokenProvider? _tokenProvider;
    private readonly string _version;

    public VpaxVertipaqAnalyzer(IAccessTokenProvider? tokenProvider, string version)
    {
        _tokenProvider = tokenProvider;
        _version = version;
    }

    public async Task<VertipaqModelStats> AnalyzeAsync(ModelReference model, CancellationToken cancellationToken)
    {
        var daxModel = await ExtractDaxModelAsync(model, cancellationToken).ConfigureAwait(false);
        return VpaStatsMapper.Map(daxModel);
    }

    public Task<VertipaqModelStats> ImportAsync(string vpaxPath, CancellationToken cancellationToken)
        => Task.Run(() =>
        {
            try
            {
                var content = VpaxTools.ImportVpax(vpaxPath, importDatabase: false);
                if (content.DaxModel is null)
                    throw new VertipaqAnalysisException(
                        VertipaqAnalysisKind.VpaxReadFailed,
                        $"No statistics found in '{vpaxPath}'. The file does not contain a DaxModel part.");

                return VpaStatsMapper.Map(content.DaxModel);
            }
            catch (VertipaqAnalysisException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new VertipaqAnalysisException(
                    VertipaqAnalysisKind.VpaxReadFailed,
                    $"Could not read '{vpaxPath}': {ex.Message}",
                    ex);
            }
        }, cancellationToken);

    public async Task<VertipaqExportResult> ExportAsync(
        ModelReference model,
        string vpaxPath,
        bool obfuscate,
        CancellationToken cancellationToken)
    {
        var daxModel = await ExtractDaxModelAsync(model, cancellationToken).ConfigureAwait(false);
        var stats = VpaStatsMapper.Map(daxModel);

        var dictionaryPath = await Task.Run(() =>
        {
            try
            {
                return obfuscate
                    ? WriteObfuscated(daxModel, vpaxPath)
                    : WritePlain(model, daxModel, vpaxPath, cancellationToken);
            }
            catch (VertipaqAnalysisException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new VertipaqAnalysisException(
                    VertipaqAnalysisKind.VpaxWriteFailed,
                    $"Could not write '{vpaxPath}': {ex.Message}",
                    ex);
            }
        }, cancellationToken).ConfigureAwait(false);

        return new VertipaqExportResult(stats, vpaxPath, dictionaryPath);
    }

    private string? WritePlain(
        ModelReference model,
        Dax.Metadata.Model daxModel,
        string vpaxPath,
        CancellationToken cancellationToken)
    {
        var viewVpa = new Dax.ViewVpaExport.Model(daxModel);

        // Embed the TOM definition (Model.bim part) so the package is complete for other tools.
        // Requires a second metadata connection; a failure here should not lose the statistics,
        // so fall back to a package without the TOM part.
        Microsoft.AnalysisServices.Tabular.Database? database = null;
        try
        {
            var connection = BuildConnectionAsync(model, cancellationToken)
                .ConfigureAwait(false).GetAwaiter().GetResult();
            database = connection.AccessToken is null
                ? TomExtractor.GetDatabase(connection.ConnectionString)
                : TomExtractor.GetDatabase(connection.ConnectionString, connection.AccessToken.Value, connection.OnTokenExpired);
        }
        catch
        {
            database = null;
        }

        VpaxTools.ExportVpax(vpaxPath, daxModel, viewVpa, database);
        return null;
    }

    internal static string? WriteObfuscated(Dax.Metadata.Model daxModel, string vpaxPath)
    {
        // The obfuscator rewrites the DaxModel part and strips every other part from the
        // package, so skip the view/TOM parts instead of building and discarding them.
        using var stream = new MemoryStream();
        VpaxTools.ExportVpax(stream, daxModel, viewVpa: null, database: null);

        var dictionary = new VpaxObfuscator().Obfuscate(stream);
        var dictionaryPath = Path.ChangeExtension(vpaxPath, ".dict");
        dictionary.WriteTo(dictionaryPath, overwrite: true);

        stream.Position = 0;
        using var file = new FileStream(vpaxPath, FileMode.Create, FileAccess.Write, FileShare.Read);
        stream.CopyTo(file);
        return dictionaryPath;
    }

    private async Task<Dax.Metadata.Model> ExtractDaxModelAsync(ModelReference model, CancellationToken cancellationToken)
    {
        var connection = await BuildConnectionAsync(model, cancellationToken).ConfigureAwait(false);

        return await Task.Run(() =>
        {
            try
            {
                return connection.AccessToken is null
                    ? TomExtractor.GetDaxModel(connection.ConnectionString, ExtractorAppName, _version)
                    : TomExtractor.GetDaxModel(
                        connection.ConnectionString,
                        ExtractorAppName,
                        _version,
                        accessToken: connection.AccessToken.Value,
                        onTokenExpired: connection.OnTokenExpired);
            }
            catch (Exception ex)
            {
                throw new VertipaqAnalysisException(
                    VertipaqAnalysisKind.ExtractionFailed,
                    $"Statistics extraction failed: {ex.Message}",
                    ex);
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task<(string ConnectionString, AsAccessToken? AccessToken, Func<AsAccessToken, AsAccessToken>? OnTokenExpired)>
        BuildConnectionAsync(ModelReference model, CancellationToken cancellationToken)
    {
        var connectionString = $"Data Source={TomModelDeployer.ResolveEndpoint(model.Value)}";
        if (!string.IsNullOrWhiteSpace(model.Database))
            connectionString += $";Initial Catalog={model.Database}";

        if (model.IsLocalInstance)
            return (connectionString, null, null);

        if (_tokenProvider is null)
            throw new AuthenticationRequiredException("Not authenticated. Run 'tx auth login'.");

        var token = await _tokenProvider.GetTokenAsync(model.Value, cancellationToken).ConfigureAwait(false);

        AsAccessToken Refresh(AsAccessToken _)
        {
            var refreshed = _tokenProvider.GetTokenAsync(model.Value, cancellationToken)
                .ConfigureAwait(false).GetAwaiter().GetResult();
            return new AsAccessToken(refreshed.Token, refreshed.ExpiresOn.UtcDateTime);
        }

        return (connectionString, new AsAccessToken(token.Token, token.ExpiresOn.UtcDateTime), Refresh);
    }
}
