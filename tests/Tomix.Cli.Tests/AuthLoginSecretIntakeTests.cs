using System.CommandLine;
using Tomix.Cli.Commands;
using Tomix.Core.Authentication;

namespace Tomix.Cli.Tests;

/// <summary>
/// Secret-intake policy (docs/cli-ux-guidelines.md): secrets never arrive via argv values or
/// environment variables. The only channels are the '-' stdin sentinel, a secret file, or a
/// masked interactive prompt.
/// </summary>
[Collection(ConsoleStateCollection.Name)]
public sealed class AuthLoginSecretIntakeTests
{
    [Theory]
    [InlineData("--password")]
    [InlineData("--certificate-password")]
    public void SecretValueOnArgv_IsRejectedAtParseTime(string option)
    {
        var result = Parse("auth", "login", option, "hunter2");

        var error = Assert.Single(result.Errors);
        Assert.Contains("does not accept a secret value on the command line", error.Message);
    }

    [Theory]
    [InlineData("--password")]
    [InlineData("--certificate-password")]
    public void StdinSentinel_PassesParseValidation(string option)
    {
        var result = Parse("auth", "login", option, "-");

        Assert.Empty(result.Errors);
    }

    [Fact]
    public void StdinAndFileSources_AreMutuallyExclusive()
    {
        var (exitCode, stderr) = Invoke("auth", "login", "-u", "app", "--password", "-", "--password-file", "x.txt");

        Assert.Equal(2, exitCode);
        Assert.Contains("cannot be combined", stderr);
    }

    [Fact]
    public void MissingSecretSource_NonInteractive_FailsFastWithGuidance()
    {
        // Console input is redirected under the test runner, so the interaction gate is closed.
        var (exitCode, stderr) = Invoke("auth", "login", "-u", "app", "-t", "tenant");

        Assert.Equal(2, exitCode);
        Assert.Contains("--password -", stderr);
        Assert.Contains("--password-file", stderr);
    }

    [Fact]
    public void MissingPasswordFile_FailsWithPath()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"absent-{Guid.NewGuid():N}.txt");
        var (exitCode, stderr) = Invoke("auth", "login", "-u", "app", "--password-file", missing);

        Assert.Equal(2, exitCode);
        // Spectre wraps long lines on the test console; compare without the inserted breaks.
        Assert.Contains(missing, stderr.Replace("\r", "").Replace("\n", ""));
    }

    [Fact]
    public void Resolver_ReadsStdinSentinel_TrimmingCarriageReturn()
    {
        var resolution = AuthSecretResolver.Resolve(
            optionValue: "-", filePath: null, "--password", "--password-file",
            readStdinLine: () => "s3cret\r");

        Assert.Equal("s3cret", resolution.Secret);
        Assert.Null(resolution.ErrorCode);
    }

    [Fact]
    public void Resolver_ReadsSecretFile_TrimmingTrailingNewline()
    {
        var file = Path.Combine(Path.GetTempPath(), $"secret-{Guid.NewGuid():N}.txt");
        File.WriteAllText(file, "s3cret\r\n");
        try
        {
            var resolution = AuthSecretResolver.Resolve(
                optionValue: null, filePath: file, "--password", "--password-file",
                readStdinLine: () => null);

            Assert.Equal("s3cret", resolution.Secret);
            Assert.Null(resolution.ErrorCode);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void Resolver_EmptySecretFile_ReturnsSecretRequired()
    {
        // A file that exists but holds no secret is "no secret provided", the same code as
        // empty stdin or an empty prompt; FILE_NOT_FOUND is reserved for a missing path
        // (docs/error-codes.md).
        var file = Path.Combine(Path.GetTempPath(), $"secret-{Guid.NewGuid():N}.txt");
        File.WriteAllText(file, "\r\n");
        try
        {
            var resolution = AuthSecretResolver.Resolve(
                optionValue: null, filePath: file, "--password", "--password-file",
                readStdinLine: () => null);

            Assert.Null(resolution.Secret);
            Assert.Equal("TOMIX_AUTH_SECRET_REQUIRED", resolution.ErrorCode);
            Assert.Contains("empty", resolution.ErrorMessage);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void Resolver_UsesPromptFallback_WhenNoOtherSource()
    {
        var resolution = AuthSecretResolver.Resolve(
            optionValue: null, filePath: null, "--password", "--password-file",
            readStdinLine: () => null,
            promptFallback: () => "prompted");

        Assert.Equal("prompted", resolution.Secret);
    }

    [Theory]
    [InlineData(true, null, null, false, AuthMethod.ManagedIdentity)]
    [InlineData(false, "cert.pem", null, false, AuthMethod.ServicePrincipalCertificate)]
    [InlineData(false, null, "app-id", false, AuthMethod.ServicePrincipalSecret)]
    [InlineData(false, null, null, true, AuthMethod.DeviceCode)]
    [InlineData(false, null, null, false, AuthMethod.Interactive)]
    public void ResolveMethod_UsesIdentityInputsOnly(
        bool identity, string? certificate, string? username, bool deviceCode, AuthMethod expected)
        => Assert.Equal(expected, AuthCommand.ResolveMethod(identity, certificate, username, deviceCode));

    private static ParseResult Parse(params string[] args)
    {
        var root = new RootCommand("test");
        foreach (var option in GlobalOptions.All())
            root.Options.Add(option);
        root.Subcommands.Add(new AuthCommand().Build());
        return root.Parse(args);
    }

    private static (int ExitCode, string Stderr) Invoke(params string[] args)
    {
        var result = Parse(args);
        var original = Console.Error;
        var stderr = new StringWriter();
        Console.SetError(stderr);
        try
        {
            return (result.Invoke(), stderr.ToString());
        }
        finally
        {
            Console.SetError(original);
        }
    }
}
