using Tomix.App.Get;
using Tomix.App.Rm;
using Tomix.App.Set;
using Tomix.App.Stage;
using Tomix.App.Tests.Support;
using Tomix.Core.Models;
using Tomix.Provider.Tmdl;
using Tomix.Provider.Tom;

namespace Tomix.App.Tests;

public sealed class RefreshPolicyWorkflowTests
{
    private static readonly IModelProvider[] Providers = [new TmdlModelProvider(), new TomFileModelProvider()];
    private static readonly ModelPropertyAssignment[] Properties =
    [
        new("RollingWindowGranularity", "year"), new("RollingWindowPeriods", "10"),
        new("IncrementalGranularity", "day"), new("IncrementalPeriods", "3"),
        new("SourceExpression", "let Source = #table({}, {}), Filtered = Table.SelectRows(Source, each [Date] >= RangeStart and [Date] < RangeEnd) in Filtered")
    ];

    [Theory]
    [InlineData("bim")]
    [InlineData("tmdl")]
    public async Task SaveTo_RoundTripsPolicyAndSupportsQuotedTablePaths(string serialization)
    {
        using var model = SampleModel.CopyToTemp();
        using var output = new TempDir();
        using var config = new TempConfigDir();
        var set = new SetModelPropertyHandler(Providers, config.Stores);
        var reference = new ModelReference(model.Path);
        var renamed = await set.HandleAsync(new SetModelPropertyRequest(reference, "Sales", [new("Name", "Net/Sales'2024")],
            ModelObjectKind.Table, true, null, "", NoSync: true), CancellationToken.None);
        Assert.True(renamed.Success);
        var path = "'Net/Sales''2024'/RefreshPolicy";
        var target = output.Combine(serialization == "bim" ? "model.bim" : "model");
        var saved = await set.HandleAsync(new SetModelPropertyRequest(reference, path, Properties, null,
            false, target, serialization, NoSync: true), CancellationToken.None);
        Assert.True(saved.Success, string.Join("; ", saved.Diagnostics.Select(d => d.Message)));
        var get = new GetModelHandler(Providers);
        var loaded = await get.HandleAsync(new(new(target), path, null, null), CancellationToken.None);
        Assert.True(loaded.Success, string.Join("; ", loaded.Diagnostics.Select(d => d.Message)));
        Assert.Equal(10, loaded.Data!.Properties["rollingWindowPeriods"]);
        Assert.Equal(path, loaded.Data.Path);
        Assert.False((await get.HandleAsync(new(reference, path, null, null), CancellationToken.None)).Success);
    }

    [Fact]
    public async Task Revert_DiscardsStagedPolicy()
    {
        using var model = SampleModel.CopyToTemp();
        using var config = new TempConfigDir();
        var handler = new SetModelPropertyHandler(Providers, config.Stores);
        var request = new SetModelPropertyRequest(new(model.Path), "Sales/RefreshPolicy", Properties, null, false, null, "", Stage: true);
        Assert.True((await handler.HandleAsync(request, CancellationToken.None)).Success);
        var result = await handler.HandleAsync(request with { Properties = [], Stage = false, Revert = true }, CancellationToken.None);
        Assert.True(result.Success);
        Assert.False(new StageHandler(config.Staging).Status(request.Model).Data!.Staged);
        Assert.False((await new GetModelHandler(Providers).HandleAsync(new(request.Model, request.Path, null, null), CancellationToken.None)).Success);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task PolicyLifecycle_PreviewSaveAndStage(bool save, bool stage)
    {
        using var model = SampleModel.CopyToTemp();
        using var config = new TempConfigDir();
        var reference = new ModelReference(model.Path);
        var request = new SetModelPropertyRequest(reference, "Sales/RefreshPolicy", Properties, null,
            save, null, "", Stage: stage, NoSync: true);
        var result = await new SetModelPropertyHandler(Providers, config.Stores).HandleAsync(request, CancellationToken.None);
        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        Assert.Equal(["RangeStart", "RangeEnd"], result.Data!.CreatedExpressions);
        var get = new GetModelHandler(Providers);
        var readRequest = new GetModelRequest(reference, "Sales/RefreshPolicy", null, null);
        var before = await get.HandleAsync(readRequest, CancellationToken.None);
        Assert.Equal(save, before.Success);
        if (stage)
        {
            var staging = new StageHandler(config.Staging);
            Assert.Equal("refresh-policy", Assert.Single(staging.Status(reference).Data!.Ops).Command);
            var committed = await staging.CommitAsync(reference, Providers, false, CancellationToken.None);
            Assert.True(committed.Success, string.Join("; ", committed.Diagnostics.Select(d => d.Message)));
        }
        if (!save && !stage)
            return;
        var read = await get.HandleAsync(readRequest, CancellationToken.None);
        Assert.True(read.Success);
        Assert.Equal(3, read.Data!.Properties["incrementalPeriods"]);
        Assert.True(read.Data.Properties.ContainsKey("issues"));
        Assert.True(read.Data.Properties.ContainsKey("policyPartitions"));
        var removed = await new RemoveModelObjectHandler(Providers, config.Stores).HandleAsync(
            new RemoveModelObjectRequest(reference, "Sales/RefreshPolicy", null, false, false, true, null, "", false, NoSync: true),
            CancellationToken.None);
        Assert.True(removed.Success);
        Assert.Equal("Sales/RefreshPolicy", removed.Data!.Removed);
        Assert.False((await get.HandleAsync(readRequest, CancellationToken.None)).Success);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidPolicy_RequiresForce(bool force)
    {
        using var model = SampleModel.CopyToTemp();
        using var config = new TempConfigDir();
        var properties = Properties.Select(p => p.Property == "SourceExpression" ? p with { Value = "let Source = Src in Source" } : p).ToArray();
        var result = await new SetModelPropertyHandler(Providers, config.Stores).HandleAsync(
            new SetModelPropertyRequest(new(model.Path), "Sales/RefreshPolicy", properties, null, true, null, "", NoSync: true, Force: force),
            CancellationToken.None);
        Assert.Equal(force, result.Success);
        if (force)
            Assert.Contains(result.Data!.Policy!.Issues, i => i.IsError);
        else
            Assert.Equal("TOMIX_REFRESH_POLICY_INVALID", result.Diagnostics[0].Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingPolicy_RemoveHonorsIfExists(bool ifExists)
    {
        using var model = SampleModel.CopyToTemp();
        using var config = new TempConfigDir();
        var result = await new RemoveModelObjectHandler(Providers, config.Stores).HandleAsync(
            new RemoveModelObjectRequest(new(model.Path), "Sales/RefreshPolicy", null, ifExists, false, true, null, "", false), CancellationToken.None);
        Assert.Equal(ifExists, result.Success);
        if (ifExists)
            Assert.Equal(false, result.Data!.Removed);
        else
            Assert.Equal("TOMIX_REFRESH_POLICY_NOT_FOUND", result.Diagnostics[0].Code);
    }
}
