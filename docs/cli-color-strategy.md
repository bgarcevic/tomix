# CLI Color Strategy

Reference for all ANSI color usage in `tx`. Read this before adding or changing colored output.

## Palette

Every role keeps at least ~3.3:1 contrast on both dark (`#1E1E1E`) and light (`#FFFFFF`) terminal backgrounds — the practical ceiling for a single palette, since 4.5:1 (WCAG normal text) on both is mathematically impossible: it would require every color's luminance to sit in a near-zero-width band. Roles that appear in the same view are separated by lightness as well as hue, so the distinction survives for red-green color-blind readers (see [Palette construction](#palette-construction)). The guarantees are enforced by `PaletteTests`.

| Role    | Name    | Hex       | Example                    | Use                          |
|---------|---------|-----------|----------------------------|------------------------------|
| Title   | Sage    | `#34897E` | `MyCli`                    | App names, section headers   |
| Command | Default | —         | `mycli build`              | Commands (bold, no color)    |
| Option  | Lav     | `#8572AF` | `--project`                | Flags and options            |
| Value   | Terra   | `#966442` | `api-service`              | IDs, names, literals         |
| Path    | Harbor  | `#4582AC` | `./src/api-service`        | Files and folders            |
| Success | Moss    | `#408139` | `OK Project initialized`   | Completed actions            |
| Warning | Amber   | `#B07E2A` | `WARN Config not found`    | Recoverable issues           |
| Error   | Rose    | `#CC6766` | `ERROR Build failed`       | Failures                     |
| Measures| Orchid  | `#CF67AC` | `[Profit]` in DAX          | Measure references in DAX    |
| Muted   | Slate   | `#757F88` | `(2.3s elapsed)`           | Hints, timings, secondary    |

## Palette Implementation

Defined in `src/Tomix.Cli/Output/Styling.cs`:

```csharp
using Spectre.Console;

namespace Tomix.Cli.Output;

internal static class Palette
{
    public static readonly Color Sage   = new(0x34, 0x89, 0x7E);
    public static readonly Color Lav    = new(0x85, 0x72, 0xAF);
    public static readonly Color Terra  = new(0x96, 0x64, 0x42);
    public static readonly Color Harbor = new(0x45, 0x82, 0xAC);
    public static readonly Color Moss   = new(0x40, 0x81, 0x39);
    public static readonly Color Amber  = new(0xB0, 0x7E, 0x2A);
    public static readonly Color Rose   = new(0xCC, 0x67, 0x66);
    public static readonly Color Orchid = new(0xCF, 0x67, 0xAC);
    public static readonly Color Slate  = new(0x75, 0x7F, 0x88);
}
```

Use `Palette.Sage` for Spectre widget styling (table borders, panel borders). Use the markup helpers below for inline text.

## Palette Construction

The palette is derived, not hand-picked: hues and chroma come from the original design, and each color's CIELAB lightness is set deliberately.

- **Contrast first.** Lightness targets pull every role toward the luminance that maximizes its worst-case contrast against dark and light backgrounds, so no role drops below ~3.3:1 on either.
- **Lightness as a second channel.** Roles that appear side by side get a deliberate lightness gap in addition to their hue difference (columns darker than measures, variables darker than literals). Under red-green color vision deficiency the hue difference vanishes and the lightness gap carries the distinction: the columns/measures pair improves from ΔE 13 to 29 under deuteranopia simulation, variables/literals from 13 to 16.
- **Enforced by tests.** `PaletteTests` computes WCAG contrast ratios and CIELAB ΔE76 for every role and fails when a color falls below 3.2:1 on either background or a same-view pair drifts under ΔE 25.

When changing a palette color, keep this contract: adjust lightness before hue, and let `PaletteTests` arbitrate.

## Message Categories

