# Offline Power Query (M) engine

Microsoft's [powerquery-parser](https://github.com/microsoft/powerquery-parser) and
[powerquery-formatter](https://github.com/microsoft/powerquery-formatter), wrapped and bundled into
one script that `tx` embeds and runs in-process. Users need no Node.js and no network: npm is only
needed here, to regenerate the bundle.

| | |
|---|---|
| Output | `src/Tomix.App/Format/M/powerquery-engine.js` (committed, embedded in `Tomix.App`) |
| Pins | `@microsoft/powerquery-formatter` 1.0.1, `@microsoft/powerquery-parser` 2.0.0 (exact, lockfile) |
| Host | Jint (pure .NET JavaScript interpreter), in the `tx` process |

## Regenerate

```bash
cd engines/powerquery
npm ci
npm run build   # writes src/Tomix.App/Format/M/powerquery-engine.js
npm run smoke   # runs the bundle in a bare JS context
```

Commit the regenerated bundle with the change that caused it. CI (`m-engine` job) rebuilds it from
the lockfile and fails if the committed file differs. `ANALYZE=1 npm run build` prints a per-module
size breakdown.

## Contract

Evaluating the bundle defines `globalThis.TomixM`. Every operation takes a JSON string and resolves
to a JSON string, so the host never marshals JavaScript objects. Operations never reject: failures
come back as JSON.

| Member | Request | Response |
|---|---|---|
| `version` | (a JSON string, not a function) | `{"formatter":"1.0.1","parser":"2.0.0"}` |
| `format(json)` | `{"text", "indentationLiteral"?, "newlineLiteral"?, "maxWidth"?}` | `{"ok":true,"text"}` or `{"ok":false,"error"}` |
| `diagnose(json)` | `{"text"}` | `{"errors":[error, ...]}` (empty when the text lexes and parses) |

`error` is `{"kind":"lex"|"parse"|"internal","message","line","column"}` with 1-based `line` and
`column`, both `null` when the error has no position (for example, empty input). `newlineLiteral`
defaults to `"\n"`. Formatted text ends with a newline; the host decides whether to trim it.

## What the build changes

These choices keep the bundle small and runnable under Jint. Each one is enforced by the build or
the smoke test.

- **English-only messages.** The parser ships message templates for 42 locales; `tx` uses en-US, so
  every locale import resolves to the English template (about 400 KB smaller).
- **`performance-now` shim.** The package reads `process.hrtime`; the parser only uses it for trace
  timestamps, which `tx` never enables. Replaced with `Date.now()`.
- **`grapheme-splitter` shim.** Used only to turn offsets into display columns in error positions;
  it never affects formatted output. The original spells out Unicode break properties as `||`
  chains hundreds of terms long, which exceed Jint's hard-coded script nesting limit (256) and cost
  about 750 interpreted comparisons per character. The shim keeps combining marks, ZWJ sequences,
  emoji modifiers, flag pairs, and CRLF together, which covers what matters for columns.
- **No `minifySyntax`.** esbuild's syntax minifier merges declarations into
  `let a = f(), b = await g()`. When Jint resumes after that `await`, it runs `f()` again (V8 does
  not), which breaks the parser. Whitespace and identifier minification are kept; the build fails
  if the output contains the merged shape.
- **No Node built-ins.** Importing any Node module fails the build, and the smoke test runs the
  bundle in a context with no `require` or `process`.

The upstream MIT notices are appended to the bundle and listed in `THIRD-PARTY-NOTICES.md`.

## Measured under Jint 4.16.4

113 M expressions from `/samples`, one reused engine: bundle evaluation about 0.46 s, formatting
about 3.8 s in total (about 33 ms per expression, with the first calls slowest while Jint warms up).
The output is byte-identical to Node on all 113.
