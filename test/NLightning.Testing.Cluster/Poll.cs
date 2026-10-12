namespace NLightning.Testing.Cluster;

using Diagnostics;

/// <summary>
/// The harness's one deadline-bound polling helper (no fixed sleeps; a timeout says what was awaited and what was
/// last seen), as the Docker tests' <c>Poll</c>. Every wait of the chain helpers, the topologies and the node adapters
/// goes through it.
/// </summary>
/// <remarks>
/// A timeout first dumps the live runs of the current test or fixture (<see cref="ClusterDiagnostics"/>; not with
/// <c>NLTG_CLUSTER_DIAG=off</c> or inside <see cref="ClusterDiagnostics.SuppressPollCapture"/>) while the state that
/// made it time out is still there, then throws.
/// </remarks>
public static class Poll
{
    /// <summary>The interval when none is given.</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>Evaluates <paramref name="condition"/> every <paramref name="interval"/> until it is true.</summary>
    /// <exception cref="TimeoutException">Still false after <paramref name="timeout"/>.</exception>
    public static async Task UntilAsync(Func<CancellationToken, Task<bool>> condition, TimeSpan timeout,
                                        TimeSpan interval, string description, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(condition);
        var deadline = DateTime.UtcNow + timeout;
        while (!await condition(cancellationToken).ConfigureAwait(false))
        {
            if (DateTime.UtcNow >= deadline)
                throw await TimedOutAsync($"Timed out after {timeout} waiting for: {description}")
                          .ConfigureAwait(false);

            await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Evaluates <paramref name="check"/> every <paramref name="interval"/> (<see cref="DefaultInterval"/>) until it
    /// returns null (done); a non-null result describes what is still missing and ends the timeout's message.
    /// </summary>
    /// <exception cref="TimeoutException">Not done in time; the message carries the last description.</exception>
    public static async Task UntilDoneAsync(Func<CancellationToken, Task<string?>> check, TimeSpan timeout,
                                            string description, CancellationToken cancellationToken,
                                            TimeSpan? interval = null)
    {
        ArgumentNullException.ThrowIfNull(check);
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var missing = await check(cancellationToken).ConfigureAwait(false);
            if (missing is null)
                return;
            if (DateTime.UtcNow >= deadline)
                throw await TimedOutAsync($"Timed out after {timeout} waiting for: {description}: {missing}")
                          .ConfigureAwait(false);

            await Task.Delay(interval ?? DefaultInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Evaluates <paramref name="probe"/> until it returns a value, and returns it.</summary>
    /// <exception cref="TimeoutException">
    /// Still null after <paramref name="timeout"/>; the message ends with <paramref name="describeLast"/>'s text.
    /// </exception>
    public static async Task<T> ForAsync<T>(Func<CancellationToken, Task<T?>> probe, TimeSpan timeout,
                                            TimeSpan interval, string description,
                                            CancellationToken cancellationToken, Func<string>? describeLast = null)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(probe);
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            if (await probe(cancellationToken).ConfigureAwait(false) is { } value)
                return value;

            if (DateTime.UtcNow >= deadline)
                throw await TimedOutAsync($"Timed out after {timeout} waiting for: {description}"
                                        + (describeLast is null ? string.Empty : $" ({describeLast()})"))
                          .ConfigureAwait(false);

            await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<TimeoutException> TimedOutAsync(string message)
    {
        await ClusterDiagnostics.OnPollTimeoutAsync(message).ConfigureAwait(false);
        return new TimeoutException(message);
    }
}