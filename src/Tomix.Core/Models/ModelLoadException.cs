namespace Tomix.Core.Models;

/// <summary>
/// Thrown when a model source cannot be loaded at all (unparsable TMDL/BIM, unresolvable
/// references, unreadable file). Providers wrap their implementation-specific load errors in
/// this type so callers can report the failure without depending on provider internals.
/// </summary>
public sealed class ModelLoadException : Exception
{
    public ModelLoadException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
