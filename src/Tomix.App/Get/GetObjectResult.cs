using System.Text.Json.Serialization;
using Tomix.Core.Models;

namespace Tomix.App.Get;

/// <summary>
/// One object's properties, as read by <c>get &lt;path&gt;</c>. <see cref="Script"/> is the object
/// in the model file format the request asked for (<see cref="GetModelRequest.Script"/>).
/// </summary>
public sealed record GetObjectResult(
    string Type,
    string Path,
    IReadOnlyDictionary<string, object?> Properties,
    [property: JsonIgnore] ModelObject Object,
    [property: JsonIgnore] IReadOnlySet<string>? MeasureNames = null,
    [property: JsonIgnore] string? Script = null);
