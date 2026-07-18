using Tomix.App.Connect;
using Tomix.Core.Models;

namespace Tomix.App.Tests;

public class ConnectPlanHandlerTests
{
    // ------------------------------------------------------------------------------------------
    // Pure helpers (moved from ConnectCommandWorkspaceTests when the logic left the CLI layer).
    // ------------------------------------------------------------------------------------------

    // Local primary (model is not null): bare workspace names expand to a powerbi:// endpoint,
    // mirroring the primary-server normalization. This is the regression that caused a bare
    // `-w` value to be stored verbatim and rendered as a never-reached mirror target.
    [Theory]
    [InlineData("MyWorkspace", "powerbi://api.powerbi.com/v1.0/myorg/MyWorkspace")]
    [InlineData("My Workspace", "powerbi://api.powerbi.com/v1.0/myorg/My Workspace")]
    [InlineData("test", "powerbi://api.powerbi.com/v1.0/myorg/test")]
    public void NormalizeWorkspaceTarget_LocalPrimary_ExpandsBareName(string input, string expected)
    {
        var normalized = ConnectPlanHandler.NormalizeWorkspaceTarget(model: "./local-model", input);

        Assert.Equal(expected, normalized);
        Assert.True(ModelReference.IsRemoteEndpoint(normalized));
    }

    // Local primary: endpoints that already identify as remote or local-instance pass through.
    [Theory]
    [InlineData("powerbi://api.powerbi.com/v1.0/myorg/Workspace")]
    [InlineData("asazure://westeurope.asazure.windows.net/server")]
    [InlineData("localhost:52123")]
    [InlineData("127.0.0.1:52123")]
    public void NormalizeWorkspaceTarget_LocalPrimary_PassesEndpointsThrough(string input)
        => Assert.Equal(input, ConnectPlanHandler.NormalizeWorkspaceTarget(model: "./local-model", input));

    // Percent-escaped workspace names (e.g. pasted from a browser URL) are decoded before being
    // expanded, so the stored mirror matches the real workspace name the XMLA endpoint expects.
    [Theory]
    [InlineData("sandbox%20bkg", "powerbi://api.powerbi.com/v1.0/myorg/sandbox bkg")]
    [InlineData("My%20Workspace", "powerbi://api.powerbi.com/v1.0/myorg/My Workspace")]
    public void NormalizeWorkspaceTarget_LocalPrimary_DecodesPercentEscapes(string input, string expected)
    {
        var normalized = ConnectPlanHandler.NormalizeWorkspaceTarget(model: "./local-model", input);

        Assert.Equal(expected, normalized);
        Assert.True(ModelReference.IsRemoteEndpoint(normalized));
    }

    // An already-formed endpoint is returned verbatim, never decoded. This keeps
    // NormalizeEndpoint idempotent so a percent-escaped workspace name (e.g. one whose
    // real name contains "%20") survives the second normalization pass applied at connect
    // time by TomModelDeployer.ResolveEndpoint instead of being turned into a space.
    [Fact]
    public void NormalizeWorkspaceTarget_LocalPrimary_PassesEndpointWithPercentEscapesThrough()
    {
        var normalized = ConnectPlanHandler.NormalizeWorkspaceTarget(
            model: "./local-model",
            "powerbi://api.powerbi.com/v1.0/myorg/sandbox%20bkg");

        Assert.Equal("powerbi://api.powerbi.com/v1.0/myorg/sandbox%20bkg", normalized);
        Assert.True(ModelReference.IsRemoteEndpoint(normalized));
    }

    // Local primary: a missing workspace stays missing.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NormalizeWorkspaceTarget_LocalPrimary_NullOrWhitespace_ReturnsAsIs(string? input)
        => Assert.Equal(input, ConnectPlanHandler.NormalizeWorkspaceTarget(model: "./local-model", input));

    // Remote primary (model is null): -w is documented as a local folder/.bim target and must
    // NEVER be expanded to a powerbi:// URL. A bare name here is left untouched so the
    // local-folder-init branch can handle it.
    [Theory]
    [InlineData("MyWorkspace")]
    [InlineData("./mirror-folder")]
    [InlineData("C:\\models\\mirror.bim")]
    [InlineData("mirror")]
    public void NormalizeWorkspaceTarget_RemotePrimary_NeverExpands(string input)
    {
        var normalized = ConnectPlanHandler.NormalizeWorkspaceTarget(model: null, input);

        Assert.Equal(input, normalized);
        Assert.False(ModelReference.IsRemoteEndpoint(normalized));
    }

