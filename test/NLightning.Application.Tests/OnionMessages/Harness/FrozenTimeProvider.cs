namespace NLightning.Application.Tests.OnionMessages.Harness;

/// <summary>
/// A clock that stands still until <see cref="Advance"/> moves it.
/// </summary>
internal sealed class FrozenTimeProvider(DateTimeOffset now) : TimeProvider
{
    private DateTimeOffset _now = now;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}