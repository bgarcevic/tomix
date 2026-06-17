using Tomix.Core.Models;

namespace Tomix.App.Add;

public sealed record AddModelObjectRequest(
    ModelReference Model,
    string Path,
    string? Type,
    string? Value,
    IReadOnlyList<ModelPropertyAssignment> Properties,
    bool IfNotExists,
    bool Save,
    string? SaveTo,
    string Serialization,
    bool Force,
    bool Stage = false,
    bool Revert = false,
    string? Columns = null,
    string? Mode = null,
    string? Source = null,
    string? Endpoint = null,
    string? ConnectionString = null,
    string? SourceTable = null,
    string? SourceDatabase = null,
    string? PartitionExpression = null,
    string? SourceType = null);
