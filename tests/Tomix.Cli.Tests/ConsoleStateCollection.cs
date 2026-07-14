namespace Tomix.Cli.Tests;

/// <summary>
/// Serializes test classes that read or swap the process-global console writers
/// (<see cref="Console.SetError"/>, <c>Console.Error</c> identity checks). xUnit runs each test
/// class as its own collection, in parallel — so a class swapping <c>Console.Error</c> mid-run
/// makes any concurrent identity assertion flaky. Put every class that touches global console
/// state in this collection.
/// </summary>
[CollectionDefinition(Name)]
public sealed class ConsoleStateCollection
{
    public const string Name = "console-state";
}
