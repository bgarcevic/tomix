using Tomix.App.Bpa;
using Tomix.Core.Bpa;
using Tomix.Core.Models;

namespace Tomix.App.Tests;

public sealed class BpaFixerTests
{
    [Fact]
    public void TryParseSimpleAssignment_BooleanFalse()
    {
        var ok = BpaFixer.TryParseSimpleAssignment("IsAvailableInMDX = false", out var assignments);
        Assert.True(ok);
        var assignment = Assert.Single(assignments);
        Assert.Equal("IsAvailableInMDX", assignment.Property);
        Assert.Equal("false", assignment.Value);
    }

    [Fact]
    public void TryParseSimpleAssignment_BooleanTrue()
    {
        var ok = BpaFixer.TryParseSimpleAssignment("IsHidden = true", out var assignments);
        Assert.True(ok);
        var assignment = Assert.Single(assignments);
        Assert.Equal("IsHidden", assignment.Property);
        Assert.Equal("true", assignment.Value);
    }

    [Fact]
    public void TryParseSimpleAssignment_QuotedString()
    {
        var ok = BpaFixer.TryParseSimpleAssignment("FormatString = \"dd-mm-yyyy\"", out var assignments);
        Assert.True(ok);
        var assignment = Assert.Single(assignments);
        Assert.Equal("FormatString", assignment.Property);
        Assert.Equal("dd-mm-yyyy", assignment.Value);
    }

    [Fact]
    public void TryParseSimpleAssignment_QuotedStringWithSpaces()
    {
        var ok = BpaFixer.TryParseSimpleAssignment("FormatString = \"#,##0.0 %\"", out var assignments);
        Assert.True(ok);
        var assignment = Assert.Single(assignments);
        Assert.Equal("FormatString", assignment.Property);
        Assert.Equal("#,##0.0 %", assignment.Value);
    }

    [Fact]
    public void TryParseSimpleAssignment_EnumValue()
    {
        var ok = BpaFixer.TryParseSimpleAssignment("SummarizeBy = AggregateFunction.None", out var assignments);
        Assert.True(ok);
        var assignment = Assert.Single(assignments);
        Assert.Equal("SummarizeBy", assignment.Property);
        Assert.Equal("None", assignment.Value);
    }

    [Fact]
    public void TryParseSimpleAssignment_DataTypeEnum()
    {
        var ok = BpaFixer.TryParseSimpleAssignment("DataType = DataType.Decimal", out var assignments);
        Assert.True(ok);
        var assignment = Assert.Single(assignments);
        Assert.Equal("DataType", assignment.Property);
        Assert.Equal("Decimal", assignment.Value);
    }

    [Fact]
    public void TryParseSimpleAssignment_DeleteNotParsed()
    {
        var ok = BpaFixer.TryParseSimpleAssignment("Delete()", out _);
        Assert.False(ok);
    }

    [Fact]
    public void TryParseSimpleAssignment_ComplexExpressionNotParsed()
    {
        var ok = BpaFixer.TryParseSimpleAssignment("Name = string.Concat(it.Name.ToCharArray())", out _);
        Assert.False(ok);
    }

    [Fact]
    public void ApplyFixes_SimplePropertyChange()
    {
        var session = new StubMutationSession();
        var fixer = new BpaFixer();

        var rules = new List<BpaRule>
        {
            new("HIDE_FK", "Hide FK", "Formatting", BpaSeverity.Warning,
                ["Column"], FixExpression: "IsHidden = true")
        };

        var violations = new List<BpaViolation>
        {
            new("HIDE_FK", "Hide FK", "Formatting", BpaSeverity.Warning,
                "Column", "'Orders'[CustomerId]", "Orders/CustomerId",
                CanFix: true, ObjectKind: ModelObjectKind.Column)
        };

        var result = fixer.ApplyFixes(session, violations, rules);

        Assert.Equal(1, result.FixesApplied);
        Assert.Equal(0, result.FixesSkipped);
        Assert.Empty(result.Errors);
        Assert.Equal("Orders/CustomerId", session.LastSetPath);
        Assert.Equal("IsHidden", session.LastSetAssignments?[0].Property);
        Assert.Equal("true", session.LastSetAssignments?[0].Value);
    }

