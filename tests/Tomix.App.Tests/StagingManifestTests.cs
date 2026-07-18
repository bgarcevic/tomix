using System.Text.Json;
using Tomix.App.State;
using Tomix.Core.Models;

namespace Tomix.App.Tests;

public sealed class StagingManifestTests
{
    [Fact]
    public void StagingManifest_SourceEndpoint_SourceDatabase_SetForRemote()
    {
        var manifest = new StagingManifest(
            SessionId: "test",
            Source: "powerbi://api.powerbi.com/v1.0/myorg/ws|MyModel",
            SourceKind: "remote",
            SourceEndpoint: "powerbi://api.powerbi.com/v1.0/myorg/ws",
            SourceDatabase: "MyModel",
            Workspace: null,
            Serialization: "tmdl",
            WorkingCopy: "/tmp/working",
            CreatedUtc: DateTimeOffset.UtcNow,
            UpdatedUtc: DateTimeOffset.UtcNow,
            SourceFingerprint: null,
            Ops: []);

        Assert.Equal("powerbi://api.powerbi.com/v1.0/myorg/ws", manifest.SourceEndpoint);
        Assert.Equal("MyModel", manifest.SourceDatabase);
        Assert.Equal("remote", manifest.SourceKind);
    }

    [Fact]
    public void StagingManifest_SourceEndpoint_SourceDatabase_NullForLocal()
    {
        var manifest = new StagingManifest(
            SessionId: "test",
            Source: "/home/user/model.tmdl",
            SourceKind: "local",
            SourceEndpoint: null,
            SourceDatabase: null,
            Workspace: null,
            Serialization: "tmdl",
            WorkingCopy: "/tmp/working",
            CreatedUtc: DateTimeOffset.UtcNow,
            UpdatedUtc: DateTimeOffset.UtcNow,
            SourceFingerprint: "abc123",
            Ops: []);

        Assert.Null(manifest.SourceEndpoint);
        Assert.Null(manifest.SourceDatabase);
        Assert.Equal("local", manifest.SourceKind);
    }

    [Fact]
    public void StagingManifest_RoundTrips_ThroughJson()
    {
        var original = new StagingManifest(
            SessionId: "s1",
            Source: "powerbi://api.powerbi.com/v1.0/myorg/ws|MyModel",
            SourceKind: "remote",
            SourceEndpoint: "powerbi://api.powerbi.com/v1.0/myorg/ws",
            SourceDatabase: "MyModel",
            Workspace: new StagingWorkspace("powerbi://api.powerbi.com/v1.0/myorg/ws", "MyModel"),
            Serialization: "tmdl",
            WorkingCopy: "/tmp/working",
            CreatedUtc: DateTimeOffset.UtcNow,
            UpdatedUtc: DateTimeOffset.UtcNow,
            SourceFingerprint: null,
            Ops: [new StagedOp(1, DateTimeOffset.UtcNow, "add table", "Added table X")]);

        var json = JsonSerializer.Serialize(original);
        var deserialized = JsonSerializer.Deserialize<StagingManifest>(json);

        Assert.NotNull(deserialized);
        Assert.Equal(original.SourceEndpoint, deserialized.SourceEndpoint);
        Assert.Equal(original.SourceDatabase, deserialized.SourceDatabase);
        Assert.Equal(original.SourceKind, deserialized.SourceKind);
        Assert.Equal(original.Ops.Count, deserialized.Ops.Count);
    }

    [Fact]
    public void StagingManifest_OldManifestDeserializes_WithNullEndpointFields()
    {
        var json = """
            {
              "SessionId": "s1",
              "Source": "/home/user/model.tmdl",
              "SourceKind": "local",
              "Workspace": null,
              "Serialization": "tmdl",
              "WorkingCopy": "/tmp/working",
              "CreatedUtc": "2025-01-01T00:00:00Z",
              "UpdatedUtc": "2025-01-01T00:00:00Z",
              "SourceFingerprint": "abc123",
              "Ops": []
            }
            """;

        var deserialized = JsonSerializer.Deserialize<StagingManifest>(json);

        Assert.NotNull(deserialized);
        Assert.Null(deserialized.SourceEndpoint);
        Assert.Null(deserialized.SourceDatabase);
        Assert.Equal("local", deserialized.SourceKind);
        // A manifest written before versioning IS version 1; it must never be rejected.
        Assert.Equal(StagingManifest.CurrentVersion, deserialized.Version);
    }

    [Fact]
    public void StagingManifest_Serializes_WithCurrentVersion()
    {
        var manifest = new StagingManifest(
            SessionId: "s1",
            Source: "/home/user/model.tmdl",
            SourceKind: "local",
            SourceEndpoint: null,
            SourceDatabase: null,
            Workspace: null,
            Serialization: "tmdl",
            WorkingCopy: "/tmp/working",
            CreatedUtc: DateTimeOffset.UtcNow,
            UpdatedUtc: DateTimeOffset.UtcNow,
            SourceFingerprint: null,
            Ops: []);

        var json = JsonSerializer.Serialize(manifest);

        Assert.Contains($"\"Version\":{StagingManifest.CurrentVersion}", json);
    }
}
