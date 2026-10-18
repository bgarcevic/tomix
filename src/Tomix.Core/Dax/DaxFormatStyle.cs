namespace Tomix.Core.Dax;

/// <summary>Whether a function name is followed by a space before its parenthesis.</summary>
public enum DaxFunctionSpacing
{
    /// <summary><c>IF ( … )</c> — the SQLBI best-practice layout, and the default.</summary>
    Space,

    /// <summary><c>IF( … )</c>.</summary>
    None
}

/// <summary>
/// The list and decimal separators DAX is shown with. Models always store <see cref="Us"/>; the
/// other style is how DAX reads in a locale whose decimal separator is a comma.
/// </summary>
public enum DaxSeparators
{
    /// <summary><c>A, B, C, 1234.00</c> — the invariant form every model stores.</summary>
    Us,

    /// <summary><c>A; B; C; 1234,00</c> — display only.</summary>
    Eu
}
