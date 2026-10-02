namespace NLightning.Testing.Cluster.Chain;

/// <summary>
/// Deadline-bound polling (no fixed sleeps; a timeout says what was awaited), as the Docker tests' <c>Poll</c>.
/// </summary>
public static class ChainPoll
{
    /// <summary>Evaluates <paramref name="condition"/> until it is true.</summary>
    /// <exception cref="TimeoutException">Still false after <paramref name="timeout"/>.</exception>
    public static async Task UntilAsync(Func<CancellationToken, Task<bool>> condition, TimeSpan timeout,
                                        TimeSpan interval, string description, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!await condition(cancellationToken).ConfigureAwait(false))
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException($"Timed out after {timeout} waiting for: {description}");

            await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Evaluates <paramref name="probe"/> until it returns a value, and returns it.</summary>
    /// <exception cref="TimeoutException">Still null after <paramref name="timeout"/>; the message ends with <paramref name="describeLast"/>'s text.</exception>
    public static async Task<T> ForAsync<T>(Func<CancellationToken, Task<T?>> probe, TimeSpan timeout,
                                            TimeSpan interval, string description,
                                            CancellationToken cancellationToken, Func<string>? describeLast = null)
        where T : class
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            if (await probe(cancellationToken).ConfigureAwait(false) is { } value)
                return value;

            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException($"Timed out after {timeout} waiting for: {description}"
                                         + (describeLast is null ? string.Empty : $" ({describeLast()})"));

            await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
        }
    }
}