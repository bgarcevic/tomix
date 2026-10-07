namespace Tomix.Core.Models;

/// <summary>Identifies a provider-neutral model connection failure.</summary>
public enum ModelConnectionFailureKind
{
    /// <summary>The requested database does not exist on the endpoint.</summary>
    DatabaseNotFound,

    /// <summary>The endpoint hosts several databases and none was named.</summary>
    DatabaseRequired,

    /// <summary>
    /// A local Power BI Desktop endpoint (<c>localhost:&lt;port&gt;</c>) has nothing listening:
    /// its report was closed, or Desktop restarted on another port.
    /// </summary>
    LocalInstanceGone
}

/// <summary>
/// A provider-neutral failure raised while connecting to a model endpoint. Provider adapters
/// translate infrastructure-specific errors into this type when the failure kind is known.
/// </summary>
public sealed class ModelConnectionException : Exception
{
    public ModelConnectionException(ModelConnectionFailureKind kind, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
    }

    /// <summary>The classified connection failure.</summary>
    public ModelConnectionFailureKind Kind { get; }
}
