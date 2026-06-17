namespace Tomix.App.Diagnostics;

/// <summary>
/// Builds the message for a failed remote XMLA connection, unwrapping to the innermost exception
/// and attaching an actionable hint for the common Power BI "authentication failed" case (which
/// usually means the workspace has no XMLA endpoint, the account lacks access, or a custom
/// <c>auth.clientId</c> is not on Microsoft's approved Power BI client-app list).
/// </summary>
public static class RemoteConnectError
{
    public static string Describe(string endpoint, Exception exception)
    {
        var innermost = exception;
        while (innermost.InnerException is not null)
            innermost = innermost.InnerException;

        var message = $"Could not connect to '{endpoint}': {innermost.Message}";

        if (innermost.Message.Contains("Authentication failed", StringComparison.OrdinalIgnoreCase))
            message +=
                "\nHint: confirm the workspace is on Premium/PPU/Fabric capacity with the XMLA endpoint enabled (Read) " +
                "and that your account can access it. If you overrode auth.clientId (TOMIX_AUTH_CLIENT_ID), ensure it is an " +
                "app the Power BI XMLA endpoint trusts (one of Microsoft's approved Power BI client apps, or your own " +
                "app with delegated Power BI Service permission). The built-in default (Power BI Desktop) works out of the box.";

        return message;
    }
}
