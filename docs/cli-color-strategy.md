# CLI Color Strategy

Reference for all ANSI color usage in `tx`. Read this before adding or changing colored output.

## Palette

All colors are chosen for readability on both dark and light terminal backgrounds.

| Role    | Name    | Hex       | Example                    | Use                          |
|---------|---------|-----------|----------------------------|------------------------------|
| Title   | Sage    | `#3E9287` | `MyCli`                    | App names, section headers   |
| Command | Default | —         | `mycli build`              | Commands (bold, no color)    |
| Option  | Lav     | `#8E7BB8` | `--project`                | Flags and options            |
| Value   | Sand    | `#B08440` | `api-service`              | IDs, names, literals         |
| Path    | Harbor  | `#4E8AB5` | `./src/api-service`        | Files and folders            |
| Success | Moss    | `#5C9D52` | `OK Project initialized`   | Completed actions            |
| Warning | Amber   | `#B5832F` | `WARN Config not found`    | Recoverable issues           |
| Error   | Rose    | `#C25E5E` | `ERROR Build failed`       | Failures                     |
| Muted   | Slate   | `#768089` | `(2.3s elapsed)`           | Hints, timings, secondary    |

## Palette Implementation

Defined in `src/Tomix.Cli/Output/Styling.cs`:

```csharp
using Spectre.Console;

namespace Tomix.Cli.Output;

internal static class Palette
{
    public static readonly Color Sage   = new(0x3E, 0x92, 0x87);
    public static readonly Color Lav    = new(0x8E, 0x7B, 0xB8);
    public static readonly Color Sand   = new(0xB0, 0x84, 0x40);
    public static readonly Color Harbor = new(0x4E, 0x8A, 0xB5);
    public static readonly Color Moss   = new(0x5C, 0x9D, 0x52);
    public static readonly Color Amber  = new(0xB5, 0x83, 0x2F);
    public static readonly Color Rose   = new(0xC2, 0x5E, 0x5E);
    public static readonly Color Slate  = new(0x76, 0x80, 0x89);
}
```

Use `Palette.Sage` for Spectre widget styling (table borders, panel borders). Use the markup helpers below for inline text.

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
| `Styling.Value(text)`                   | Sand                                     |
| `Styling.KeyValue(label, value)`        | Bold label + plain value                 |
| `Styling.Guidance(text)`                | Slate                                    |
| `Styling.MarkupEscape(text)`            | Escapes `[` and `]` for Spectre markup  |
| `Styling.SeverityMarkup(severity)`      | Colored severity label (Error/Warning/Info) |
| `Styling.NewTable(params columns)`      | Rounded-border table with Slate border   |

## What NOT to Color

- **JSON output** (`--format json`) — raw JSON, no markup.
- **CSV output** (`--format csv`) — raw CSV, no markup.
- **TMDL/BIM raw output** (`--format tmdl`, `--format bim`) — raw syntax.
- **CI annotations** (`::error::`, `##vso[task.logissue...]`) — plain-text CI protocols.
- **Completion scripts** (`completion bash/zsh/fish`) — shell script output.

For the migration plan and per-command progress, see `/docs/spectre-migration.md`.
