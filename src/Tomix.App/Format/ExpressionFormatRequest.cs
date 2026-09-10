namespace Tomix.App.Format;

public sealed record ExpressionFormatRequest(
    string Expression,
    string Language,
    bool Long);
