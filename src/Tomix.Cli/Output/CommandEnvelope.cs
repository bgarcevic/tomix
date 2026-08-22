using Tomix.Core.Diagnostics;

namespace Tomix.Cli.Output;

/// <summary>
/// The stdout JSON contract shared by every command: the command's own payload under
/// <c>data</c>, and any non-fatal diagnostics it produced under <c>diagnostics</c>.
/// </summary>
/// <remarks>
/// <para>
/// This mirrors <see cref="Tomix.Core.Results.TomixResult{T}"/>, which has always carried both
/// halves — the JSON writer simply discarded the diagnostics half and emitted the bare payload,
/// while docs/cli-ux-guidelines.md listed the <c>(data, diagnostics)</c> envelope as API that
/// major versions protect. The code was the side that was wrong.
/// </para>
/// <para>
/// At 0.1.0 <c>diagnostics</c> is always empty: no handler emits a
/// <see cref="DiagnosticSeverity.Warning"/> yet, and a result with no <c>Data</c> writes its
/// error to stderr and nothing at all to stdout. The field is here because adding it after the
/// first tag would be a breaking change to a documented contract, and because a command that
/// succeeds *with* something to say currently has nowhere to say it in JSON mode.
/// </para>
/// <para>
/// Deliberately not enveloped, because they are not command JSON: <c>--output-format csv</c>,
/// <c>get --output-format tmdl|bim|tmsl</c> (model-shaped fragments), <c>deploy --xmla -</c> in
/// <em>text</em> mode (the raw TMSL script), and <c>query --output-file</c> (a data file for
/// jq/pandas, where a wrapper would break every strict reader). Note the qualifier on
/// <c>deploy --xmla</c>: under <c>--output-format json</c> it is an ordinary command result and
/// <em>is</em> enveloped. Every one of these is pinned by a test — see
/// <c>CommandEnvelopeContractTests</c> and <c>QueryOutputFileContractTests</c>.
/// </para>
/// <para>
/// <c>diagnostics</c> also exists on some payloads (<c>bpa run</c>, <c>bpa rules list</c>), where
/// it means something else entirely. Enveloping moved those to <c>.data.diagnostics</c> along with
/// every other payload field; the shared name only makes a script reading <c>.diagnostics</c> get
/// an empty array instead of a missing-key error. docs/cli-ux-guidelines.md spells this out.
/// </para>
/// <para>
/// stderr keeps its own single-error shape (<c>error</c>/<c>code</c>/<c>severity</c>/<c>hint</c>,
/// see <see cref="ErrorOutput"/> and docs/error-codes.md). The two streams answer different
/// questions — "what did the command produce" versus "why did it fail" — and merging them would
/// force every error consumer to walk an array to find the one error it cares about.
/// </para>
/// <para>
/// There is deliberately no <c>Of(data)</c> convenience that defaults the diagnostics to empty:
/// its one caller used it to envelope a payload whose diagnostics were simply out of scope at that
/// seam, which is invisible in the output — the key is still there, still an empty array. Callers
/// pass the diagnostics they hold.
/// </para>
/// </remarks>
/// <param name="Data">The command's payload, in whatever shape that command documents.</param>
/// <param name="Diagnostics">Non-fatal diagnostics; empty when the command had nothing to add.</param>
internal sealed record CommandEnvelope<T>(T Data, IReadOnlyList<TomixDiagnostic> Diagnostics);
