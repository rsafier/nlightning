namespace NLightning.Application.Tests.InteractiveTx.TestDoubles;

using Application.InteractiveTx.Interfaces;
using Domain.Protocol.InteractiveTx;
using Domain.Protocol.InteractiveTx.Models;

/// <summary>A clock that moves only when the test says so.</summary>
internal sealed class InteractiveTxTestClock : TimeProvider
{
    private DateTimeOffset _now = new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>
/// The reference engine without its IT-RBF-01 double-spend check (it is told of no earlier attempt), standing for an
/// engine that does not apply that rule: the driver's own check must catch it.
/// </summary>
internal sealed class NoRbfRuleInteractiveTxEngine : IInteractiveTxEngine
{
    private readonly ReferenceInteractiveTxEngine _inner = new();

    public IInteractiveTxNegotiation Create(InteractiveTxSessionParameters parameters) =>
        _inner.Create(parameters with { PreviousAttempts = [] });

    public IInteractiveTxNegotiation Restore(InteractiveTxSessionModel model,
                                             InteractiveTxSessionParameters parameters) =>
        _inner.Restore(model, parameters with { PreviousAttempts = [] });
}