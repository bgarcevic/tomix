namespace Tomix.Core.Models;

/// <summary>
/// Optional mutation capability: a session that can move an object to a different parent
/// (optionally renaming it in the same operation). Handlers capability-check with a type test
/// instead of discovering support through <see cref="NotSupportedException"/> at call time.
/// Only measures are movable — every other table child is bound to its table's data.
/// </summary>
public interface IObjectMoveSession
{
    ModelObjectMutationResult MoveObject(ModelObjectMoveRequest request);
}
