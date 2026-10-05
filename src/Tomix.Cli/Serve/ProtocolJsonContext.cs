using System.Text.Json.Serialization;
using Tomix.Core.Models;

namespace Tomix.Cli.Serve;

/// <summary>
/// Source-generated JSON for the session events <c>tx serve</c> sends, so they need no runtime
/// reflection (trimming and Native AOT). Command results are not here: they are the commands'
/// own <c>--output-format json</c> text, parsed as it is.
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
[JsonSerializable(typeof(ModelChangeBatch))]
[JsonSerializable(typeof(SessionState))]
internal sealed partial class ProtocolJsonContext : JsonSerializerContext;
