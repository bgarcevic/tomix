using Tomix.Core.Models;

namespace Tomix.App.Connect;

/// <summary>
/// Pure decision logic for <c>tx connect</c>: classifies the requested target, validates flag
/// combinations, and reports the first missing piece the CLI must resolve interactively.
/// The CLI calls <see cref="Plan"/> in a loop — each returned <see cref="ConnectNeed"/> is
/// resolved by a prompt (or Desktop discovery), folded back into the request, and re-planned —
/// until the plan yields a terminal outcome. Interactive picking itself lives in the CLI, not here.
/// </summary>
public sealed class ConnectPlanHandler
{
    public static ConnectPlanResult Plan(ConnectPlanRequest request)
    {
        var r = request;
        string? reinterpreted = null;

        // --remote: interactive server-only connect. The CLI picks a workspace and model from the
        // tenant, folds them into the request (DatabaseResolved), and the plan falls through into
        // the normal remote pipeline. A null database after the pick means the user deliberately
        // chose "workspace only" and must not be re-prompted.
        if (r.Remote && !r.DatabaseResolved)
        {
            if (!string.IsNullOrWhiteSpace(r.Server) || !string.IsNullOrWhiteSpace(r.Database) ||
                !string.IsNullOrWhiteSpace(r.Profile) || r.WorkspaceSpecified || r.Local)
                return Outcome(r, usageError: "--remote cannot be combined with a server/database, --local, --profile, or --workspace.");

            if (!r.CanPrompt)
                return Outcome(r, interactionRequired: new ConnectInteractionRequired(
                    "connect --remote is interactive and needs a TTY.",
                    "Pass the workspace and model explicitly: tx connect <workspace> <database>"));

            return Outcome(r, need: new ConnectNeed(ConnectNeedKind.RemotePick));
        }

        // Recovery: `tx connect -w model.bim` — the ZeroOrOne option greedily consumed the model
        // path as its value. Reinterpret it as the primary model and treat -w as valueless.
        if (ShouldReinterpretWorkspaceAsModel(r.Server, r.WorkspaceValue, r.WorkspaceSpecified))
        {
            reinterpreted = r.WorkspaceValue;
            r = r with { Server = r.WorkspaceValue, WorkspaceValue = null };
        }

        var workspaceValueless = r.WorkspaceSpecified && string.IsNullOrWhiteSpace(r.WorkspaceValue);

        if (r.Local &&
            string.IsNullOrWhiteSpace(r.Database) &&
            !LooksLikeLocalModelPath(r.Server) &&
            !ModelReference.IsRemoteEndpoint(r.Server))
        {
            r = r with { Database = r.Server, Server = null };
        }

        // Interactive fill-in for missing pieces (TTY only; non-TTY keeps the errors below).
        // Each need fills `workspace` / `database` so the validation and mirror logic below run
        // unchanged. Skipped for --remote, which already resolved both above.
        if (r.CanPrompt && !r.Local && !r.Remote)
        {
            var primaryIsLocalModel = !string.IsNullOrWhiteSpace(r.Server) &&
                                      !ModelReference.IsRemoteEndpoint(r.Server) &&
                                      LooksLikeLocalModelPath(r.Server);

            // Local model + valueless -w: pick the remote mirror workspace.
            if (workspaceValueless && primaryIsLocalModel &&
                string.IsNullOrWhiteSpace(r.Profile))
                return Outcome(r, reinterpreted, need: new ConnectNeed(ConnectNeedKind.MirrorWorkspace));

            // Local model + remote mirror workspace known but no dataset: pick or create one.
            if (primaryIsLocalModel &&
                !string.IsNullOrWhiteSpace(r.WorkspaceValue) &&
                string.IsNullOrWhiteSpace(r.Database) &&
                ModelReference.IsRemoteEndpoint(ModelReference.NormalizeEndpoint(r.WorkspaceValue)))
                return Outcome(r, reinterpreted, need: new ConnectNeed(
                    ConnectNeedKind.MirrorDatabase,
                    Endpoint: ModelReference.NormalizeEndpoint(r.WorkspaceValue),
                    SuggestionModelName: ModelNameFromPath(r.Server!)));

            // Remote primary without a dataset (no mirror): pick a model, or connect workspace-only.
            if (!primaryIsLocalModel &&
                !r.WorkspaceSpecified &&
                !string.IsNullOrWhiteSpace(r.Server) &&
                ModelReference.IsRemoteEndpoint(ModelReference.NormalizeEndpoint(r.Server)) &&
                !ModelReference.IsLocalInstanceEndpoint(ModelReference.NormalizeEndpoint(r.Server)) &&
                string.IsNullOrWhiteSpace(r.Database) &&
                !r.DatabaseResolved)
                return Outcome(r, reinterpreted, need: new ConnectNeed(
                    ConnectNeedKind.PrimaryDatabase,
                    Endpoint: ModelReference.NormalizeEndpoint(r.Server)));

            // Remote primary + valueless -w: -w is a local mirror folder — prompt for the path.
            if (workspaceValueless && !primaryIsLocalModel &&
                !string.IsNullOrWhiteSpace(r.Server) &&
                ModelReference.IsRemoteEndpoint(ModelReference.NormalizeEndpoint(r.Server)))
                return Outcome(r, reinterpreted, need: new ConnectNeed(
                    ConnectNeedKind.MirrorFolder,
                    SuggestedFolder: "./" + (string.IsNullOrWhiteSpace(r.Database) ? "workspace" : SlugPath(r.Database))));
        }

        // A valueless -w that could not be resolved interactively (non-TTY, --non-interactive,
        // --quiet, or json/csv output) has no target — surface the same machine-readable
        // TOMIX_INTERACTIVE_REQUIRED diagnostic as --remote, with the flag-equivalent hint.
        if (workspaceValueless)
            return Outcome(r, reinterpreted, interactionRequired: new ConnectInteractionRequired(
                "-w with no value needs an interactive terminal to pick the workspace.",
                "Pass the target explicitly: tx connect <model> <database> -w <workspace>."));

        var workspaceFormat = r.WorkspaceFormat;
        var workspaceAuth = r.WorkspaceAuth;
        if (!string.IsNullOrWhiteSpace(r.WorkspaceValue))
        {
            if (r.Local)
                return Outcome(r, reinterpreted, usageError: "--workspace is not supported with --local (PBI Desktop).");

            if (!string.IsNullOrWhiteSpace(r.Profile))
                return Outcome(r, reinterpreted, usageError: "--workspace cannot be combined with --profile. Activate the profile first, then set up workspace mode separately.");

            if (string.IsNullOrWhiteSpace(r.Server) && string.IsNullOrWhiteSpace(r.Database))
                return Outcome(r, reinterpreted, usageError: "--workspace requires an explicit primary source (server+database or local path).");

            if (!string.IsNullOrWhiteSpace(r.Server) && string.IsNullOrWhiteSpace(r.Database))
            {
                var message = LooksLikeLocalModelPath(r.Server)
                    ? "--workspace requires <server> <database> (two values) when the primary is a local path."
                    : "--workspace requires both <server> and <database> for the primary connection.";
                return Outcome(r, reinterpreted, usageError: message);
            }

            if (string.IsNullOrWhiteSpace(workspaceAuth))
                workspaceAuth = string.IsNullOrWhiteSpace(r.Auth) ? "auto" : r.Auth;
        }
        else
        {
            workspaceFormat = null;
            workspaceAuth = null;
        }

        if (string.IsNullOrWhiteSpace(r.Server) &&
            string.IsNullOrWhiteSpace(r.Database) &&
            string.IsNullOrWhiteSpace(r.Profile) &&
            string.IsNullOrWhiteSpace(r.WorkspaceValue) &&
            !r.Local)
        {
            return Outcome(r, reinterpreted, showCurrent: true);
        }

        var isRemoteEndpoint = ModelReference.IsRemoteEndpoint(r.Server);
        var model = (!isRemoteEndpoint && LooksLikeLocalModelPath(r.Server)) ? Path.GetFullPath(r.Server!) : null;
        var remoteServer = model is null ? r.Server : null;

        if (r.Local && model is null && !ModelReference.IsLocalInstanceEndpoint(remoteServer))
            return Outcome(r, reinterpreted, need: new ConnectNeed(ConnectNeedKind.DesktopDiscovery));

        // A bare workspace name (e.g. "MyWorkspace") is shorthand for the workspace's XMLA
        // endpoint. Expand it to a fully-qualified endpoint so the stored connection can be
        // opened by every remote provider (CanOpen checks IsRemote) and validated by the CLI
        // when a database is supplied. The TOM provider applies the same normalization at
        // connect time, but expanding here keeps the active connection usable by every later command.
        if (!string.IsNullOrWhiteSpace(remoteServer) && !ModelReference.IsRemoteEndpoint(remoteServer))
        {
            remoteServer = ModelReference.NormalizeEndpoint(remoteServer);
            isRemoteEndpoint = true;
        }

        // Mirror target normalization: a bare workspace name as a mirror target (local primary)
        // is shorthand for the workspace's XMLA endpoint, mirroring the primary-server expansion
        // above. Without this, a bare -w value is stored verbatim, IsRemoteEndpoint returns
        // false, the mirror probe is skipped, and ResolveSyncTarget later returns null — so the
        // mirror is rendered but never actually reachable. Remote-primary -w values (local
        // folder/.bim paths) are returned unchanged.
        var workspace = NormalizeWorkspaceTarget(model, r.WorkspaceValue);

        // Note: every value classified as a server here is openable — anything containing a
        // path separator classifies as a local model path above, and NormalizeEndpoint turns
        // the remaining bare names into powerbi:// (or localhost) endpoints — so there is no
        // dead-end "neither endpoint nor path" case to reject.

        // Validate by opening before storing: local model paths always, and remote XMLA
        // endpoints when a dataset is given (so the CLI opens that specific catalog, not the
        // whole workspace). A remote endpoint without a dataset is stored as-is without opening.
        ModelReference? validation = null;
        if (string.IsNullOrWhiteSpace(r.Profile))
        {
            if (!string.IsNullOrWhiteSpace(model))
                validation = new ModelReference(model);
            else if (isRemoteEndpoint && !string.IsNullOrWhiteSpace(r.Database))
                validation = ModelReference.Remote(remoteServer!, r.Database);
        }

        return Outcome(r, reinterpreted, target: new ConnectTarget(
            Model: model,
            RemoteServer: remoteServer,
            Database: r.Database,
            Workspace: workspace,
            WorkspaceFormat: workspaceFormat,
            WorkspaceAuth: workspaceAuth,
            Profile: r.Profile,
            Auth: r.Auth,
            Local: r.Local,
            Validation: validation,
            ProbeMirror: model is not null &&
                         !string.IsNullOrWhiteSpace(workspace) &&
                         !string.IsNullOrWhiteSpace(r.Database) &&
                         ModelReference.IsRemoteEndpoint(workspace),
            InitializeWorkspace: model is null &&
                                 !string.IsNullOrWhiteSpace(workspace) &&
                                 !ModelReference.IsRemoteEndpoint(workspace)));
    }

