using System.Text.Json.Serialization;
using Tomix.Core.Models;

namespace Tomix.App.Get;

/// <summary>One object's properties, as read by <c>get &lt;path&gt;</c>.</summary>
public sealed record GetObjectResult(
    string Type,
    string Path,
    IReadOnlyDictionary<string, object?> Properties,
    [property: JsonIgnore] ModelObject Object,
    [property: JsonIgnore] IReadOnlySet<string>? MeasureNames = null);