    [Fact]
    public void ApplyFixes_DeleteViolation_SkippedByDefault()
    {
        var session = new StubMutationSession();
        var fixer = new BpaFixer();

        var rules = new List<BpaRule>
        {
            new("UNNECESSARY_COL", "Remove", "Performance", BpaSeverity.Warning,
                ["Column"], FixExpression: "Delete()")
        };

        var violations = new List<BpaViolation>
        {
            new("UNNECESSARY_COL", "Remove", "Performance", BpaSeverity.Warning,
                "Column", "'Table'[Col]", "Table/Col",
                CanFix: true, ObjectKind: ModelObjectKind.Column)
        };

        var result = fixer.ApplyFixes(session, violations, rules);

        Assert.Equal(0, result.FixesApplied);
        Assert.Equal(1, result.DestructiveFixesSkipped);
        Assert.Empty(result.Errors);
        Assert.Null(session.LastRemovedPath);
    }

    [Fact]
    public void ApplyFixes_DeleteViolation_AppliedWithAllowDelete()
    {
        var session = new StubMutationSession();
        var fixer = new BpaFixer();

        var rules = new List<BpaRule>
        {
            new("UNNECESSARY_COL", "Remove", "Performance", BpaSeverity.Warning,
                ["Column"], FixExpression: "Delete()")
        };

        var violations = new List<BpaViolation>
        {
            new("UNNECESSARY_COL", "Remove", "Performance", BpaSeverity.Warning,
                "Column", "'Table'[Col]", "Table/Col",
                CanFix: true, ObjectKind: ModelObjectKind.Column)
        };

        var result = fixer.ApplyFixes(session, violations, rules, allowDelete: true);

        Assert.Equal(1, result.FixesApplied);
        Assert.Equal(0, result.DestructiveFixesSkipped);
        Assert.Equal("Table/Col", session.LastRemovedPath);
        Assert.Equal(ModelObjectKind.Column, session.LastRemovedKind);
    }

    [Fact]
    public void ApplyFixes_MixedFixes_AppliesPropertySetsAndGatesDeletes()
    {
        var session = new StubMutationSession();
        var fixer = new BpaFixer();

        var rules = new List<BpaRule>
        {
            new("HIDE_FK", "Hide FK", "Formatting", BpaSeverity.Warning,
                ["Column"], FixExpression: "IsHidden = true"),
            new("UNNECESSARY_COL", "Remove", "Maintenance", BpaSeverity.Warning,
                ["Column"], FixExpression: "Delete()")
        };

        var violations = new List<BpaViolation>
        {
            new("HIDE_FK", "Hide FK", "Formatting", BpaSeverity.Warning,
                "Column", "'Orders'[CustomerId]", "Orders/CustomerId",
                CanFix: true, ObjectKind: ModelObjectKind.Column),
            new("UNNECESSARY_COL", "Remove", "Maintenance", BpaSeverity.Warning,
                "Column", "'Table'[Col]", "Table/Col",
                CanFix: true, ObjectKind: ModelObjectKind.Column)
        };

        var result = fixer.ApplyFixes(session, violations, rules);

        Assert.Equal(1, result.FixesApplied);
        Assert.Equal(1, result.DestructiveFixesSkipped);
        Assert.Equal("Orders/CustomerId", session.LastSetPath);
        Assert.Null(session.LastRemovedPath);
    }

    [Fact]
    public void ApplyFixes_UnsupportedExpressionSkipped()
    {
        var session = new StubMutationSession();
        var fixer = new BpaFixer();

        var rules = new List<BpaRule>
        {
            new("COMPLEX", "Complex fix", "Formatting", BpaSeverity.Warning,
                ["Column"], FixExpression: "Name = string.Concat(it.Name)")
        };

        var violations = new List<BpaViolation>
        {
            new("COMPLEX", "Complex fix", "Formatting", BpaSeverity.Warning,
                "Column", "'T'[C]", "T/C",
                CanFix: true, ObjectKind: ModelObjectKind.Column)
        };

        var result = fixer.ApplyFixes(session, violations, rules);

        Assert.Equal(0, result.FixesApplied);
        Assert.Equal(1, result.FixesSkipped);
        Assert.Single(result.Errors);
    }

