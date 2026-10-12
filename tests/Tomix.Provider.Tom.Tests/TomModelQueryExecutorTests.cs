namespace Tomix.Provider.Tom.Tests;

public sealed class TomModelQueryExecutorTests
{
    [Theory]
    [InlineData(
        "Either the user '<euii>app:123@tenant</euii>' does not have permission, or the database does not exist.\r\n\r\nTechnical Details:\r\nRootActivityId: 5d17",
        "Either the user 'app:123@tenant' does not have permission, or the database does not exist.")]
    [InlineData("Access denied.", "Access denied.")]
    [InlineData("\r\n  Access denied.  \r\n", "Access denied.")]
    [InlineData("", "")]
    public void ServerReason_KeepsTheFirstLineWithoutIdentityMarkers(string message, string expected)
        => Assert.Equal(expected, TomModelQueryExecutor.ServerReason(message));
}
