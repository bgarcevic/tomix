// Replaces the `performance-now` package, which reads `process.hrtime` and so ties the bundle to
// Node. The parser calls `require("performance-now")()` for trace timestamps only, which tomix
// never enables. CommonJS so the require call gets the function itself.
module.exports = function now() {
    return Date.now();
};