    [Fact]
    public void ApplyFixes_OnlyFixableViolationsProcessed()
    {
        var session = new StubMutationSession();
        var fixer = new BpaFixer();

        var rules = new List<BpaRule>
        {
            new("FIXABLE", "Fixable", "Formatting", BpaSeverity.Warning,
                ["Column"], FixExpression: "IsHidden = true"),
            new("NOT_FIXABLE", "Not fixable", "Formatting", BpaSeverity.Warning,
                ["Column"])
        };

        var violations = new List<BpaViolation>
        {
            new("FIXABLE", "Fixable", "Formatting", BpaSeverity.Warning,
                "Column", "'T'[A]", "T/A",
                CanFix: true, ObjectKind: ModelObjectKind.Column),
            new("NOT_FIXABLE", "Not fixable", "Formatting", BpaSeverity.Warning,
                "Column", "'T'[B]", "T/B",
                CanFix: false)
        };

        var result = fixer.ApplyFixes(session, violations, rules);

        Assert.Equal(1, result.FixesApplied);
        Assert.Null(session.LastSetPath?.Contains("T/B") == true ? "T/B" : null);
    }

    [Fact]
    public void ApplyFixes_MultipleFixes()
    {
        var session = new StubMutationSession();
        var fixer = new BpaFixer();

        var rules = new List<BpaRule>
        {
            new("FIX1", "Fix 1", "Formatting", BpaSeverity.Warning,
                ["Column"], FixExpression: "FormatString = \"dd-mm-yyyy\""),
            new("FIX2", "Fix 2", "Formatting", BpaSeverity.Warning,
                ["Column"], FixExpression: "SummarizeBy = AggregateFunction.None")
        };

        var violations = new List<BpaViolation>
        {
            new("FIX1", "Fix 1", "Formatting", BpaSeverity.Warning,
                "Column", "'T'[Date]", "T/Date",
                CanFix: true, ObjectKind: ModelObjectKind.Column),
            new("FIX2", "Fix 2", "Formatting", BpaSeverity.Warning,
                "Column", "'T'[Amount]", "T/Amount",
                CanFix: true, ObjectKind: ModelObjectKind.Column)
        };

        var result = fixer.ApplyFixes(session, violations, rules);

        Assert.Equal(2, result.FixesApplied);
        Assert.Equal(2, session.SetCallCount);
    }

    private sealed class StubMutationSession : IModelMutationSession, IModelSession
    {
        public string SourcePath => "";

        public string? LastSetPath { get; private set; }
        public IReadOnlyList<ModelPropertyAssignment>? LastSetAssignments { get; private set; }
        public string? LastRemovedPath { get; private set; }
        public ModelObjectKind? LastRemovedKind { get; private set; }
        public int SetCallCount { get; private set; }

        public ModelObjectMutationResult SetProperty(ModelObjectSetRequest request)
        {
            LastSetPath = request.Path;
            LastSetAssignments = request.Properties;
            SetCallCount++;
            return new ModelObjectMutationResult(request.Path, true);
        }

        public ModelObjectMutationResult RemoveObject(ModelObjectRemoveRequest request)
        {
            LastRemovedPath = request.Path;
            LastRemovedKind = request.Type;
            return new ModelObjectMutationResult(request.Path, true);
        }

        public ModelObjectMutationResult AddObject(ModelObjectAddRequest request)
            => new(request.Path, false);

        public ModelReplaceResult ReplaceText(ModelReplaceRequest request)
            => new(0, []);

        public Task<ModelExportResult> SaveAsync(string? outputPath, string serialization, bool force, CancellationToken cancellationToken)
            => Task.FromResult(new ModelExportResult(outputPath ?? "saved", serialization));

        public Task<ModelSummary> GetSummaryAsync(CancellationToken _)
            => Task.FromResult(new ModelSummary("stub", 1601, 0, 0, 0, 0, 0));

        public Task<ModelSnapshot> GetSnapshotAsync(CancellationToken _)
            => Task.FromResult(new ModelSnapshot("stub", 1601, []));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
