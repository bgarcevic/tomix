// Replaces the `grapheme-splitter` package. The parser uses it only to turn a code-unit offset into
// a display column for error positions; it never affects lexing, parsing, or formatted output.
// The original encodes Unicode 10 break properties as `||` chains hundreds of terms long, which
// exceed the in-process host's script nesting limit and cost ~750 interpreted comparisons per
// character. This approximation keeps clusters that matter for columns together: CRLF, combining
// marks, variation selectors, ZWJ sequences, emoji modifiers, and regional-indicator flag pairs.
"use strict";

const ascii = /^[\x00-\x7F]*$/;
const extend = /^[\p{M}‍\u{1F3FB}-\u{1F3FF}\u{E0020}-\u{E007F}]$/u;
const regionalIndicator = /^[\u{1F1E6}-\u{1F1FF}]$/u;

function splitGraphemes(text) {
    const clusters = [];
    if (ascii.test(text)) {
        for (let i = 0; i < text.length; i++) {
            if (text[i] === "\r" && text[i + 1] === "\n") {
                clusters.push("\r\n");
                i++;
            } else {
                clusters.push(text[i]);
            }
        }

        return clusters;
    }

    let joinNext = false;
    for (const codePoint of text) {
        const last = clusters.length - 1;
        if (
            last >= 0 &&
            (joinNext ||
                extend.test(codePoint) ||
                (codePoint === "\n" && clusters[last] === "\r") ||
                (regionalIndicator.test(codePoint) &&
                    clusters[last].length === 2 &&
                    regionalIndicator.test(clusters[last])))
        ) {
            clusters[last] += codePoint;
        } else {
            clusters.push(codePoint);
        }

        joinNext = codePoint === "‍";
    }

    return clusters;
}

class GraphemeSplitter {
    splitGraphemes(text) {
        return splitGraphemes(text);
    }

    countGraphemes(text) {
        return splitGraphemes(text).length;
    }
}

module.exports = GraphemeSplitter;
