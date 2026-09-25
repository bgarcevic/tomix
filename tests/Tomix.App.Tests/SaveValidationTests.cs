using Tomix.App.Add;
using Tomix.App.Mutations;
using Tomix.App.Stage;
using Tomix.Core.Models;
using Tomix.Provider.Tmdl;

namespace Tomix.App.Tests;

public sealed class SaveValidationTests
{
    [Theory]
    [InlineData("SUM('Missing'[Amount])", 1, "DAX0001")]
    [InlineData("[Unknown]", 0, null)]
    [InlineData("1", 0, null)]
    public void Compare_GatesOnlyNewErrors(string expression, int expected, string? code)
    {
        var before = Snapshot(Measure("Existing", "SUM('Missing'[Amount])"));
        var after = Snapshot(
            Measure("Existing", "SUM('Missing'[Amount])"),
            Measure("Added", expression));

        var delta = SaveValidation.Compare(before, after);

        Assert.Equal(expected, delta.NewErrorCount);
        Assert.Equal(expected + 1, delta.ErrorCount);
        if (code is not null)
        {
            var issue = Assert.Single(delta.NewErrors);
            Assert.Equal(code, issue.Code);
            Assert.Equal("Sales/Added", issue.Object);
        }
    }

    [Fact]
    public void Compare_ScansCalculatedColumns()
    {
        var before = Snapshot();
        var after = Snapshot(Measure("Broken", "SUM('Missing'[Amount])") with
        {
            Kind = ModelObjectKind.CalculatedColumn
        });

        var issue = Assert.Single(SaveValidation.Compare(before, after).NewErrors);

        Assert.Equal("DAX0001", issue.Code);
        Assert.Equal("Sales/Broken", issue.Object);
    }

    [Theory]
    [InlineData(ModelObjectKind.Measure, "[Self]")]
    [InlineData(ModelObjectKind.Measure, "'Sales'[Self]")]
    [InlineData(ModelObjectKind.CalculatedColumn, "Sales[Self]")]
    public void Compare_BlocksDirectDaxSelfReference(ModelObjectKind kind, string expression)
    {
        var before = Snapshot();
        var after = Snapshot(Measure("Self", expression) with { Kind = kind });

        var issue = Assert.Single(SaveValidation.Compare(before, after).NewErrors);

        Assert.Equal("DAX0006", issue.Code);
        Assert.Equal("Sales/Self", issue.Object);
    }

    [Fact]
    public async Task AddSave_BlocksBeforeWriting_ForceAndConfigCanBypass()
    {
        using var config = new TempConfigDir();
        using var model = SampleModel.CopyToTemp();
        var reference = new ModelReference(model.Path);
        var initialFiles = Directory.GetFiles(model.Path, "*", SearchOption.AllDirectories)
            .ToDictionary(file => Path.GetRelativePath(model.Path, file), File.ReadAllBytes);
        var handler = new AddModelObjectHandler([new TmdlModelProvider()], config.Stores);

        var blocked = await handler.HandleAsync(Request(reference, "Broken", force: false), CancellationToken.None);

        Assert.False(blocked.Success);
        Assert.Equal(1, blocked.ExitCode);
        Assert.Equal("TOMIX_SAVE_VALIDATION_BLOCKED", Assert.Single(blocked.Diagnostics).Code);
        Assert.Equal("Sales/Broken", Assert.Single(blocked.Diagnostics[0].NewErrors!).Object);
        var currentFiles = Directory.GetFiles(model.Path, "*", SearchOption.AllDirectories)
            .ToDictionary(file => Path.GetRelativePath(model.Path, file), File.ReadAllBytes);
        Assert.Equal(initialFiles.Keys.Order(), currentFiles.Keys.Order());
        foreach (var (path, bytes) in initialFiles)
            Assert.Equal(bytes, currentFiles[path]);

        var selfReference = await handler.HandleAsync(
            Request(reference, "Self", force: false) with { Value = "[Self]" }, CancellationToken.None);
        Assert.False(selfReference.Success);
        Assert.Equal("DAX0006", Assert.Single(selfReference.Diagnostics[0].NewErrors!).Code);

        var forced = await handler.HandleAsync(Request(reference, "Broken", force: true), CancellationToken.None);
        Assert.True(forced.Success);
        Assert.Equal(1, forced.Data!.NewValidationErrors);
        Assert.Equal("TOMIX_SAVE_VALIDATION_FORCED", Assert.Single(forced.Diagnostics).Code);

        var valid = await handler.HandleAsync(
            Request(reference, "Valid", force: false) with { Value = "1" }, CancellationToken.None);
        Assert.True(valid.Success);
        Assert.Equal(0, valid.Data!.NewValidationErrors);

        var gateDisabled = new AddModelObjectHandler(
            [new TmdlModelProvider()], config.Stores with { ValidateOnSave = () => false });
        var uncheckedSave = await gateDisabled.HandleAsync(
            Request(reference, "Unchecked", force: false), CancellationToken.None);
        Assert.True(uncheckedSave.Success);
    }

    [Fact]
    public async Task AddSaveTo_BlocksBeforeCreatingTarget()
    {
        using var config = new TempConfigDir();
        using var model = SampleModel.CopyToTemp();
        using var output = new TempDir();
        var target = output.Combine("copy");
        var handler = new AddModelObjectHandler([new TmdlModelProvider()], config.Stores);

        var result = await handler.HandleAsync(
            Request(new ModelReference(model.Path), "Broken", force: false) with
            {
                Save = false,
                SaveTo = target
            }, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("TOMIX_SAVE_VALIDATION_BLOCKED", Assert.Single(result.Diagnostics).Code);
        Assert.False(Directory.Exists(target));
        Assert.False(File.Exists(target));
    }

    [Fact]
    public async Task StageCommit_BlocksPromotionAndKeepsWorkingCopy()
    {
        using var config = new TempConfigDir();
        using var model = SampleModel.CopyToTemp();
        var reference = new ModelReference(model.Path);
        var handler = new AddModelObjectHandler([new TmdlModelProvider()], config.Stores);
        var staged = await handler.HandleAsync(
            Request(reference, "Broken", force: false) with { Save = false, Stage = true },
            CancellationToken.None);
        Assert.True(staged.Success);

        var stage = new StageHandler(config.Staging);
        var blocked = await stage.CommitAsync(reference, [new TmdlModelProvider()], false, CancellationToken.None);

        Assert.False(blocked.Success);
        Assert.Equal("TOMIX_SAVE_VALIDATION_BLOCKED", Assert.Single(blocked.Diagnostics).Code);
        Assert.NotNull(config.Staging.TryLoad(reference));

        var forced = await stage.CommitAsync(reference, [new TmdlModelProvider()], true, CancellationToken.None);
        Assert.True(forced.Success);
        Assert.Equal("TOMIX_SAVE_VALIDATION_FORCED", Assert.Single(forced.Diagnostics).Code);
        Assert.Null(config.Staging.TryLoad(reference));
    }

    private static AddModelObjectRequest Request(ModelReference reference, string name, bool force)
        => new(reference, $"Sales/{name}", "Measure", "SUM('Missing'[Amount])", [],
            IfNotExists: false, Save: true, SaveTo: null, Serialization: "", Force: force);

    private static ModelSnapshot Snapshot(params ModelObject[] objects)
        => new("M", 1601, [MutationStubs.Table("Sales"), .. objects]);

    private static ModelObject Measure(string name, string expression)
        => MutationStubs.Measure(name, $"Sales/{name}", expression);
}
