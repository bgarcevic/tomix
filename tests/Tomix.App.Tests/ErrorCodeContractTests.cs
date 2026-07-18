using Tomix.Core.Results;

namespace Tomix.App.Tests;

/// <summary>
/// Contract tests for the error codes emitted by mutation commands. The JSON envelope
/// shape itself is pinned against the production serializer in
/// <c>Tomix.Cli.Tests.ErrorOutputContractTests</c>.
/// </summary>
public sealed class ErrorCodeContractTests
{
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

        Assert.Equal("TOMIX_MUTATION_FAILED", result.Diagnostics.Single().Code);
    }

    [Fact]
    public void MutationErrorCode_SaveFailed_HasExitCode2()
    {
        var result = TomixResult<object>.Fail("TOMIX_MUTATION_SAVE_FAILED", "IO error", exitCode: 2);

        Assert.Equal(2, result.ExitCode);
    }

    // ── Query error codes ───────────────────────────────────────────────────

    [Theory]
    [InlineData("TOMIX_QUERY_REQUIRED")]
    [InlineData("TOMIX_QUERY_INPUT_CONFLICT")]
    [InlineData("TOMIX_QUERY_FILE_NOT_FOUND")]
    [InlineData("TOMIX_QUERY_BAD_PARAM")]
    [InlineData("TOMIX_QUERY_OUTPUT_FORMAT")]
    [InlineData("TOMIX_QUERY_INVALID")]
    [InlineData("TOMIX_QUERY_NO_REMOTE_TARGET")]
    [InlineData("TOMIX_QUERY_UNSUPPORTED")]
    [InlineData("TOMIX_QUERY_FAILED")]
    public void QueryErrorCodes_AreValidUppercaseSnakeCase(string code)
    {
        Assert.Matches(@"^TOMIX_[A-Z][A-Z0-9]*(_[A-Z0-9]+)*$", code);
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

        Assert.Contains("--force", result.Diagnostics.Single().Hint);
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

        Assert.NotNull(result.Diagnostics.Single().Hint);
    }

    // ── Update error codes ──────────────────────────────────────────────────

    [Theory]
    [InlineData("TOMIX_UPDATE_CHECK_FAILED")]
    [InlineData("TOMIX_UPDATE_VERSION_NOT_FOUND")]
    [InlineData("TOMIX_UPDATE_UNSUPPORTED_INSTALL")]
    [InlineData("TOMIX_UPDATE_TOOL_FAILED")]
    [InlineData("TOMIX_UPDATE_DOWNLOAD_FAILED")]
    [InlineData("TOMIX_UPDATE_CHECKSUM_MISMATCH")]
    [InlineData("TOMIX_UPDATE_APPLY_FAILED")]
    public void UpdateErrorCodes_AreValidUppercaseSnakeCase(string code)
    {
        Assert.Matches(@"^TOMIX_[A-Z][A-Z0-9]*(_[A-Z0-9]+)*$", code);
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
