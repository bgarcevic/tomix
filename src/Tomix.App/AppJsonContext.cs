using System.Text.Json.Serialization;
using Tomix.App.State;
using Tomix.App.Update;

namespace Tomix.App;

/// <summary>
/// Source-generated JSON metadata for the files Tomix persists under its config directory
/// (config, profiles, recents, session state, staging manifests, update check), so reading
/// and writing them needs no runtime reflection and works in trimmed and Native AOT builds.
/// Indented output matches what these files have always looked like; reading is unaffected
/// by indentation.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(Dictionary<string, CliProfile>))]
[JsonSerializable(typeof(List<RecentConnection>))]
[JsonSerializable(typeof(CliConnectionState))]
[JsonSerializable(typeof(StagingManifest))]
[JsonSerializable(typeof(UpdateCheckState))]
[JsonSerializable(typeof(string[]))]
internal sealed partial class AppJsonContext : JsonSerializerContext;
