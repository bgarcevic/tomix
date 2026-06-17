using Tomix.Core.Models;

namespace Tomix.App.Find;

public sealed record FindModelRequest(
    ModelReference Model,
    string Pattern,
    string Scope,
    bool Regex,
    bool CaseSensitive);