    // Remote primary: an already-remote -w value (unusual but valid) is preserved.
    [Fact]
    public void NormalizeWorkspaceTarget_RemotePrimary_PreservesRemoteEndpoint()
        => Assert.Equal(
            "powerbi://api.powerbi.com/v1.0/myorg/Workspace",
            ConnectPlanHandler.NormalizeWorkspaceTarget(model: null, "powerbi://api.powerbi.com/v1.0/myorg/Workspace"));

    // The remote reports the canonical dataset name; it wins over the user-typed value so the
    // stored mirror target matches exactly (Power BI blocks casing-change renames via XMLA).
    [Theory]
    [InlineData("Contoso_core", "Contoso_Core", "Contoso_Core")]
    [InlineData("Contoso_Core", "Contoso_core", "Contoso_core")]
    [InlineData("MyModel", "MyModel", "MyModel")]
    public void ResolveWorkspaceDatabase_PrefersRemoteName(string requested, string resolved, string expected)
        => Assert.Equal(expected, ConnectPlanHandler.ResolveWorkspaceDatabase(requested, resolved));

    // When the remote didn't report a name (new dataset, or summary lacked one), keep the
    // user-typed value rather than blanking the target.
    [Theory]
    [InlineData("Contoso_core", null)]
    [InlineData("Contoso_core", "")]
    [InlineData("Contoso_core", "   ")]
    public void ResolveWorkspaceDatabase_BlankRemote_FallsBackToRequested(string requested, string? resolved)
        => Assert.Equal(requested, ConnectPlanHandler.ResolveWorkspaceDatabase(requested, resolved));

    // `tx connect -w ./model.bim`: the valueless -w greedily consumed the model path while the
    // server argument stayed empty. Reinterpret it as the primary model with a valueless -w.
    [Theory]
    [InlineData("./model.bim")]
    [InlineData("/abs/path/model")]
    [InlineData("C:\\models\\sales.bim")]
    public void ShouldReinterpretWorkspaceAsModel_SwallowedPath_True(string swallowed)
        => Assert.True(ConnectPlanHandler.ShouldReinterpretWorkspaceAsModel(server: null, swallowed, workspacePresent: true));

    // Not a swallow: server already set, -w absent, blank -w value, or a bare name that is not a path.
    [Fact]
    public void ShouldReinterpretWorkspaceAsModel_ServerSet_False()
        => Assert.False(ConnectPlanHandler.ShouldReinterpretWorkspaceAsModel(server: "MyWorkspace", "./model.bim", workspacePresent: true));

    [Fact]
    public void ShouldReinterpretWorkspaceAsModel_WorkspaceAbsent_False()
        => Assert.False(ConnectPlanHandler.ShouldReinterpretWorkspaceAsModel(server: null, "./model.bim", workspacePresent: false));

    [Fact]
    public void ShouldReinterpretWorkspaceAsModel_BareName_False()
        => Assert.False(ConnectPlanHandler.ShouldReinterpretWorkspaceAsModel(server: null, "SalesWorkspace", workspacePresent: true));

    // ------------------------------------------------------------------------------------------
    // Plan pipeline (previously untested inline orchestration in ConnectCommand.SetAction).
    // ------------------------------------------------------------------------------------------

    private static ConnectPlanRequest Request(
        string? server = null,
        string? database = null,
        string? profile = null,
        string? workspaceValue = null,
        bool workspaceSpecified = false,
        bool local = false,
        bool remote = false,
        string? auth = null,
        string? workspaceFormat = null,
        string? workspaceAuth = null,
        bool canPrompt = false,
        bool databaseResolved = false)
        => new(server, database, profile, workspaceValue, workspaceSpecified, local, remote,
            auth, workspaceFormat, workspaceAuth, canPrompt, databaseResolved);

    [Fact]
    public void Plan_EmptyRequest_ShowsCurrent()
    {
        var plan = ConnectPlanHandler.Plan(Request());

        Assert.True(plan.ShowCurrent);
        Assert.Null(plan.Need);
        Assert.Null(plan.Target);
    }

    // --- --remote -----------------------------------------------------------------------------

    [Fact]
    public void Plan_Remote_CombinedWithServer_IsUsageError()
    {
        var plan = ConnectPlanHandler.Plan(Request(server: "MyWorkspace", remote: true, canPrompt: true));

        Assert.Equal("--remote cannot be combined with a server/database, --local, --profile, or --workspace.", plan.UsageError);
    }

    [Fact]
    public void Plan_Remote_WithoutTty_RequiresInteraction()
    {
        var plan = ConnectPlanHandler.Plan(Request(remote: true, canPrompt: false));

        Assert.NotNull(plan.InteractionRequired);
        Assert.Equal("connect --remote is interactive and needs a TTY.", plan.InteractionRequired!.Message);
    }