    private static ConnectPlanResult Outcome(
        ConnectPlanRequest request,
        string? reinterpreted = null,
        ConnectNeed? need = null,
        bool showCurrent = false,
        string? usageError = null,
        ConnectInteractionRequired? interactionRequired = null,
        ConnectTarget? target = null)
        => new(request, need, reinterpreted, showCurrent, usageError, interactionRequired, target);

    // Detects `tx connect -w model.bim`, where the ZeroOrOne -w option greedily consumed the model
    // path as its value while the server argument stayed empty. Reinterpreted by Plan as a local
    // primary with a valueless -w.
    internal static bool ShouldReinterpretWorkspaceAsModel(string? server, string? workspaceValue, bool workspacePresent)
        => workspacePresent
           && !string.IsNullOrWhiteSpace(workspaceValue)
           && string.IsNullOrWhiteSpace(server)
           && LooksLikeLocalModelPath(workspaceValue);

    // Model name for the autogenerated mirror-dataset suggestion: a .bim/.tmsl file's name without
    // extension, else the containing folder's name (TMDL/TE folder).
    internal static string ModelNameFromPath(string path)
    {
        var trimmed = path.TrimEnd('/', '\\');
        var name = Directory.Exists(trimmed)
            ? Path.GetFileName(trimmed)
            : Path.GetFileNameWithoutExtension(trimmed);
        return string.IsNullOrWhiteSpace(name) ? "model" : name;
    }

