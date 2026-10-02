namespace NLightning.Testing.Cluster.Topology;

/// <summary>Polling until a condition holds, with the last observation in the timeout message.</summary>
public static class TopologyPoll
{
    /// <summary>
    /// Polls <paramref name="check"/> every <paramref name="interval"/> (250 ms) until it returns null (done) or
    /// <paramref name="timeout"/> passes; a non-null result describes what is still missing.
    /// </summary>
    /// <exception cref="TimeoutException">Not done in time; the message carries the last description.</exception>
    public static async Task UntilAsync(Func<CancellationToken, Task<string?>> check, TimeSpan timeout, string what,
                                        CancellationToken cancellationToken, TimeSpan? interval = null)
    {
        ArgumentNullException.ThrowIfNull(check);
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var missing = await check(cancellationToken).ConfigureAwait(false);
            if (missing is null)
                return;
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException($"{what} not reached after {timeout}: {missing}");

            await Task.Delay(interval ?? TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
        }
    }
}