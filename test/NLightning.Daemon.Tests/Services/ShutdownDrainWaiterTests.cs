using Microsoft.Extensions.Logging.Abstractions;

namespace NLightning.Daemon.Tests.Services;

using Application.Node.Services;
using Daemon.Services;

/// <summary>
/// The drain wait of <c>shutdown --wait</c> (NL-592): idle (held for the settle period) drains and the gate stays
/// closed for the stop; a timeout reports the last busy state and opens the gate again; a cancellation (the client
/// went away) throws and opens it.
/// </summary>
public sealed class ShutdownDrainWaiterTests
{
    private readonly NodeDrainState _drainState = new();
    private readonly FakeBusyMonitor _busyMonitor = new();

    private ShutdownDrainWaiter CreateWaiter(TimeSpan? settle = null, TimeSpan? poll = null) =>
        new(_busyMonitor, NullLogger<ShutdownDrainWaiter>.Instance)
        {
            Settle = settle ?? TimeSpan.FromMilliseconds(40),
            Poll = poll ?? TimeSpan.FromMilliseconds(10)
        };

    [Fact]
    public async Task Given_AnIdleNode_When_Waited_Then_ItDrainsAndTheGateStaysClosed()
    {
        // Arrange: the caller (the handler) began the drain before waiting
        _busyMonitor.Default = FakeBusyMonitor.Idle(3);
        Assert.True(_drainState.TryBeginDrain());
        var waiter = CreateWaiter();

        // Act
        var result = await waiter.WaitUntilIdleAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // Assert: the drain holds until the host stops
        Assert.True(result.Drained);
        Assert.Equal(0, result.Reported.HtlcsInFlight);
        Assert.Equal(3, result.Reported.ChannelCount);
        Assert.True(_drainState.IsDraining);
    }

    [Fact]
    public async Task Given_HtlcsThatResolveDuringTheWait_When_Waited_Then_ItDrainsOnTheIdleTurn()
    {
        // Arrange: two busy turns, then idle; the settle period holds it
        Assert.True(_drainState.TryBeginDrain());
        _busyMonitor.Queue(FakeBusyMonitor.Busy(1, 2));
        _busyMonitor.Queue(FakeBusyMonitor.Busy(1, 1));
        _busyMonitor.Default = FakeBusyMonitor.Idle(1);
        var waiter = CreateWaiter();

        // Act
        var result = await waiter.WaitUntilIdleAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // Assert
        Assert.True(result.Drained);
        Assert.Equal(1, result.Reported.ChannelCount);
    }

    [Fact]
    public async Task Given_ANodeStillBusyAtTheTimeout_When_Waited_Then_ItTimesOutAndReopensTheGate()
    {
        // Arrange
        Assert.True(_drainState.TryBeginDrain());
        _busyMonitor.Default = FakeBusyMonitor.Busy(1, 2);
        var waiter = CreateWaiter();

        // Act
        var result = await waiter.WaitUntilIdleAsync(TimeSpan.FromMilliseconds(80),
                                                     TestContext.Current.CancellationToken);

        // Assert: the caller ends the drain on a timeout (the handler does), the node goes on, and the response
        // reports what is still busy
        Assert.False(result.Drained);
        Assert.Equal(2, result.Reported.HtlcsInFlight);
        Assert.Equal(1, result.Reported.NegotiationCount);
        _drainState.EndDrain();
        Assert.False(_drainState.IsDraining);
    }

    [Fact]
    public async Task Given_TheClientWentAway_When_Waited_Then_ItCancelsAndReopensTheGate()
    {
        // Arrange
        Assert.True(_drainState.TryBeginDrain());
        _busyMonitor.Default = FakeBusyMonitor.Busy(0, 1);
        var waiter = CreateWaiter();
        using var cts = new CancellationTokenSource(30);

        // Act
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            waiter.WaitUntilIdleAsync(TimeSpan.FromSeconds(5), cts.Token));

        // Assert: the caller ends the drain on the cancellation (the handler's catch does)
        _drainState.EndDrain();
        Assert.False(_drainState.IsDraining);
    }

    internal sealed class FakeBusyMonitor : INodeBusyStateMonitor
    {
        private readonly Queue<NodeBusyState> _states = new();

        public NodeBusyState Default { get; set; } = Idle(0);

        public Queue<NodeBusyState> Queue(NodeBusyState state)
        {
            _states.Enqueue(state);
            return _states;
        }

        public NodeBusyState Snapshot() => _states.Count > 0 ? _states.Dequeue() : Default;

        internal static NodeBusyState Idle(int channelCount) =>
            new(channelCount, 0, 0, [], 0, -1);

        internal static NodeBusyState Busy(int negotiations, int htlcs) =>
            new(1, htlcs, negotiations,
                [new NodeBusyChannel(new string('a', 64), htlcs, negotiations > 0)], 500,
                htlcs > 0 ? 40 : -1);
    }
}