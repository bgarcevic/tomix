using System.CommandLine;
using Tomix.Cli.Commands;
using Tomix.Core.Models;

namespace Tomix.Cli.Tests;

/// <summary>
/// Beta-polish contract for <c>connect</c>: usage errors exit 2 like every other command
/// (they exited 1 before v-beta), and connection failures honor <c>--error-format json</c>.
/// </summary>
[Collection(ConsoleStateCollection.Name)]
public sealed class ConnectExitCodeTests
{
    private static readonly IReadOnlyList<IModelProvider> NoProviders = [];

    [Theory]
    [InlineData("connect", "--remote", "somehost", "db")]
    [InlineData("connect", "-w", "some-folder")]
    public void UsageErrors_ExitTwo(params string[] args)
    {
        var (exitCode, stderr) = Invoke(NoProviders, args);

        Assert.Equal(2, exitCode);
        Assert.NotEmpty(stderr);
    }

    [Fact]
    public void ValidationFailure_UsesJsonEnvelope_WhenErrorFormatJson()
    {
        var model = Directory.CreateTempSubdirectory("tomix-connect-").FullName;
        try
        {
            var (exitCode, stderr) = Invoke(
                [new ThrowingProvider()],
                "connect", model, "--error-format", "json");

            Assert.NotEqual(0, exitCode);
            Assert.Contains("\"code\": \"TOMIX_MODEL_LOAD_FAILED\"", stderr);
        }
        finally
        {
            Directory.Delete(model, recursive: true);
        }
    }

    private static (int ExitCode, string Stderr) Invoke(IReadOnlyList<IModelProvider> providers, params string[] args)
    {
        var root = new RootCommand("test");
        foreach (var option in GlobalOptions.All())
            root.Options.Add(option);
        root.Subcommands.Add(new ConnectCommand(providers, FakeWorkspaceCatalog.Empty, () => null, TestServices.Create()).Build());

        var parseResult = root.Parse(args);
        var original = Console.Error;
        var stderr = new StringWriter();
        Console.SetError(stderr);
        try
        {
            return (Program.Invoke(parseResult), stderr.ToString());
        }
        finally
        {
            Console.SetError(original);
        }
    }

    private sealed class ThrowingProvider : IModelProvider
    {
        public bool CanOpen(ModelReference _) => true;

        public Task<IModelSession> OpenAsync(ModelReference reference, CancellationToken ct)
            => throw new ModelLoadException("Cannot load model.", new InvalidOperationException("inner"));
    }
}
