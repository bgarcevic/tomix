namespace Tomix.App.Mutations;

/// <summary>
/// Ambient progress channel from long-running mutation phases (workspace sync crosses the
/// network) up to whatever surface is showing progress — the CLI wires it to the live spinner
/// label. AsyncLocal so it flows through the handler's async call chain without threading a
/// callback through every request record; a no-op when nothing is listening (JSON/quiet/CI).
/// </summary>
public static class MutationProgress
{
    private static readonly AsyncLocal<Action<string>?> Reporter = new();

    /// <summary>Reports a phase message to the active listener, if any.</summary>
    public static void Report(string message) => Reporter.Value?.Invoke(message);

    /// <summary>Registers <paramref name="reporter"/> for the current async flow.</summary>
    public static IDisposable Use(Action<string> reporter)
    {
        var previous = Reporter.Value;
        Reporter.Value = reporter;
        return new Scope(() => Reporter.Value = previous);
    }

    private sealed class Scope(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }
}
