namespace CodeGraph.Services.Pipeline;

/// <summary>
/// Carries the indexing phase a failure happened in on the exception itself, so the message
/// consumer can report it without wrapping (and so without changing the exception types the
/// MassTransit retry and redelivery filters match).
/// </summary>
public static class IndexingFailure
{
    public const string PhaseKey = "CodeGraph.IndexingPhase";
    private const int MaxDescriptionLength = 4000;

    /// <summary>Records <paramref name="phase"/> unless an inner phase was recorded first. Always false, for use in exception filters.</summary>
    public static bool TagPhase(Exception exception, string phase)
    {
        try
        {
            if (!exception.Data.Contains(PhaseKey))
                exception.Data[PhaseKey] = phase;
        }
        catch (Exception)
        {
            // Data can be read-only on exotic exception types; the phase is diagnostic only.
        }

        return false;
    }

    public static string GetPhase(Exception exception) =>
        exception.Data.Contains(PhaseKey) && exception.Data[PhaseKey] is string phase ? phase : "unknown";

    /// <summary>
    /// True for failures in the cheap phases before indexing starts (checkout fetch/resolve, HEAD
    /// and up-to-date check), which are safe to retry while the message delivery is held. A failure
    /// from indexing onward is not: an in-place retry re-runs the whole index under the same unacked
    /// delivery, and a few such runs exceed RabbitMQ's consumer_timeout.
    /// </summary>
    public static bool FailedBeforeIndexing(Exception exception) =>
        GetPhase(exception) is "resolve-repository" or "check-up-to-date";

    public static string Describe(Exception exception)
    {
        var description = $"[{GetPhase(exception)}] {exception.GetType().FullName}: {exception.Message}";
        return description.Length <= MaxDescriptionLength ? description : description[..MaxDescriptionLength];
    }
}