| Category           | Style                                         | Example                                          |
|--------------------|-----------------------------------------------|--------------------------------------------------|
| Banner             | `[bold]` on title                             | `[bold]tx doctor[/]`                            |
| Section header     | `[bold]` label                                | `[bold]Tables[/] (4)`                            |
| Status progress    | Sage                                          | `Validating...` in Sage                          |
| Success            | Moss                                          | `Saved: model.tmdl` in Moss                      |
| Warning            | Amber                                         | `Changes not saved.` in Amber                    |
| Error              | Rose + bold                                   | `Build failed` in Rose bold                      |
| Key-value label    | `[bold]` label, plain value                   | `[bold]Version:[/] 1.0.0`                        |
| Guidance hint      | Slate                                         | `Run 'tx stage commit' to promote.` in Slate    |
| Diff added         | Moss prefix `+`                               | `+ table Sales`                                  |
| Diff removed       | Rose prefix `-`                               | `- table Sales`                                  |
| Diff modified      | Amber prefix `~`                              | `~ table Sales`                                  |
| Table              | Spectre `Table().RoundedBorder().BorderColor(Palette.Slate)` | Already established in `LsRenderer` |
| Table row de-emphasis | Whole row in Slate (`Styling.Muted` per cell) | Hidden-object rows in `ls` are muted end to end |
| Connection banner  | Slate on stderr                               | `Connected to: C:\models\Sales` before the model opens |
| DAX highlighting   | Role-mapped palette on text output only       | `get` properties, `ls` expression cells, `validate` offending lines, `set` DAX before/after previews, and `format` inline/`--path` output: keywords Lav, functions Harbor, tables Sage, columns Moss, measures Orchid, variables Terra, literals Amber, comments Slate; M expressions and JSON/CSV/TMDL/BIM stay markup-free; `script` and `bpa run --fix` stay plain because neither carries DAX today |
| CI annotations     | Plain text, no markup                         | `::error::...` / `##vso[task.logissue...]`       |

## NO_COLOR Compliance

`tx` follows the [NO_COLOR](https://no-color.org/) convention:

1. **Environment variable.** When `NO_COLOR` is set (any non-empty value), `tx` suppresses all ANSI color codes.
2. **Config override.** `noColor: true` in `~/.tomix/config.json` also disables color.
3. **Implementation.** Both mechanisms set `AnsiConsole.Profile.Capabilities.ColorSystem = ColorSystem.NoColors` at startup (see `Program.cs`). Spectre.Console automatically strips all markup and color from output when this is set.
4. **Piped output.** Spectre.Console also detects `Console.IsOutputRedirected` and degrades gracefully.

Do not bypass this by writing raw ANSI escape codes. Always use Spectre.Console APIs or the `Styling` helpers.

## Styling Helpers

All output helpers live in `src/Tomix.Cli/Output/Styling.cs`. Use these instead of raw markup strings.

| Helper                                  | Output                                   |
|-----------------------------------------|------------------------------------------|
| `Styling.Bold(text)`                    | Bold text                                |
| `Styling.Title(text)`                   | Sage bold                                |
| `Styling.Success(text)`                 | Moss                                     |
| `Styling.Warning(text)`                 | Amber                                    |
| `Styling.Error(text)`                   | Rose bold                                |
| `Styling.Muted(text)`                   | Slate                                    |
| `Styling.Path(text)`                    | Harbor                                   |
| `Styling.Value(text)`                   | Terra                                    |
| `Styling.Option(text)`                  | Lav                                      |
| `Styling.KeyValue(label, value)`        | Bold label + plain value                 |
| `Styling.Guidance(text)`                | Slate                                    |
| `Styling.MarkupEscape(text)`            | Escapes `[` and `]` for Spectre markup  |
| `Styling.DaxMarkup(expression)`         | Syntax-highlighted DAX as escaped markup (see Message Categories) |
| `Styling.ExpressionMarkup(isDax, text)` | The shared entry point for expression text: highlighted when `isDax` (from `DaxExpressions.IsDaxValue`/`IsDaxExpression`), escaped plain otherwise; optional `measureNames` resolves measure references to their own color, optional `suffix` (e.g. `... (+2 lines)`) stays plain |
| `Styling.SeverityMarkup(severity)`      | Colored severity label (Error/Warning/Info) |
| `Styling.NewTable(params columns)`      | Rounded-border table with Slate border   |

## What NOT to Color

- **JSON output** (`--format json`) — raw JSON, no markup.
- **CSV output** (`--format csv`) — raw CSV, no markup.
- **TMDL/BIM raw output** (`--format tmdl`, `--format bim`) — raw syntax.
- **CI annotations** (`::error::`, `##vso[task.logissue...]`) — plain-text CI protocols.
- **Completion scripts** (`completion bash/zsh/fish`) — shell script output.