    [Fact]
    public void Plan_Remote_OnTty_NeedsRemotePick()
    {
        var plan = ConnectPlanHandler.Plan(Request(remote: true, canPrompt: true));

        Assert.Equal(ConnectNeedKind.RemotePick, plan.Need?.Kind);
    }

    // After the pick is folded back (DatabaseResolved), the filled server/database must not
    // re-trigger the combination guard or any further prompting — including the deliberate
    // "workspace only" choice where database stays null.
    [Theory]
    [InlineData("Picked Model")]
    [InlineData(null)]
    public void Plan_Remote_AfterPick_ProceedsToTarget(string? pickedDatabase)
    {
        var plan = ConnectPlanHandler.Plan(Request(
            server: "powerbi://api.powerbi.com/v1.0/myorg/Picked",
            database: pickedDatabase,
            remote: true, canPrompt: true, databaseResolved: true));

        Assert.Null(plan.Need);
        Assert.Null(plan.UsageError);
        Assert.NotNull(plan.Target);
        Assert.Equal(pickedDatabase, plan.Target!.Validation?.Database);
    }

    // --- -w model.bim reinterpretation ---------------------------------------------------------

    [Fact]
    public void Plan_SwallowedModelPath_ReinterpretsAndReportsValue()
    {
        var plan = ConnectPlanHandler.Plan(Request(
            workspaceValue: "./model.bim", workspaceSpecified: true, canPrompt: false));

        Assert.Equal("./model.bim", plan.ReinterpretedWorkspaceValue);
        // Non-TTY: the now-valueless -w cannot be resolved — interactive required.
        Assert.NotNull(plan.InteractionRequired);
        Assert.Equal("./model.bim", plan.Request.Server);
        Assert.Null(plan.Request.WorkspaceValue);
    }

    // --- --local reshuffle + discovery ---------------------------------------------------------

    [Fact]
    public void Plan_LocalBareDatabase_ReshufflesAndNeedsDiscovery()
    {
        var plan = ConnectPlanHandler.Plan(Request(server: "Sales Model", local: true));

        Assert.Equal(ConnectNeedKind.DesktopDiscovery, plan.Need?.Kind);
        Assert.Equal("Sales Model", plan.Request.Database);
        Assert.Null(plan.Request.Server);
    }

    // Re-planning with the discovered endpoint folded in must converge: the local-instance
    // endpoint suppresses both the reshuffle and a second discovery pass.
    [Fact]
    public void Plan_LocalWithDiscoveredEndpoint_Converges()
    {
        var plan = ConnectPlanHandler.Plan(Request(server: "localhost:51542", database: "Sales Model", local: true));

        Assert.Null(plan.Need);
        Assert.NotNull(plan.Target);
        Assert.Equal("localhost:51542", plan.Target!.RemoteServer);
        Assert.Equal("Sales Model", plan.Target.Database);
        Assert.NotNull(plan.Target.Validation);
    }

    [Fact]
    public void Plan_LocalWithoutEndpoint_NeedsDiscoveryEvenWithoutTty()
    {
        var plan = ConnectPlanHandler.Plan(Request(local: true, canPrompt: false));

        Assert.Equal(ConnectNeedKind.DesktopDiscovery, plan.Need?.Kind);
    }

    // --- interactive fill-in needs -------------------------------------------------------------

    [Fact]
    public void Plan_LocalModelWithValuelessW_NeedsMirrorWorkspace()
    {
        var plan = ConnectPlanHandler.Plan(Request(
            server: "./model.bim", workspaceSpecified: true, canPrompt: true));

        Assert.Equal(ConnectNeedKind.MirrorWorkspace, plan.Need?.Kind);
    }

    [Fact]
    public void Plan_LocalModelWithMirrorWorkspaceButNoDatabase_NeedsMirrorDatabase()
    {
        var plan = ConnectPlanHandler.Plan(Request(
            server: "./model.bim", workspaceValue: "MyWorkspace", workspaceSpecified: true, canPrompt: true));

        Assert.Equal(ConnectNeedKind.MirrorDatabase, plan.Need?.Kind);
        Assert.Equal("powerbi://api.powerbi.com/v1.0/myorg/MyWorkspace", plan.Need!.Endpoint);
        Assert.Equal("model", plan.Need.SuggestionModelName);
    }

    [Fact]
    public void Plan_RemotePrimaryWithoutDatabase_NeedsPrimaryDatabase()
    {
        var plan = ConnectPlanHandler.Plan(Request(server: "MyWorkspace", canPrompt: true));

        Assert.Equal(ConnectNeedKind.PrimaryDatabase, plan.Need?.Kind);
        Assert.Equal("powerbi://api.powerbi.com/v1.0/myorg/MyWorkspace", plan.Need!.Endpoint);
    }

