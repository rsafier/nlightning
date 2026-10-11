namespace NLightning.Tests.Utils;

/// <summary>
/// Deadline-bound waiting for unit tests: no fixed sleeps, and a timeout says what was being waited for. The
/// deadline is generous on purpose — under a loaded parallel run everything takes longer, so a wait that survives
/// load asserts the condition, not the machine's speed (the loaded-run flake antidote, NL-382 et al.).
/// </summary>
public static class WaitFor
{
    private static readonly TimeSpan s_defaultInterval = TimeSpan.FromMilliseconds(10);

    /// <summary>
    /// Evaluates <paramref name="condition"/> until it is true.
    /// </summary>
    /// <exception cref="TimeoutException">Still false after <paramref name="timeout"/>.</exception>
    public static async Task TrueAsync(Func<Task<bool>> condition, TimeSpan timeout, string description,
                                       CancellationToken cancellationToken, TimeSpan? interval = null)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"Timed out after {timeout} waiting for: {description}");

            await Task.Delay(interval ?? s_defaultInterval, cancellationToken);
        }
    }

    /// <inheritdoc cref="TrueAsync(Func{Task{bool}}, TimeSpan, string, CancellationToken, TimeSpan?)"/>
    public static Task TrueAsync(Func<bool> condition, TimeSpan timeout, string description,
                                 CancellationToken cancellationToken, TimeSpan? interval = null) =>
        TrueAsync(() => Task.FromResult(condition()), timeout, description, cancellationToken, interval);

    /// <summary>
    /// Evaluates <paramref name="probe"/> until it returns a value, and returns that value.
    /// </summary>
    /// <exception cref="TimeoutException">Still null after <paramref name="timeout"/>.</exception>
    public static async Task<T> ValueAsync<T>(Func<Task<T?>> probe, TimeSpan timeout, string description,
                                              CancellationToken cancellationToken, TimeSpan? interval = null)
        where T : class
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            if (await probe() is { } value)
                return value;

            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"Timed out after {timeout} waiting for: {description}");

            await Task.Delay(interval ?? s_defaultInterval, cancellationToken);
        }
    }

    /// <inheritdoc cref="ValueAsync{T}(Func{Task{T?}}, TimeSpan, string, CancellationToken, TimeSpan?)"/>
    public static Task<T> ValueAsync<T>(Func<T?> probe, TimeSpan timeout, string description,
                                        CancellationToken cancellationToken, TimeSpan? interval = null)
        where T : class =>
        ValueAsync(() => Task.FromResult(probe()), timeout, description, cancellationToken, interval);
}