    internal static string SlugPath(string value)
    {
        var slug = new string(value.Trim().Select(ch => char.IsAsciiLetterOrDigit(ch) ? ch : '-').ToArray());
        return slug.Trim('-') is { Length: > 0 } s ? s : "workspace";
    }

    internal static bool LooksLikeLocalModelPath(string? value)
        => !string.IsNullOrWhiteSpace(value) &&
           (Directory.Exists(value) || File.Exists(value) || value.Contains('\\') || value.Contains('/'));

    // A bare workspace name as a mirror target (local primary) is shorthand for the workspace's
    // XMLA endpoint, mirroring the primary-server normalization. NormalizeEndpoint expands bare
    // names to powerbi:// and decodes percent escapes (e.g. "sandbox%20bkg"), so both a typed
    // name and a pasted URL resolve to the real workspace. Only applies when the primary is
    // local; a remote primary expects -w to be a local folder/.bim path and is left untouched.
    internal static string? NormalizeWorkspaceTarget(string? model, string? workspace)
    {
        if (model is not null && !string.IsNullOrWhiteSpace(workspace))
        {
            return ModelReference.NormalizeEndpoint(workspace);
        }
        return workspace;
    }

    // Returns the canonical dataset name reported by the remote workspace when available, so the
    // stored mirror target matches exactly. Power BI rejects XMLA deploys that change a dataset's
    // name (even a casing difference), so preferring the resolved name over the user-typed value
    // keeps later syncs/deploys from being treated as a rename. Falls back to the requested name
    // when the remote didn't report one.
    internal static string ResolveWorkspaceDatabase(string? requested, string? resolvedFromRemote)
        => string.IsNullOrWhiteSpace(resolvedFromRemote) ? (requested ?? "") : resolvedFromRemote;
}

