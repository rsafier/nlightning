namespace NLightning.Integration.Tests.Docker.Utils;

/// <summary>
/// Deadline-bound polling for the Docker tests: no fixed sleeps, and a timeout says what was being waited for.
/// </summary>
public static class Poll
{
    private static readonly TimeSpan s_defaultInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Evaluates <paramref name="condition"/> until it is true.
    /// </summary>
    /// <exception cref="TimeoutException">Still false after <paramref name="timeout"/>.</exception>
    public static async Task UntilAsync(Func<Task<bool>> condition, TimeSpan timeout, string description,
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

    /// <inheritdoc cref="UntilAsync(Func{Task{bool}}, TimeSpan, string, CancellationToken, TimeSpan?)"/>
    public static Task UntilAsync(Func<bool> condition, TimeSpan timeout, string description,
                                  CancellationToken cancellationToken, TimeSpan? interval = null) =>
        UntilAsync(() => Task.FromResult(condition()), timeout, description, cancellationToken, interval);

    /// <summary>
    /// Checks that <paramref name="condition"/> stays true for <paramref name="duration"/>.
    /// </summary>
    /// <exception cref="Xunit.Sdk.XunitException">It became false; the message names the elapsed time.</exception>
    public static async Task StaysTrueAsync(Func<bool> condition, TimeSpan duration, string description,
                                            CancellationToken cancellationToken, TimeSpan? interval = null)
    {
        var start = DateTime.UtcNow;
        while (DateTime.UtcNow - start < duration)
        {
            if (!condition())
                Assert.Fail($"No longer true after {DateTime.UtcNow - start}: {description}");

            await Task.Delay(interval ?? s_defaultInterval, cancellationToken);
        }

        Assert.True(condition(), $"No longer true at the end of {duration}: {description}");
    }
}