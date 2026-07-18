namespace Tomix.Core.Models;

/// <summary>
/// Optional mutation capability: a session that can write rewritten DAX back to
/// expression-bearing properties. Handlers capability-check with a type test instead of
/// discovering support through <see cref="NotSupportedException"/> at call time.
/// </summary>
public interface IExpressionRewriteSession
{
    /// <summary>
    /// Applies pre-computed DAX rewrites to expression-bearing properties (rename reference
    /// fixup). Each edit replaces one property's full text; the caller computed the new text
    /// from exact reference spans, so the provider only routes it to the right object property.
    /// </summary>
    ModelExpressionRewriteResult RewriteExpressions(IReadOnlyList<ModelExpressionEdit> edits);
}
