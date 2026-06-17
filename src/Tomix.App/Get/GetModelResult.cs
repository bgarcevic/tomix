using System.Text.Json.Serialization;
using Tomix.Core.Models;

namespace Tomix.App.Get;

public sealed record GetModelResult(
    string Type,
    string Path,
    IReadOnlyDictionary<string, object?> Properties,
    [property: JsonIgnore] ModelObject Object);
