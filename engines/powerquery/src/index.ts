// Offline Power Query (M) engine for tx. Bundled by build.mjs into
// src/Tomix.App/Format/M/powerquery-engine.js and evaluated in-process by the .NET host.
//
// Contract: the bundle defines `globalThis.TomixM`. Every operation takes a JSON string and
// resolves to a JSON string, so the host never marshals JavaScript objects. Operations never
// reject: failures come back as structured JSON.
import * as PQF from "@microsoft/powerquery-formatter";
import * as PQP from "@microsoft/powerquery-parser";

declare const __FORMATTER_VERSION__: string;
declare const __PARSER_VERSION__: string;

type ErrorKind = "lex" | "parse" | "internal";

interface EngineError {
    readonly kind: ErrorKind;
    readonly message: string;
    // 1-based; null when the error carries no position.
    readonly line: number | null;
    readonly column: number | null;
}

interface FormatRequest {
    readonly text: string;
    readonly indentationLiteral?: string;
    readonly newlineLiteral?: string;
    readonly maxWidth?: number;
}

interface DiagnoseRequest {
    readonly text: string;
}

const version = JSON.stringify({
    formatter: __FORMATTER_VERSION__,
    parser: __PARSER_VERSION__,
});

async function format(requestJson: string): Promise<string> {
    try {
        const request = JSON.parse(requestJson) as FormatRequest;
        const settings: PQF.FormatSettings = {
            ...PQF.DefaultSettings,
            indentationLiteral: (request.indentationLiteral ??
                PQF.DefaultSettings.indentationLiteral) as PQF.IndentationLiteral,
            newlineLiteral: (request.newlineLiteral ?? "\n") as PQF.NewlineLiteral,
            maxWidth: request.maxWidth ?? PQF.DefaultSettings.maxWidth,
        };

        const result = await PQF.tryFormat(settings, request.text);
        return JSON.stringify(
            PQP.ResultUtils.isOk(result) ? { ok: true, text: result.value } : { ok: false, error: toEngineError(result.error) },
        );
    } catch (error) {
        return JSON.stringify({ ok: false, error: toEngineError(error) });
    }
}

async function diagnose(requestJson: string): Promise<string> {
    try {
        const request = JSON.parse(requestJson) as DiagnoseRequest;
        const task = await PQP.TaskUtils.tryLexParse(PQP.DefaultSettings, request.text);
        return JSON.stringify({ errors: PQP.TaskUtils.isError(task) ? [toEngineError(task.error)] : [] });
    } catch (error) {
        return JSON.stringify({ errors: [toEngineError(error)] });
    }
}

function toEngineError(error: unknown): EngineError {
    const kind = errorKind(error);
    // Read `message` as data: LexError's broken prototype makes String(error) throw.
    const rawMessage = (error as { message?: unknown } | null)?.message;
    const message = typeof rawMessage === "string" ? rawMessage : Object.prototype.toString.call(error);
    const position = findPosition(error, 0);
    return {
        kind,
        message,
        line: position ? position.lineNumber + 1 : null,
        column: position ? position.column + 1 : null,
    };
}

// Upstream LexError calls `Object.setPrototypeOf(this, LexError)` (missing `.prototype`), so the
// outer wrapper fails `instanceof`. The inner errors it carries are ordinary class instances.
function errorKind(error: unknown): ErrorKind {
    const inner = (error as { innerError?: unknown } | null)?.innerError;
    if (PQP.Lexer.LexError.isTInnerLexError(inner) || PQP.Lexer.LexError.isTLexError(inner)) {
        return "lex";
    }

    if (PQP.Parser.ParseError.isTInnerParseError(inner)) {
        return "parse";
    }

    return "internal";
}

interface Position {
    readonly lineNumber: number;
    readonly column: number;
}

// Parser and lexer errors put their location in different places (positionStart,
// graphemePosition, foundToken.token.positionStart, an errorLineMap, or behind innerError).
// Walk the known carriers instead of switching on every error class.
function findPosition(value: unknown, depth: number): Position | undefined {
    if (depth > 4 || value === null || typeof value !== "object") {
        return undefined;
    }

    const candidate = value as Record<string, unknown>;
    if (typeof candidate.lineNumber === "number" && typeof candidate.lineCodeUnit === "number") {
        const column = typeof candidate.columnNumber === "number" ? candidate.columnNumber : candidate.lineCodeUnit;
        return { lineNumber: candidate.lineNumber, column };
    }

    for (const key of ["positionStart", "graphemePosition", "foundToken", "token", "innerError"]) {
        const found = findPosition(candidate[key], depth + 1);
        if (found) {
            return found;
        }
    }

    if (candidate.errorLineMap instanceof Map) {
        for (const lineError of candidate.errorLineMap.values()) {
            const found = findPosition((lineError as Record<string, unknown>).error, depth + 1);
            if (found) {
                return found;
            }
        }
    }

    return undefined;
}

(globalThis as Record<string, unknown>).TomixM = Object.freeze({ version, format, diagnose });
