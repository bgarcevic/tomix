using Tomix.Core.Models;

namespace Tomix.App.Validate;

public sealed record ValidateModelRequest(
    ModelReference Model,
    bool ErrorsOnly,
    bool NoWarnings,
    bool ServerOnly,
    // Skips the Power Query (M) syntax check (M0001), the one check that runs the offline engine.
    bool SkipM = false);
