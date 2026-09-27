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
    // Stable camelCase name of the upstream error (e.g. "expectedTokenKind"); null for internal errors.
    readonly code: string | null;
    readonly message: string;
    // 1-based; null when the error carries no position.
    readonly line: number | null;
    readonly column: number | null;
    // 1-based and inclusive (the offending token's last character); null when only a start is known.
    readonly endLine: number | null;
    readonly endColumn: number | null;
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
            PQP.ResultUtils.isOk(result) ? { ok: true, text: result.value } : { ok: false, error: toEngineError(result.error, request.text) },
        );
    } catch (error) {
        return JSON.stringify({ ok: false, error: toEngineError(error) });
    }
}

async function diagnose(requestJson: string): Promise<string> {
    try {
        const request = JSON.parse(requestJson) as DiagnoseRequest;
        const task = await PQP.TaskUtils.tryLexParse(PQP.DefaultSettings, request.text);
        return JSON.stringify({ errors: PQP.TaskUtils.isError(task) ? [toEngineError(task.error, request.text)] : [] });
    } catch (error) {
        return JSON.stringify({ errors: [toEngineError(error)] });
    }
}

function toEngineError(error: unknown, text?: string): EngineError {
    const kind = errorKind(error);
    const inner = innermost(error);
    // The innermost message is the specific one: the lexer's line-map wrapper only says
    // "Error on line(s): 0". Read `message` as data: LexError's broken prototype makes
    // String(error) throw.
    const message = messageOf(inner) ?? messageOf(error) ?? Object.prototype.toString.call(error);
    // A parse error that ran out of tokens has no found token: it points at the end of the input.
    const span = kind === "internal" ? undefined : (spanOf(inner) ?? (kind === "parse" ? endOf(text) : undefined));
    return {
        kind,
        code: kind === "internal" ? null : codeOf(inner),
        message,
        line: span ? span.start.lineNumber + 1 : null,
        column: span ? span.start.column + 1 : null,
        endLine: span?.end ? span.end.lineNumber + 1 : null,
        endColumn: span?.end ? span.end.column : null,
    };
}

function messageOf(error: unknown): string | undefined {
    const message = (error as { message?: unknown } | null)?.message;
    return typeof message === "string" && message.length > 0 ? message : undefined;
}