    // The workspace-only pick answers the database question with null; the resolved marker keeps
    // the plan from asking again (the loop would otherwise never converge).
    [Fact]
    public void Plan_RemotePrimary_AfterWorkspaceOnlyPick_DoesNotReprompt()
    {
        var plan = ConnectPlanHandler.Plan(Request(server: "MyWorkspace", canPrompt: true, databaseResolved: true));

        Assert.Null(plan.Need);
        Assert.NotNull(plan.Target);
        Assert.Null(plan.Target!.Validation); // no database — stored without opening
    }

    [Fact]
    public void Plan_RemotePrimaryWithValuelessW_NeedsMirrorFolder_WithSlugDefault()
    {
        var plan = ConnectPlanHandler.Plan(Request(
            server: "MyWorkspace", database: "Sales Model 2026", workspaceSpecified: true, canPrompt: true));

        Assert.Equal(ConnectNeedKind.MirrorFolder, plan.Need?.Kind);
        Assert.Equal("./Sales-Model-2026", plan.Need!.SuggestedFolder);
    }

    [Fact]
    public void Plan_RemotePrimaryWithValuelessW_NoDatabase_SuggestsWorkspaceFolder()
    {
        var plan = ConnectPlanHandler.Plan(Request(
            server: "MyWorkspace", workspaceSpecified: true, canPrompt: true));

        Assert.Equal(ConnectNeedKind.MirrorFolder, plan.Need?.Kind);
        Assert.Equal("./workspace", plan.Need!.SuggestedFolder);
    }

    // Non-TTY: fill-ins are skipped and an unresolved valueless -w surfaces the machine-readable
    // interactive-required diagnostic instead of a prompt.
    [Fact]
    public void Plan_ValuelessW_WithoutTty_RequiresInteraction()
    {
        var plan = ConnectPlanHandler.Plan(Request(
            server: "./model.bim", workspaceSpecified: true, canPrompt: false));

        Assert.NotNull(plan.InteractionRequired);
        Assert.Equal("-w with no value needs an interactive terminal to pick the workspace.", plan.InteractionRequired!.Message);
    }

    // --- workspace usage matrix (message-exact; rendered by the CLI at exit code 1) -------------

    [Fact]
    public void Plan_WorkspaceWithLocal_IsUsageError()
        => Assert.Equal(
            "--workspace is not supported with --local (PBI Desktop).",
            ConnectPlanHandler.Plan(Request(server: "localhost:51542", database: "m", workspaceValue: "./mirror", workspaceSpecified: true, local: true)).UsageError);

    [Fact]
    public void Plan_WorkspaceWithProfile_IsUsageError()
        => Assert.Equal(
            "--workspace cannot be combined with --profile. Activate the profile first, then set up workspace mode separately.",
            ConnectPlanHandler.Plan(Request(server: "MyWorkspace", database: "m", profile: "prod", workspaceValue: "./mirror", workspaceSpecified: true)).UsageError);

    // A path-like -w value with no server would be reinterpreted as the model, so a bare
    // (non-path) mirror name is the input that actually reaches the matrix check.
    [Fact]
    public void Plan_WorkspaceWithoutPrimary_IsUsageError()
        => Assert.Equal(
            "--workspace requires an explicit primary source (server+database or local path).",
            ConnectPlanHandler.Plan(Request(workspaceValue: "mirror", workspaceSpecified: true)).UsageError);

    [Fact]
    public void Plan_WorkspaceWithLocalPathPrimaryMissingDatabase_IsUsageError()
        => Assert.Equal(
            "--workspace requires <server> <database> (two values) when the primary is a local path.",
            ConnectPlanHandler.Plan(Request(server: "./model.bim", workspaceValue: "MyWorkspace", workspaceSpecified: true)).UsageError);

    [Fact]
    public void Plan_WorkspaceWithRemotePrimaryMissingDatabase_IsUsageError()
        => Assert.Equal(
            "--workspace requires both <server> and <database> for the primary connection.",
            ConnectPlanHandler.Plan(Request(server: "MyWorkspace", workspaceValue: "./mirror", workspaceSpecified: true)).UsageError);

    // --- workspace auth/format defaulting -------------------------------------------------------

