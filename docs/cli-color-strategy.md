# CLI Color Strategy

Reference for all ANSI color usage in `tx`. Read this before adding or changing colored output.

## Principles

- **The terminal's colors, not ours.** `tx` uses ANSI-16 palette indexes, never RGB. Each user's theme (Campbell, Dark+, Solarized, a high-contrast or color-blind-friendly theme) decides the actual shade, so output looks native everywhere and contrast is the theme's job.
- **Color means something, or it isn't used.** Red, yellow, and green carry status; DAX and M highlighting uses a few roles. Everything else is bold (headings, commands to copy), dim (secondary text, borders), or plain (names, paths, values, options).
- **Color is never the only signal.** Every status also has a glyph or a word (`✓ ! ✗`, `+ - ~`, `ERROR`, `FAIL`), so the output reads the same for color-blind users and with `NO_COLOR`.

## Palette

| Role      | ANSI index      | Use                                                        |
|-----------|-----------------|------------------------------------------------------------|
| Error     | 1, red          | Failures, removed lines in diffs (bold for headings)       |
| Success   | 2, green        | Completed actions, added lines in diffs                    |
| Warning   | 3, yellow       | Recoverable issues, modified lines in diffs                |
| Info      | 6, cyan         | Spinners, progress, `Info` severity, VertiPaq bars         |
| Keyword   | 5, magenta      | DAX and M keywords                                         |
| Function  | 12, bright blue | DAX and M functions                                        |
| Reference | 6, cyan         | DAX measure references                                     |
| Literal   | 2, green        | Strings and numbers in DAX and M                           |
| Muted     | dim             | Hints, timings, table borders, rules, hidden rows, comments |

Only these indexes are allowed: normal blue (4) is unreadable on the Windows Terminal default (Campbell), and the bright codes other than blue (9 to 15) turn grey in Solarized. Bright blue is the one exception because normal blue fails where most Windows users look. Yellow is the weakest color on some light themes (VS Code Light+), so keep it to short labels and prefixes, never long text.

## Palette Implementation

Defined in `src/Tomix.Cli/Output/Styling.cs`:

```csharp
internal static class Palette
{
    public static readonly Color Error = Color.Maroon;
    public static readonly Color Success = Color.Green;
    public static readonly Color Warning = Color.Olive;
    public static readonly Color Info = Color.Teal;
    public static readonly Color Keyword = Color.Purple;
    public static readonly Color Function = Color.Blue;
    public static readonly Color Reference = Color.Teal;
    public static readonly Color Literal = Color.Green;

    public static readonly Style Muted = new(decoration: Decoration.Dim);
}
```

Spectre names the ANSI colors after the HTML ones: `Maroon` is red (1), `Olive` is yellow (3), `Purple` is magenta (5), `Teal` is cyan (6), and `Blue` is bright blue (12). Spectre writes them as 256-color indexes (`ESC[38;5;1m`), and indexes 0 to 15 are the theme's colors in every mainstream terminal. Never construct a `Color` from RGB.

Use `Palette.Muted` for Spectre widget styling (table borders, rules). Use the markup helpers below for inline text.

`PaletteTests` pins the contract: every role is one of the allowed indexes, it renders as a palette index even on a true-color terminal, and the four DAX roles never share a color.

## Color Blindness

The most common forms of color blindness (deuteranopia and protanopia) merge red and green, so the status colors only reinforce what the glyph or word already says. In highlighted expressions the roles also differ by shape: functions are uppercase and followed by `(`, measure references sit in `[...]`, strings in quotes, comments start with `//`. Magenta keywords and bright-blue functions stay apart by lightness in most themes. Users who need more pick a theme tuned for their vision, and `tx` follows it.

## Hierarchy

Every line sits on one of four levels, loudest first. Pick the level by what the reader came for, not by what the code knows.

1. **Status**: color (with its glyph or word). The outcome of the command: `Authenticated`, `✗ 3 errors`, `Not logged in`.
2. **Structure**: bold. Titles, section headings, table column headers, and commands to copy.
3. **Content**: plain. Names, paths, IDs, values — the data itself.
4. **Commentary**: dim. Progress notes (`Authenticating...`), where something was saved, hints, timings, help placeholders, borders.

A label never outranks its value: key-value labels stay plain and the values line up in one column, so alignment, not color, shows the structure. Keep dim for commentary; a block where every label is grey reads as all grey. Commentary goes to stderr, so a redirected stdout holds only content.

## Message Categories

