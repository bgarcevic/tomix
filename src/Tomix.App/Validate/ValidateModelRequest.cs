using Tomix.Core.Models;

namespace Tomix.App.Validate;

public sealed record ValidateModelRequest(
    ModelReference Model,
    bool ErrorsOnly,
    bool NoWarnings,
    bool ServerOnly);