// The last character of the input, skipping trailing whitespace so the position lands on code.
function endOf(text: string | undefined): Span | undefined {
    if (text === undefined) {
        return undefined;
    }

    const lines = text.trimEnd().split(/\r\n|\r|\n/);
    const lineNumber = lines.length - 1;
    const column = Math.max(0, lines[lineNumber].length - 1);
    return { start: { lineNumber, column } };
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

// Walk the wrappers (LexError / ParseError / BadStateError `innerError`, and the lexer's
// per-line ErrorLineMapError) down to the error that says what actually went wrong.
function innermost(error: unknown): unknown {
    let current = error;
    for (let depth = 0; depth < 5 && current !== null && typeof current === "object"; depth++) {
        const candidate = current as { innerError?: unknown; errorLineMap?: unknown };
        if (candidate.errorLineMap instanceof Map) {
            const first = candidate.errorLineMap.values().next();
            current = first.done ? current : (first.value as { error?: unknown }).error;
            if (first.done) break;
            continue;
        }

        if (candidate.innerError === undefined) break;
        current = candidate.innerError;
    }

    return current;
}

// The bundle mangles class names, so codes come from this table rather than `constructor.name`.
// Codes are part of tx's JSON output: rename one only with a breaking-change note.
const errorCodes: ReadonlyArray<readonly [abstract new (...args: never[]) => unknown, string]> = [
    [PQP.Parser.ParseError.ExpectedAnyTokenKindError, "expectedAnyTokenKind"],
    [PQP.Parser.ParseError.ExpectedClosingTokenKind, "expectedClosingTokenKind"],
    [PQP.Parser.ParseError.ExpectedCsvContinuationError, "expectedCsvContinuation"],
    [PQP.Parser.ParseError.ExpectedGeneralizedIdentifierError, "expectedGeneralizedIdentifier"],
    [PQP.Parser.ParseError.ExpectedTokenKindError, "expectedTokenKind"],
    [PQP.Parser.ParseError.InvalidCatchFunctionError, "invalidCatchFunction"],
    [PQP.Parser.ParseError.InvalidPrimitiveTypeError, "invalidPrimitiveType"],
    [PQP.Parser.ParseError.RequiredParameterAfterOptionalParameterError, "requiredParameterAfterOptionalParameter"],
    [PQP.Parser.ParseError.UnterminatedSequence, "unterminatedSequence"],
    [PQP.Parser.ParseError.UnusedTokensRemainError, "unusedTokensRemain"],
    [PQP.Lexer.LexError.BadLineNumberError, "badLineNumber"],
    [PQP.Lexer.LexError.BadRangeError, "badRange"],
    [PQP.Lexer.LexError.EndOfStreamError, "endOfStream"],
    [PQP.Lexer.LexError.ExpectedError, "expected"],
    [PQP.Lexer.LexError.UnexpectedEofError, "unexpectedEof"],
    [PQP.Lexer.LexError.UnexpectedReadError, "unexpectedRead"],
    [PQP.Lexer.LexError.UnterminatedMultilineTokenError, "unterminatedMultilineToken"],
];

function codeOf(error: unknown): string | null {
    for (const [type, code] of errorCodes) {
        if (error instanceof type) {
            return code;
        }
    }

    return null;
}

interface Position {
    readonly lineNumber: number;
    // 0-based start column, or for an end position the 0-based exclusive end (= 1-based inclusive).
    readonly column: number;
}

interface Span {
    readonly start: Position;
    readonly end?: Position;
}

// Inner errors carry their location in different fields: a grapheme position (`positionStart`
// or `graphemePosition`) and/or the offending token (`foundToken.token`, `firstUnusedToken`,
// `startToken`, `token`, `missingOptionalToken`). The grapheme position is the better start
// (it counts characters, not UTF-16 code units); the token supplies the end.
function spanOf(error: unknown): Span | undefined {
    if (error === null || typeof error !== "object") {
        return undefined;
    }

    const candidate = error as Record<string, unknown>;
    const token = tokenOf(candidate);
    const grapheme = graphemeOf(candidate.positionStart) ?? graphemeOf(candidate.graphemePosition);
    const foundColumn = (candidate.foundToken as { columnNumber?: unknown } | undefined)?.columnNumber;

    const start =
        grapheme ??
        (token && typeof foundColumn === "number"
            ? { lineNumber: token.positionStart.lineNumber, column: foundColumn }
            : token
              ? { lineNumber: token.positionStart.lineNumber, column: token.positionStart.lineCodeUnit }
              : undefined);
    if (!start) {
        return undefined;
    }

    const end = token ? { lineNumber: token.positionEnd.lineNumber, column: token.positionEnd.lineCodeUnit } : undefined;
    return { start, end };
}

interface TokenLike {
    readonly positionStart: { readonly lineNumber: number; readonly lineCodeUnit: number };
    readonly positionEnd: { readonly lineNumber: number; readonly lineCodeUnit: number };
}

function tokenOf(error: Record<string, unknown>): TokenLike | undefined {
    const found = (error.foundToken as { token?: unknown } | undefined)?.token;
    for (const value of [found, error.firstUnusedToken, error.startToken, error.token, error.missingOptionalToken]) {
        if (isToken(value)) {
            return value;
        }
    }

    return undefined;
}

function isToken(value: unknown): value is TokenLike {
    const token = value as Partial<TokenLike> | null | undefined;
    return (
        typeof token?.positionStart?.lineNumber === "number" &&
        typeof token.positionStart.lineCodeUnit === "number" &&
        typeof token.positionEnd?.lineNumber === "number" &&
        typeof token.positionEnd.lineCodeUnit === "number"
    );
}

function graphemeOf(value: unknown): Position | undefined {
    const position = value as { lineNumber?: unknown; columnNumber?: unknown } | null | undefined;
    return typeof position?.lineNumber === "number" && typeof position.columnNumber === "number"
        ? { lineNumber: position.lineNumber, column: position.columnNumber }
        : undefined;
}

(globalThis as Record<string, unknown>).TomixM = Object.freeze({ version, format, diagnose });
