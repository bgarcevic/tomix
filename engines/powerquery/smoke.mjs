// Runs the committed bundle the way the in-process host will: a bare JavaScript context with no
// `require`, `process`, timers, or network APIs. If the bundle reaches for any of them, or the
// pinned versions drift, this fails.
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import vm from "node:vm";

const here = path.dirname(fileURLToPath(import.meta.url));
const bundle = readFileSync(path.resolve(here, "../../src/Tomix.App/Format/M/powerquery-engine.js"), "utf8");
const pkg = JSON.parse(readFileSync(path.join(here, "package.json"), "utf8"));

const context = vm.createContext({});
vm.runInContext(bundle, context, { filename: "powerquery-engine.js" });
const engine = context.TomixM;
assert.ok(engine, "bundle must define globalThis.TomixM");

assert.deepEqual(JSON.parse(engine.version), {
    formatter: pkg.dependencies["@microsoft/powerquery-formatter"],
    parser: pkg.dependencies["@microsoft/powerquery-parser"],
});

const format = async (request) => JSON.parse(await engine.format(JSON.stringify(request)));
const diagnose = async (text) => JSON.parse(await engine.diagnose(JSON.stringify({ text })));

// Round trip, and formatting is idempotent.
const simple = await format({ text: "let x = 1 in x", indentationLiteral: "    ", newlineLiteral: "\n", maxWidth: 40 });
assert.equal(simple.ok, true, JSON.stringify(simple));
// The formatter ends its output with a newline; trimming it is the host's call.
assert.equal(simple.text, "let\n    x = 1\nin\n    x\n");

// Shape of a real partition expression from samples/AdventureWorks Sales (Category table).
const sample = `let
    Source = Table.FromRows(Json.Document(Binary.Decompress(Binary.FromText("i45W", BinaryEncoding.Base64), Compression.Deflate)), let _t = ((type nullable text) meta [Serialized.Text = true]) in type table [Category = _t, Sorting = _t]),
    #"Changed Type" = Table.TransformColumnTypes(Source,{{"Category", type text}, {"Sorting", Int64.Type}})
in
    #"Changed Type"`;
for (const maxWidth of [40, 120]) {
    const first = await format({ text: sample, indentationLiteral: "    ", newlineLiteral: "\n", maxWidth });
    assert.equal(first.ok, true, JSON.stringify(first));
    const second = await format({ text: first.text, indentationLiteral: "    ", newlineLiteral: "\n", maxWidth });
    assert.equal(second.text, first.text, `formatting must be idempotent at maxWidth ${maxWidth}`);
}

// Broken M: a structured parse error with a 1-based position, never a rejection.
const broken = await format({ text: "let\n    x = 1,\nin\n    x" });
assert.equal(broken.ok, false);
assert.equal(broken.error.kind, "parse");
assert.equal(broken.error.line, 3);
assert.equal(broken.error.column, 1);
assert.ok(broken.error.message.length > 0);

// Lexer error: unterminated string.
const lexBroken = await diagnose('let x = "abc in x');
assert.deepEqual(lexBroken.errors, [{ kind: "lex", message: "Unterminated string", line: 1, column: 9 }]);

assert.deepEqual(await diagnose("let x = 1 in x"), { errors: [] });

// Invalid request JSON is reported, not thrown.
const invalid = JSON.parse(await engine.format("{"));
assert.equal(invalid.ok, false);
assert.equal(invalid.error.kind, "internal");

console.log(`powerquery engine smoke passed (formatter ${pkg.dependencies["@microsoft/powerquery-formatter"]}, parser ${pkg.dependencies["@microsoft/powerquery-parser"]}, ${(bundle.length / 1024).toFixed(0)} KB)`);
