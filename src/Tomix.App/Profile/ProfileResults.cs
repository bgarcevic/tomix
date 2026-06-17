using Tomix.App.State;

namespace Tomix.App.Profile;

public sealed record ProfileListResult(IReadOnlyDictionary<string, CliProfile> Profiles);

public sealed record ProfileShowResult(CliProfile Profile);

public sealed record ProfileSetResult(CliProfile Profile);

public sealed record ProfileRemoveResult(string Name, bool Removed);
