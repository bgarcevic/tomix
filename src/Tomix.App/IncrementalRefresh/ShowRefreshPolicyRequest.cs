using Tomix.Core.Models;

namespace Tomix.App.IncrementalRefresh;

public sealed record ShowRefreshPolicyRequest(
    ModelReference Model,
    string Table);