/// <summary>
/// The CLI-resolved connect input. <see cref="WorkspaceSpecified"/> carries the -w tri-state
/// (present with/without a value vs. absent) so the plan never touches System.CommandLine.
/// <see cref="DatabaseResolved"/> marks that an interactive pick already answered the database
/// question (possibly with "workspace only"), so the plan must not ask again.
/// </summary>
public sealed record ConnectPlanRequest(
    string? Server,
    string? Database,
    string? Profile,
    string? WorkspaceValue,
    bool WorkspaceSpecified,
    bool Local,
    bool Remote,
    string? Auth,
    string? WorkspaceFormat,
    string? WorkspaceAuth,
    bool CanPrompt,
    bool DatabaseResolved = false);

/// <summary>
/// One planning pass. <see cref="Request"/> is the input with any internal rewrites applied
/// (reinterpreted -w, --local reshuffle) — the CLI folds prompt answers into it and re-plans.
/// Exactly one of <see cref="Need"/>, <see cref="ShowCurrent"/>, <see cref="UsageError"/>,
/// <see cref="InteractionRequired"/>, or <see cref="Target"/> is set.
/// <see cref="ReinterpretedWorkspaceValue"/> may accompany any outcome on the pass that rewrote it.
/// </summary>
public sealed record ConnectPlanResult(
    ConnectPlanRequest Request,
    ConnectNeed? Need,
    string? ReinterpretedWorkspaceValue,
    bool ShowCurrent,
    string? UsageError,
    ConnectInteractionRequired? InteractionRequired,
    ConnectTarget? Target);

public enum ConnectNeedKind
{
    /// <summary>--remote: pick a workspace and model from the tenant.</summary>
    RemotePick,

    /// <summary>Local primary + valueless -w: pick the remote mirror workspace.</summary>
    MirrorWorkspace,

    /// <summary>Local primary + remote mirror workspace, no dataset: pick or create one.</summary>
    MirrorDatabase,

    /// <summary>Remote primary without a dataset: pick a model or connect workspace-only.</summary>
    PrimaryDatabase,

    /// <summary>Remote primary + valueless -w: prompt for the local mirror folder path.</summary>
    MirrorFolder,

    /// <summary>--local without an endpoint: discover running Power BI Desktop instances.</summary>
    DesktopDiscovery
}

public sealed record ConnectNeed(
    ConnectNeedKind Kind,
    string? Endpoint = null,
    string? SuggestionModelName = null,
    string? SuggestedFolder = null);

public sealed record ConnectInteractionRequired(string Message, string Hint);

/// <summary>
/// The fully classified connect target: primary source (local model path or normalized remote
/// endpoint), normalized mirror workspace, and the follow-up work the CLI must run —
/// <see cref="Validation"/> (open before storing), <see cref="ProbeMirror"/> (check the remote
/// mirror target), and <see cref="InitializeWorkspace"/> (scaffold a local mirror folder).
/// </summary>
public sealed record ConnectTarget(
    string? Model,
    string? RemoteServer,
    string? Database,
    string? Workspace,
    string? WorkspaceFormat,
    string? WorkspaceAuth,
    string? Profile,
    string? Auth,
    bool Local,
    ModelReference? Validation,
    bool ProbeMirror,
    bool InitializeWorkspace);
