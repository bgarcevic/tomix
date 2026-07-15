using System.Text.Json;
using System.Text.Json.Serialization;
using Tomix.Core.Diagnostics;
using Tomix.Core.Results;

namespace Tomix.App.Tests;

/// <summary>
/// JSON contract tests for the error envelope shape and specific error codes
/// emitted by mutation commands.
/// </summary>
public sealed class ErrorCodeContractTests
{
    private static readonly JsonSerializerOptions ErrorOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static string SerializeError(string code, string message, string? hint = null)
    {
        var errorObj = new Dictionary<string, string?>
        {
            ["error"] = message,
            ["code"] = code,
            ["severity"] = nameof(DiagnosticSeverity.Error),
            ["hint"] = hint
        };
        return JsonSerializer.Serialize(errorObj, ErrorOptions);
    }

    private static string JsonFromResult<T>(TomixResult<T> result)
    {
        var diag = result.Diagnostics.First();
        return SerializeError(diag.Code, diag.Message, diag.Hint);
    }

    // ── Error envelope shape ────────────────────────────────────────────────

    [Fact]
    public void ErrorEnvelope_HasAllFourFields()
    {
        var json = JsonDocument.Parse(SerializeError("TOMIX_TEST", "test message", "test hint"));

        Assert.Equal("test message", json.RootElement.GetProperty("error").GetString());
        Assert.Equal("TOMIX_TEST", json.RootElement.GetProperty("code").GetString());
        Assert.Equal("Error", json.RootElement.GetProperty("severity").GetString());
        Assert.Equal("test hint", json.RootElement.GetProperty("hint").GetString());
    }

    [Fact]
    public void ErrorEnvelope_NullHint_StillIncludesHintAsNull()
    {
        var json = SerializeError("TOMIX_TEST", "test message", hint: null);

        Assert.Contains("\"hint\": null", json);
    }

    // ── Mutation error codes ────────────────────────────────────────────────

    [Theory]
    [InlineData("TOMIX_MUTATION_UNSUPPORTED_PROVIDER")]
    [InlineData("TOMIX_MUTATION_UNSUPPORTED")]
    [InlineData("TOMIX_MUTATION_INVALID_VALUE")]
    [InlineData("TOMIX_MUTATION_FAILED")]
    [InlineData("TOMIX_MUTATION_SAVE_FAILED")]
    public void MutationErrorCodes_AreValidUppercaseSnakeCase(string code)
    {
        Assert.Matches(@"^TOMIX_[A-Z][A-Z0-9]*(_[A-Z0-9]+)*$", code);
    }

    // ── VertiPaq / VPAX error codes ─────────────────────────────────────────

    [Theory]
    [InlineData("TOMIX_VERTIPAQ_UNSUPPORTED_SOURCE")]
    [InlineData("TOMIX_VERTIPAQ_FAILED")]
    [InlineData("TOMIX_VERTIPAQ_TABLE_NOT_FOUND")]
    [InlineData("TOMIX_VERTIPAQ_OPTIONS_CONFLICT")]
    [InlineData("TOMIX_VERTIPAQ_INVALID_FIELDS")]
    [InlineData("TOMIX_VERTIPAQ_INVALID_TOP")]
    [InlineData("TOMIX_VPAX_READ_FAILED")]
    [InlineData("TOMIX_VPAX_WRITE_FAILED")]
    public void VertipaqErrorCodes_AreValidUppercaseSnakeCase(string code)
    {
        Assert.Matches(@"^TOMIX_[A-Z][A-Z0-9]*(_[A-Z0-9]+)*$", code);
    }