| Category           | Style                                         | Example                                          |
|--------------------|-----------------------------------------------|--------------------------------------------------|
| Banner             | `[bold]` on title                             | `[bold]tx doctor[/]`                            |
| Section header     | `[bold]` label                                | `[bold]Tables[/] (4)`                            |
| Help               | `[bold]` headings, plain command and option names, dim placeholders | `[bold]Discover:[/]` then `summary  ...`, `--model [dim]<path>[/]` |
| Status progress    | Info                                          | spinner frame in cyan, label plain; without an animating terminal (stdin piped) the label prints once, dim, on stderr |
| Success            | Success                                       | `Saved: model.tmdl` in green                     |
| Warning            | Warning                                       | `Changes not saved.` in yellow                   |
| Error              | Error + bold                                  | `Build failed` in bold red                       |
| Key-value block    | Plain label and value, values aligned (`Styling.WriteKeyValues`) | `Tenant:   contoso.onmicrosoft.com`   |
| Guidance hint      | Dim                                           | `Run 'tx stage commit' to promote.` dimmed       |
| Diff added         | Success, prefix `+`                           | `+ table Sales`                                  |
| Diff removed       | Error, prefix `-`                             | `- table Sales`                                  |
| Diff modified      | Warning, prefix `~`                           | `~ table Sales`                                  |
| Table              | `Styling.NewTable()`: rounded border, dim     | Already established in `LsRenderer`              |
| Table row de-emphasis | Whole row dim (`Styling.Muted` per cell)   | Hidden-object rows in `ls` are muted end to end  |
| Connection banner  | Dim on stderr                                 | `Connected to: C:\models\Sales` before the model opens |
| DAX highlighting   | Role-mapped palette on text output only       | `get` properties, `ls` expression cells, `validate` offending lines, `set` DAX before/after previews, and `format` inline/`--path` output: keywords magenta, functions bright blue, measure references cyan, literals green, comments dim, definition names bold; tables, columns, and variables plain. JSON/CSV/TMDL/BIM stay markup-free; `bpa run --fix` stays plain because it carries no DAX today |
| M highlighting     | Same roles, text output only                  | `get` properties, `ls` expression cells (partitions, shared expressions), and `format --lang m` output: keywords and type names magenta, library functions and `#table`-style constructors bright blue, literals green, comments dim; step and field definitions and field access (`[Amount]`) plain. A lexical pass (`MLanguage.Classify`), not the parser, so it costs nothing per command |
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
| `Styling.Title(text)`                   | Bold                                     |
| `Styling.Success(text)`                 | Green                                    |
| `Styling.Warning(text)`                 | Yellow                                   |
| `Styling.Error(text)`                   | Bold red                                 |
| `Styling.Muted(text)`                   | Dim                                      |
| `Styling.Path(text)`                    | Plain (escaped)                          |
| `Styling.Value(text)`                   | Plain (escaped)                          |
| `Styling.Option(text)`                  | Bold                                     |
| `Styling.KeyValue(label, value)`        | Plain label + plain value (one row)      |
| `Styling.KeyValueLines(rows, indent)` / `WriteKeyValues` | Plain rows, values aligned two spaces past the longest label |
| `Styling.Placeholder(text)`             | Dim (`<path>`, `[options]` in help)      |
| `Styling.Guidance(text)`                | Dim                                      |
| `Styling.MarkupEscape(text)`            | Escapes `[` and `]` for Spectre markup  |
| `Styling.DaxMarkup(expression)`         | Syntax-highlighted DAX as escaped markup (see Message Categories) |
| `Styling.MMarkup(expression)`           | Syntax-highlighted M as escaped markup (see Message Categories) |
| `Styling.ExpressionMarkup(language, text)` | The shared entry point for expression text: `ExpressionLanguage.Dax` or `.M` highlights (decide with `DaxExpressions.IsDaxValue`/`IsDaxExpression` and `MExpressions.IsMValue`/`IsMExpression`), `.Plain` escapes; optional `measureNames` resolves DAX measure references to their own color, optional `suffix` (e.g. `... (+2 lines)`) stays plain |
| `Styling.SeverityMarkup(severity)`      | Colored severity label (Error/Warning/Info) |
| `Styling.NewTable(params columns)`      | Rounded-border table with a dim border   |

## What NOT to Color

- **JSON output** (`--format json`) — raw JSON, no markup.
- **CSV output** (`--format csv`) — raw CSV, no markup.
- **TMDL/BIM raw output** (`--format tmdl`, `--format bim`) — raw syntax.
- **CI annotations** (`::error::`, `##vso[task.logissue...]`) — plain-text CI protocols.
- **Completion scripts** (`completion bash/zsh/fish`) — shell script output.
