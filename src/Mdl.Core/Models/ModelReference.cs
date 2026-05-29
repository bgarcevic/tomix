namespace Mdl.Core.Models;

public sealed record ModelReference(string Value)
{
    public bool IsLocalPath => !string.IsNullOrWhiteSpace(Value);
}
