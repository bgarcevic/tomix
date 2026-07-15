using Tomix.Core.Models;

namespace Tomix.Core.Vertipaq;

/// <summary>
/// Produces VertiPaq storage statistics for a model. Unlike <see cref="IModelSession"/>
/// capabilities, this is a standalone service: statistics extraction opens its own engine
/// connection (DMV queries), and <see cref="ImportAsync"/> needs no model at all.
/// </summary>
public interface IVertipaqAnalyzer
{
    /// <summary>
    /// Extracts statistics from a live engine source (<c>powerbi://</c>, <c>asazure://</c>,
    /// or a local <c>localhost:&lt;port&gt;</c> instance).
    /// </summary>
    Task<VertipaqModelStats> AnalyzeAsync(ModelReference model, CancellationToken cancellationToken);

    /// <summary>Loads statistics from a previously exported <c>.vpax</c> file (offline).</summary>
    Task<VertipaqModelStats> ImportAsync(string vpaxPath, CancellationToken cancellationToken);

    /// <summary>
    /// Extracts statistics from a live source and writes them to a <c>.vpax</c> file.
    /// Returns the statistics so a single extraction serves both display and export.
    /// </summary>
    Task<VertipaqExportResult> ExportAsync(
        ModelReference model,
        string vpaxPath,
        bool obfuscate,
        CancellationToken cancellationToken);
}

/// <summary>
/// Outcome of <see cref="IVertipaqAnalyzer.ExportAsync"/>. <see cref="ObfuscationDictionaryPath"/>
/// is set only for obfuscated exports; the dictionary is required to deobfuscate and must be
/// kept private.
/// </summary>
public sealed record VertipaqExportResult(
    VertipaqModelStats Stats,
    string VpaxPath,
    string? ObfuscationDictionaryPath);
