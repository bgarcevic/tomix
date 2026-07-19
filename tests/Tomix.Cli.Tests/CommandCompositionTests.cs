using Tomix.App;
using Tomix.Cli.Commands;

namespace Tomix.Cli.Tests;

public sealed class CommandCompositionTests
{
    [Fact]
    public void CommandModules_DoNotDependOnEntireAppServicesBundle()
    {
        var offenders = typeof(ICommandModule).Assembly
            .GetTypes()
            .Where(type => !type.IsAbstract && typeof(ICommandModule).IsAssignableFrom(type))
            .Where(type => type.GetConstructors()
                .SelectMany(constructor => constructor.GetParameters())
                .Any(parameter => parameter.ParameterType == typeof(AppServices)))
            .Select(type => type.Name)
            .Order()
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            $"Command modules must receive exact dependencies, not AppServices: {string.Join(", ", offenders)}");
    }
}
