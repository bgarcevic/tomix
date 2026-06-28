namespace Tomix.Core.Models;

public sealed class ObjectNotFoundException : Exception
{
    public string? Hint { get; }

    public ObjectNotFoundException(string message, string? hint = null)
        : base(message)
    {
        Hint = hint;
    }
}

public sealed class AmbiguousObjectException : Exception
{
    public AmbiguousObjectException(string message)
        : base(message)
    {
    }
}

public sealed class OutputExistsException : IOException
{
    public OutputExistsException(string message)
        : base(message)
    {
    }
}
