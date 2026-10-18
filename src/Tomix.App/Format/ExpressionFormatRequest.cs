using Tomix.Core.Dax;

namespace Tomix.App.Format;

/// <summary>
/// One expression to format. <paramref name="Long"/> picks the long-lines layout (DAX and M);
/// <paramref name="FunctionSpacing"/> applies to DAX only.
/// </summary>
public sealed record ExpressionFormatRequest(
    string Expression,
    string Language,
    bool Long,
    DaxFunctionSpacing FunctionSpacing = DaxFunctionSpacing.Space);