    [Fact]
    public void Plan_WorkspaceAuth_DefaultsToAuthThenAuto()
    {
        var withAuth = ConnectPlanHandler.Plan(Request(
            server: "./model.bim", database: "m", workspaceValue: "MyWorkspace", workspaceSpecified: true, auth: "spn"));
        Assert.Equal("spn", withAuth.Target!.WorkspaceAuth);

        var withoutAuth = ConnectPlanHandler.Plan(Request(
            server: "./model.bim", database: "m", workspaceValue: "MyWorkspace", workspaceSpecified: true));
        Assert.Equal("auto", withoutAuth.Target!.WorkspaceAuth);

        var explicitAuth = ConnectPlanHandler.Plan(Request(
            server: "./model.bim", database: "m", workspaceValue: "MyWorkspace", workspaceSpecified: true,
            auth: "spn", workspaceAuth: "device"));
        Assert.Equal("device", explicitAuth.Target!.WorkspaceAuth);
    }

    [Fact]
    public void Plan_NoWorkspace_NullsFormatAndAuth()
    {
        var plan = ConnectPlanHandler.Plan(Request(
            server: "MyWorkspace", database: "m", workspaceFormat: "tmdl", workspaceAuth: "device"));

        Assert.Null(plan.Target!.WorkspaceFormat);
        Assert.Null(plan.Target.WorkspaceAuth);
    }

    // --- classification and follow-up flags ------------------------------------------------------

    [Fact]
    public void Plan_BareWorkspaceName_NormalizesEndpointAndValidates()
    {
        var plan = ConnectPlanHandler.Plan(Request(server: "MyWorkspace", database: "Sales"));

        var target = plan.Target!;
        Assert.Equal("powerbi://api.powerbi.com/v1.0/myorg/MyWorkspace", target.RemoteServer);
        Assert.Null(target.Model);
        Assert.NotNull(target.Validation);
        Assert.Equal("Sales", target.Validation!.Database);
        Assert.False(target.ProbeMirror);
        Assert.False(target.InitializeWorkspace);
    }

    [Fact]
    public void Plan_RemoteWithoutDatabase_StoresWithoutValidation()
    {
        var plan = ConnectPlanHandler.Plan(Request(server: "powerbi://api.powerbi.com/v1.0/myorg/WS"));

        Assert.NotNull(plan.Target);
        Assert.Null(plan.Target!.Validation);
    }

    [Fact]
    public void Plan_Profile_SkipsValidation()
    {
        var plan = ConnectPlanHandler.Plan(Request(profile: "prod"));

        Assert.NotNull(plan.Target);
        Assert.Null(plan.Target!.Validation);
        Assert.Equal("prod", plan.Target.Profile);
    }

    [Fact]
    public void Plan_LocalModelWithRemoteMirror_ProbesMirror()
    {
        var plan = ConnectPlanHandler.Plan(Request(
            server: "./model.bim", database: "Sales", workspaceValue: "MyWorkspace", workspaceSpecified: true));

        var target = plan.Target!;
        Assert.NotNull(target.Model);
        Assert.Equal("powerbi://api.powerbi.com/v1.0/myorg/MyWorkspace", target.Workspace);
        Assert.True(target.ProbeMirror);
        Assert.False(target.InitializeWorkspace);
        Assert.NotNull(target.Validation);
        Assert.False(target.Validation!.IsRemote);
    }

    [Fact]
    public void Plan_RemotePrimaryWithLocalMirrorFolder_InitializesWorkspace()
    {
        var plan = ConnectPlanHandler.Plan(Request(
            server: "MyWorkspace", database: "Sales", workspaceValue: "./mirror", workspaceSpecified: true));

        var target = plan.Target!;
        Assert.Null(target.Model);
        Assert.Equal("./mirror", target.Workspace);
        Assert.False(target.ProbeMirror);
        Assert.True(target.InitializeWorkspace);
        Assert.NotNull(target.Validation);
        Assert.True(target.Validation!.IsRemote);
    }

    // A non-XMLA scheme like http:// contains '/', so it classifies as a local model path and
    // fails at validation (no provider) rather than at the defensive invalid-target check —
    // same as the pre-refactor flow.
    [Fact]
    public void Plan_NonXmlaScheme_ClassifiesAsLocalModelPath()
    {
        var plan = ConnectPlanHandler.Plan(Request(server: "http://not-an-xmla-endpoint"));

        Assert.Null(plan.InvalidTarget);
        Assert.NotNull(plan.Target?.Model);
    }

    [Fact]
    public void Plan_LocalModelPath_ResolvesToFullPath()
    {
        var plan = ConnectPlanHandler.Plan(Request(server: "./some/model.bim"));

        Assert.NotNull(plan.Target?.Model);
        Assert.True(Path.IsPathRooted(plan.Target!.Model));
        Assert.Null(plan.Target.RemoteServer);
    }
}
