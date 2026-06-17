using System.Linq.Dynamic.Core;
using System.Linq.Expressions;
using System.Text.RegularExpressions;

namespace Tomix.App.Bpa;

/// <summary>
/// Evaluates a Best-Practice-Analyzer rule <c>Expression</c> (a Dynamic-LINQ predicate)
/// against a collection of adapter objects, returning the elements for which the
/// predicate is <c>true</c> (i.e. the objects in violation).
///
/// The dialect is essentially what <c>System.Linq.Dynamic.Core</c> parses natively (<c>it</c>/
/// <c>outerIt</c> iterators, implicit member access inside <c>.Any()/.Where()</c>, <c>=</c> as
/// equality, <c>and</c>/<c>or</c>/<c>not</c>). A thin normalization pass maps the few
/// dialect-specific spellings that differ. A rule whose expression cannot be parsed, or which throws
/// at runtime for an element, never produces a violation — it is skipped, never a false positive.
/// </summary>
public sealed partial class BpaExpressionEvaluator
{
    private readonly ParsingConfig _config = new()
    {
        CustomTypeProvider = new BpaTypeProvider(),
        ResolveTypesBySimpleName = true,
        // Some rules call it.ToString()/Equals on object-typed elements (e.g. object-level-security
        // checks); allow those benign Object methods so such rules compile.
        AllowEqualsAndToStringMethodsOnObject = true,
    };

    /// <summary>
    /// Evaluates <paramref name="expression"/> against <paramref name="items"/>, returning the
    /// matched elements together with a status that distinguishes a clean run from a compilation
    /// failure (expression cannot be parsed/bound for this element type) or an evaluation failure
    /// (the predicate threw at runtime for an element). Never throws.
    /// </summary>
    public BpaEvaluation<T> Evaluate<T>(string expression, IReadOnlyList<T> items)
        where T : class
    {
        if (items.Count == 0)
            return BpaEvaluation<T>.Ok([]);

        var normalized = Normalize(expression);

        // Rules reference the element-in-scope by a named iterator (`current` or `outerIt`) that must
        // stay bound inside nested .Any(...) lambdas, where bare members rebind to the inner element.
        var iteratorName =
            CurrentKeyword().IsMatch(normalized) ? "current"
            : normalized.Contains("outerIt", StringComparison.Ordinal) ? "outerIt"
            : null;

        Delegate predicate;
        try
        {
            predicate = Compile<T>(normalized, iteratorName);
        }
        catch (Exception ex)
        {
            // Unsupported syntax or an unknown member for this scope — the rule cannot run here.
            return BpaEvaluation<T>.Failed(BpaEvaluationStatus.CompilationError, Unwrap(ex));
        }

        var violations = new List<T>();
        string? evalError = null;
        var args = new object[1];
        foreach (var item in items)
        {
            try
            {
                args[0] = item;

                if (predicate.DynamicInvoke(args) is true)
                    violations.Add(item);
            }
            catch (Exception ex)
            {
                // Runtime error for this element (e.g. converting a missing annotation): skip the
                // element so a clean match is never lost, but remember the first error so the caller
                // can surface it as a diagnostic instead of silently swallowing it.
                evalError ??= Unwrap(ex);
            }
        }

        return evalError is null
            ? BpaEvaluation<T>.Ok(violations)
            : new BpaEvaluation<T>(BpaEvaluationStatus.EvaluationError, violations, evalError);
    }

    /// <summary>Unwraps the <see cref="System.Reflection.TargetInvocationException"/> from a dynamic invoke.</summary>
    private static string Unwrap(Exception ex)
        => (ex.InnerException ?? ex).Message;

    private Delegate Compile<T>(string expression, string? iteratorName)
    {
        LambdaExpression lambda;
        if (iteratorName is not null)
        {
            // A single parameter named for the rule's iterator keyword: Dynamic.Core treats the lone
            // parameter as the implicit "it" (so bare members still bind), exposes it by name
            // (e.g. "current.Table.Name" / "outerIt.Expression"), and keeps it in scope inside nested
            // .Any(...) lambdas where bare members rebind to the inner element.
            var parameter = Expression.Parameter(typeof(T), iteratorName);
            lambda = DynamicExpressionParser.ParseLambda(_config, [parameter], typeof(bool), expression);
        }
        else
        {
            lambda = DynamicExpressionParser.ParseLambda(_config, typeof(T), typeof(bool), expression);
        }

        return lambda.Compile();
    }

    /// <summary>Maps rule-dialect expression spellings onto what the parser understands.</summary>
    private static string Normalize(string expression)
    {
        // Enum-style literals -> string literals (the adapter stores these properties as strings).
        var result = EnumLiteral().Replace(expression, "\"$2\"");

        // The rule dialect spells it "IsNullOrWhitespace"; the BCL method is "IsNullOrWhiteSpace".
        result = result.Replace("IsNullOrWhitespace", "IsNullOrWhiteSpace");

        // The rule dialect's outer-iterator keyword is "outerit"; Dynamic.Core spells it "outerIt".
        result = OuterItKeyword().Replace(result, "outerIt");

        // Rule expressions embed regex patterns in string literals using backslash sequences the
        // parser would reject (e.g. \s, \(, \[). Escape stray backslashes so the literal carries the
        // backslash through to the regex verbatim, matching the rule dialect's lenient string lexer.
        result = EscapeStringLiteralBackslashes(result);

        return result;
    }

    /// <summary>
    /// Doubles backslashes inside double-quoted string literals unless they form an escape the parser
    /// already understands (<c>\\</c> or <c>\"</c>), so regex patterns like <c>\s*\(</c> survive
    /// parsing as the literal text a regex expects.
    /// </summary>
    private static string EscapeStringLiteralBackslashes(string expression)
    {
        if (!expression.Contains('\\'))
            return expression;

        var sb = new System.Text.StringBuilder(expression.Length + 16);
        var inString = false;

        for (var i = 0; i < expression.Length; i++)
        {
            var c = expression[i];

            if (c == '"')
            {
                inString = !inString;
                sb.Append(c);
                continue;
            }

            if (c == '\\' && inString)
            {
                var next = i + 1 < expression.Length ? expression[i + 1] : '\0';
                if (next is '\\' or '"')
                {
                    // Already a valid escape — copy both characters unchanged.
                    sb.Append(c);
                    sb.Append(next);
                    i++;
                }
                else
                {
                    // Stray backslash (e.g. \s, \(): double it so the literal keeps one backslash.
                    sb.Append('\\');
                    sb.Append('\\');
                }
                continue;
            }

            sb.Append(c);
        }

        return sb.ToString();
    }

    [GeneratedRegex(@"\b(DataType|AggregateFunction|CrossFilteringBehavior)\.([A-Za-z0-9_]+)")]
    private static partial Regex EnumLiteral();

    [GeneratedRegex(@"\bcurrent\b")]
    private static partial Regex CurrentKeyword();

    [GeneratedRegex(@"\bouterit\b")]
    private static partial Regex OuterItKeyword();
}
