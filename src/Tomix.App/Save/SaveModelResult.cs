using Tomix.App.Mutations;

namespace Tomix.App.Save;

public sealed record SaveModelResult(string Format) : MutationResult;
