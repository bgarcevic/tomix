namespace Tomix.Core.Models;

/// <summary>
/// A deploy without a target database name whose source model has no name of its own (such as a
/// bare TMDL folder), so there is nothing to deploy as. The caller must pass a database name.
/// </summary>
public sealed class DeployTargetNameRequiredException : Exception
{
    public DeployTargetNameRequiredException()
        : base("The model has no name to deploy as.")
    {
    }
}
