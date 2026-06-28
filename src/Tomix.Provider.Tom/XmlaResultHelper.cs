using Microsoft.AnalysisServices;

namespace Tomix.Provider.Tom;

/// <summary>
/// AMO's <c>Server.Execute</c> returns a result collection that may contain
/// <see cref="XmlaError"/>/<see cref="XmlaWarning"/> messages even when no exception is thrown.
/// This helper extracts them so callers (deploy, refresh) can surface real failures instead of
/// silently reporting success.
/// </summary>
internal static class XmlaResultHelper
{
    /// <summary>
    /// Walks an Execute result collection and appends <see cref="XmlaError"/> descriptions to
    /// <paramref name="errors"/> and, when supplied, <see cref="XmlaWarning"/> descriptions to
    /// <paramref name="warnings"/>. No-op for null/empty collections.
    /// </summary>
    public static void ExtractMessages(
        XmlaResultCollection? results,
        List<string> errors,
        List<string>? warnings = null)
    {
        if (results is null || results.Count == 0)
            return;

        foreach (XmlaResult result in results)
        {
            foreach (XmlaMessage message in result.Messages)
            {
                if (message is XmlaError)
                    errors.Add(message.Description);
                else if (message is XmlaWarning && warnings is not null)
                    warnings.Add(message.Description);
            }
        }
    }
}
