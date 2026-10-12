namespace NLightning.Application.Tests.OnionMessages.Harness;

/// <summary>
/// Polls a condition the service's workers make true.
/// </summary>
internal static class OnionMessageTestWaits
{
    public static async Task UntilAsync(Func<bool> condition, CancellationToken cancellationToken,
                                        int timeoutMs = 10_000)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(timeoutMs);
        while (!condition())
            await Task.Delay(10, timeout.Token);
    }
}