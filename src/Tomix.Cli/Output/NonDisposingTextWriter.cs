namespace Tomix.Cli.Output;

/// <summary>
/// Delegates all writes to a shared <see cref="TextWriter"/> (for example
/// <see cref="Console.Error"/>) but never disposes it. The CLI owns only the
/// <see cref="StreamWriter"/> instances it opens for <c>--trace &lt;file&gt;</c>;
/// <see cref="Console.Error"/> is process-shared and must survive the command's
/// <c>using</c> scope, otherwise later stderr writes throw
/// <see cref="ObjectDisposedException"/>.
/// </summary>
internal sealed class NonDisposingTextWriter : TextWriter
{
    private readonly TextWriter _inner;

    private NonDisposingTextWriter(TextWriter inner) => _inner = inner;

    public static NonDisposingTextWriter Wrap(TextWriter inner) => new(inner);

    /// <summary>The wrapped shared writer (exposed for assertions).</summary>
    public TextWriter Inner => _inner;

    public override System.Text.Encoding Encoding => _inner.Encoding;

    public override void Write(char value) => _inner.Write(value);
    public override void Write(string? value) => _inner.Write(value);
    public override void WriteLine() => _inner.WriteLine();
    public override void WriteLine(string? value) => _inner.WriteLine(value);
    public override void WriteLine(char value) => _inner.WriteLine(value);
    public override Task WriteAsync(char value) => _inner.WriteAsync(value);
    public override Task WriteAsync(string? value) => _inner.WriteAsync(value);
    public override Task WriteLineAsync(string? value) => _inner.WriteLineAsync(value);
    public override void Flush() => _inner.Flush();
    public override Task FlushAsync() => _inner.FlushAsync();
    public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

    protected override void Dispose(bool disposing)
    {
        // Intentionally do not dispose the shared inner writer (e.g. Console.Error).
        base.Dispose(disposing);
    }
}