    [Fact]
    public void MutationErrorCode_Failed_UsedForInvalidOperationException()
    {
        var result = TomixResult<object>.Fail("TOMIX_MUTATION_FAILED", "Operation failed");
        var json = JsonDocument.Parse(JsonFromResult(result));

        Assert.Equal("TOMIX_MUTATION_FAILED", json.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public void MutationErrorCode_SaveFailed_HasExitCode2()
    {
        var result = TomixResult<object>.Fail("TOMIX_MUTATION_SAVE_FAILED", "IO error", exitCode: 2);

        Assert.Equal(2, result.ExitCode);
    }

    // ── Incremental-refresh error codes ─────────────────────────────────────

    [Theory]
    [InlineData("TOMIX_REFRESH_POLICY_NOT_FOUND")]
    [InlineData("TOMIX_REFRESH_POLICY_INVALID")]
    [InlineData("TOMIX_REFRESH_POLICY_UNSUPPORTED")]
    [InlineData("TOMIX_REFRESH_POLICY_APPLY_FAILED")]
    public void RefreshPolicyCodes_AreValidUppercaseSnakeCase(string code)
    {
        Assert.Matches(@"^TOMIX_[A-Z][A-Z0-9]*(_[A-Z0-9]+)*$", code);
        Assert.StartsWith("TOMIX_REFRESH_POLICY_", code);
    }

    [Fact]
    public void RefreshPolicyInvalid_IncludesForceHint()
    {
        var result = TomixResult<object>.Fail(
            "TOMIX_REFRESH_POLICY_INVALID",
            "Refresh policy for 'Sales' has validation errors: ...",
            hint: "Fix the reported issues or re-run with --force to save anyway.");
        var json = JsonDocument.Parse(JsonFromResult(result));

        Assert.Contains("--force", json.RootElement.GetProperty("hint").GetString());
    }

    // ── Object lookup error codes ───────────────────────────────────────────

    [Theory]
    [InlineData("TOMIX_OBJECT_NOT_FOUND")]
    [InlineData("TOMIX_OBJECT_AMBIGUOUS")]
    public void ObjectLookupCodes_FollowTomixObjectPrefix(string code)
    {
        Assert.StartsWith("TOMIX_OBJECT_", code);
    }

    [Fact]
    public void ObjectNotFound_IncludesHintInErrorJson()
    {
        var result = TomixResult<object>.Fail(
            "TOMIX_OBJECT_NOT_FOUND",
            "Object 'X' not found.",
            hint: "Run 'tx ls' to list available objects.");
        var json = JsonDocument.Parse(JsonFromResult(result));

        Assert.NotNull(json.RootElement.GetProperty("hint").GetString());
    }

    // ── Deprecated/removed error codes (regression guard) ───────────────────

    /// <summary>
    /// These codes were replaced by TOMIX_MUTATION_* codes and must NOT appear in
    /// mutation command output. This test guards against accidental reintroduction.
    /// </summary>
    [Theory]
    [InlineData("TOMIX_REPLACE_UNSUPPORTED")]
    [InlineData("TOMIX_REPLACE_FAILED")]
    [InlineData("TOMIX_REPLACE_SAVE_FAILED")]
    [InlineData("TOMIX_SCRIPT_SAVE_UNSUPPORTED")]
    [InlineData("TOMIX_SCRIPT_SAVE_FAILED")]
    [InlineData("TOMIX_BPA_FIX_UNSUPPORTED")]
    [InlineData("TOMIX_BPA_IGNORE_UNSUPPORTED")]
    public void DeprecatedErrorCodes_AreNotUsedByMutationRunner(string deprecatedCode)
    {
        var activeCodes = new HashSet<string>
        {
            "TOMIX_MUTATION_UNSUPPORTED_PROVIDER",
            "TOMIX_MUTATION_UNSUPPORTED",
            "TOMIX_MUTATION_INVALID_VALUE",
            "TOMIX_MUTATION_FAILED",
            "TOMIX_MUTATION_SAVE_FAILED",
            "TOMIX_OBJECT_NOT_FOUND",
            "TOMIX_OBJECT_AMBIGUOUS"
        };

        Assert.DoesNotContain(deprecatedCode, activeCodes);
    }
}
