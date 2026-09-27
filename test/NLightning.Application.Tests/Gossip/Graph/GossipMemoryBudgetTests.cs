using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace NLightning.Application.Tests.Gossip.Graph;

using Application.Gossip.Graph;
using Application.Gossip.Graph.Interfaces;
using Application.Gossip.Metrics;
using Domain.Channels.ValueObjects;
using Domain.Node.Interfaces;
using Metrics;

/// <summary>
/// NL-373 (BOLT 7 plan G5-T1): <c>Gossip:MaxMemoryMb</c> against the process's resident set, with the degrade policy:
/// over the budget no new channel or node is accepted from gossip while known ones keep updating; accepted again
/// below <c>MemoryResumePercent</c> of the budget.
/// </summary>
public class GossipMemoryBudgetTests : IDisposable
{
    private const long MiB = 1L << 20;

    private static readonly ShortChannelId s_scid = new(110, 1, 0);
    private static readonly TestGossipKey s_alice = new(1);
    private static readonly TestGossipKey s_bob = new(2);
    private static readonly TestGossipKey s_carol = new(3);
    private static readonly TestGossipKey s_aliceFunding = new(11);
    private static readonly TestGossipKey s_bobFunding = new(12);
    private static readonly TestGossipKey s_carolFunding = new(13);
    private static readonly uint s_now = (uint)GraphTestKit.DefaultNow.ToUnixTimeSeconds();

    private readonly GossipMetrics _metrics = new();
    private readonly GossipMetricsRecorder _recorder;
    private readonly FakeMemoryReader _reader = new();
    private readonly SettableTimeProvider _clock = new(GraphTestKit.DefaultNow);

    public GossipMemoryBudgetTests()
    {
        _recorder = new GossipMetricsRecorder(_metrics);
    }

    public void Dispose()
    {
        _recorder.Dispose();
        _metrics.Dispose();
    }

    [Fact]
    public void Given_DefaultOptions_When_Read_Then_TheBudgetIs1GiBWithResumeAt90Percent()
    {
        // Arrange
        var options = new GossipGraphOptions();

        // Act
        var budget = CreateBudget(options);

        // Assert
        Assert.Equal(1_024, options.MaxMemoryMb);
        Assert.Equal(1_024 * MiB, budget.BudgetBytes);
        Assert.Equal(1_024 * MiB * 90 / 100, budget.ResumeBytes);
        Assert.True(budget.IsEnabled);
    }

    [Fact]
    public void Given_ProcessBelowTheBudget_When_Checked_Then_NewEntriesAreAdmitted()
    {
        // Arrange
        var budget = CreateBudget();
        _reader.WorkingSet = 1_000 * MiB;

        // Act
        var refusal = budget.RefuseNew("channels");

        // Assert
        Assert.Null(refusal);
        Assert.False(budget.IsOverBudget);
        Assert.Equal(0, budget.Crossings);
    }

    [Fact]
    public void Given_ProcessGoesAboveTheBudget_When_CheckedRepeatedly_Then_RefusedAndTheCrossingIsLoggedAndCountedOnce()
    {
        // Arrange
        var logger = new Mock<ILogger<GossipMemoryBudget>>();
        logger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
        var budget = CreateBudget(logger: logger.Object);
        _reader.WorkingSet = 1_025 * MiB;

        // Act
        var first = budget.RefuseNew("channels");
        for (var i = 0; i < 5; i++)
        {
            _clock.Now += TimeSpan.FromSeconds(2);
            _reader.WorkingSet += MiB;
            budget.RefuseNew("nodes");
        }

        // Assert
        Assert.NotNull(first);
        Assert.Equal(GossipIngressOutcome.Ignored, first.Outcome);
        Assert.Equal(GossipMetricReasons.MemoryBudget, first.LimitReason);
        Assert.True(budget.IsOverBudget);
        Assert.Equal(1, budget.Crossings);
        Assert.Equal(6, budget.RefusedCount);
        Assert.Equal(1, _recorder.Sum("nlightning.gossip.memory.budget.exceeded"));
        logger.Verify(l => l.Log(LogLevel.Warning, It.IsAny<EventId>(), It.IsAny<It.IsAnyType>(),
                                 It.IsAny<Exception?>(), It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
                      Times.Once);
    }

    [Fact]
    public void Given_ExactlyAtTheBudget_When_Checked_Then_NotOver()
    {
        // Arrange ("above the budget" is strictly above)
        var budget = CreateBudget();
        _reader.WorkingSet = 1_024 * MiB;

        // Act & Assert
        Assert.False(budget.IsOverBudget);
    }

    [Fact]
    public void Given_OverTheBudget_When_MemoryFallsBetweenResumeAndBudget_Then_StillRefusedUntilBelowResume()
    {
        // Arrange (hysteresis: resume below 90 % = 921.6 MiB)
        var budget = CreateBudget();
        _reader.WorkingSet = 1_100 * MiB;
        Assert.True(budget.IsOverBudget);

        // Act
        var readings = new List<bool>();
        foreach (var mb in new long[] { 1_000, 922, 921, 1_000, 1_024, 1_025 })
        {
            _clock.Now += TimeSpan.FromSeconds(1);
            _reader.WorkingSet = mb * MiB;
            readings.Add(budget.IsOverBudget);
        }

        // Assert: 1000 and 922 keep refusing, 921 resumes, 1000 and 1024 stay admitted, 1025 crosses again
        Assert.Equal([true, true, false, false, false, true], readings);
        Assert.Equal(2, budget.Crossings);
        Assert.Equal(2, _recorder.Sum("nlightning.gossip.memory.budget.exceeded"));
    }

    [Fact]
    public void Given_ASampleInterval_When_CheckedWithinIt_Then_TheProcessIsReadOnceAndTheLastReadingDecides()
    {
        // Arrange
        var budget = CreateBudget(new GossipGraphOptions { MemorySampleInterval = TimeSpan.FromSeconds(5) });
        _reader.WorkingSet = 100 * MiB;
        Assert.False(budget.IsOverBudget);

        // Act
        _reader.WorkingSet = 2_000 * MiB;
        _clock.Now += TimeSpan.FromSeconds(4);
        var withinInterval = budget.IsOverBudget;
        _clock.Now += TimeSpan.FromSeconds(1);
        var afterInterval = budget.IsOverBudget;

        // Assert
        Assert.False(withinInterval);
        Assert.True(afterInterval);
        Assert.Equal(2, _reader.Reads);
    }

    [Fact]
    public void Given_MaxMemoryMbZero_When_TheProcessIsHuge_Then_TheBudgetIsOffButUsageIsStillReported()
    {
        // Arrange
        var budget = CreateBudget(new GossipGraphOptions { MaxMemoryMb = 0 });
        _reader.WorkingSet = 100_000 * MiB;
        _reader.ManagedHeap = 50 * MiB;

        // Act
        var refusal = budget.RefuseNew("channels");
        var state = budget.GetState();

        // Assert
        Assert.Null(refusal);
        Assert.False(budget.IsEnabled);
        Assert.False(state.IsOverBudget);
        Assert.Equal(0, state.BudgetBytes);
        Assert.Equal(100_000 * MiB, state.WorkingSetBytes);
        Assert.Equal(50 * MiB, state.ManagedHeapBytes);
    }

    [Fact]
    public void Given_TheReaderFails_When_Checked_Then_TheLastDecisionIsKept()
    {
        // Arrange
        var budget = CreateBudget();
        _reader.WorkingSet = 2_000 * MiB;
        Assert.True(budget.IsOverBudget);

        // Act
        _reader.Throw = true;
        _clock.Now += TimeSpan.FromSeconds(1);
        var over = budget.IsOverBudget;

        // Assert
        Assert.True(over);
        Assert.Equal(2_000 * MiB, budget.GetState().WorkingSetBytes);
    }

    [Fact]
    public void Given_OverTheBudget_When_TheReaderReportsZero_Then_TheLastDecisionIsKept()
    {
        // Arrange
        var budget = CreateBudget();
        _reader.WorkingSet = 2_000 * MiB;
        Assert.True(budget.IsOverBudget);

        // Act
        _reader.WorkingSet = 0;
        _clock.Now += TimeSpan.FromSeconds(1);
        var over = budget.IsOverBudget;

        // Assert
        Assert.True(over);
        Assert.Equal(2_000 * MiB, budget.GetState().WorkingSetBytes);
        Assert.Equal(1, budget.Crossings);
    }

    [Fact]
    public void Given_NoAdmissionCheck_When_TheGaugeIsObservedAfterTheInterval_Then_ItReadsTheProcessAgain()
    {
        // Arrange
        var budget = CreateBudget();
        _reader.WorkingSet = 400 * MiB;
        Assert.False(budget.IsOverBudget);

        // Act
        _reader.WorkingSet = 700 * MiB;
        var withinInterval = _recorder.Observe("nlightning.gossip.memory.working_set");
        _clock.Now += TimeSpan.FromSeconds(1);
        var afterInterval = _recorder.Observe("nlightning.gossip.memory.working_set");

        // Assert
        Assert.Equal(400 * MiB, withinInterval);
        Assert.Equal(700 * MiB, afterInterval);
        Assert.Equal(2, _reader.Reads);
    }

    [Fact]
    public void Given_TheRealProcess_When_Read_Then_TheWorkingSetAndGcCommittedBytesArePositive()
    {
        // Act
        var usage = ProcessMemoryReader.Instance.Read();

        // Assert
        Assert.True(usage.WorkingSetBytes > 0);
        Assert.True(usage.ManagedHeapBytes > 0);
    }

    [Fact]
    public void Given_AReading_When_StateAndGaugeAreRead_Then_TheyReportIt()
    {
        // Arrange
        var budget = CreateBudget();
        _reader.WorkingSet = 1_500 * MiB;
        _reader.ManagedHeap = 600 * MiB;
        budget.RefuseNew("channels");

        // Act
        var state = budget.GetState();
        var gauge = _recorder.Observe("nlightning.gossip.memory.working_set");

        // Assert
        Assert.Equal(new GossipMemoryBudgetState(1_024 * MiB, 1_024 * MiB * 90 / 100, 1_500 * MiB, 600 * MiB, true, 1,
                                                 1), state);
        Assert.Equal(1_500 * MiB, gauge);
    }

    [Theory]
    [InlineData(-1, 90, 1, "MaxMemoryMb")]
    [InlineData(1_024, 0, 1, "MemoryResumePercent")]
    [InlineData(1_024, 101, 1, "MemoryResumePercent")]
    [InlineData(1_024, 90, -1, "MemorySampleInterval")]
    public void Given_InvalidBudgetOptions_When_Validated_Then_TheyAreReported(int maxMemoryMb, int resumePercent,
                                                                               int sampleSeconds, string name)
    {
        // Arrange
        var options = new GossipGraphOptions
        {
            MaxMemoryMb = maxMemoryMb,
            MemoryResumePercent = resumePercent,
            MemorySampleInterval = TimeSpan.FromSeconds(sampleSeconds)
        };

        // Act
        var errors = options.GetValidationErrors();

        // Assert
        Assert.Contains(errors, e => e.StartsWith(name, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Given_OverTheBudget_When_ANewChannelIsAnnounced_Then_ItIsRefusedWithoutAChainLookupAndCounted()
    {
        // Arrange
        var (kit, budget) = await CreateKitWithChannelAsync();
        var peer = GraphTestKit.CreatePeer();
        var other = new ShortChannelId(111, 1, 0);
        _reader.WorkingSet = 1_100 * MiB;
        Assert.True(budget.IsOverBudget);

        // Act
        var refused = await ProcessAsync(kit, peer,
                                         GraphTestKit.SignedChannelAnnouncement(other, s_bob, s_carol, s_bobFunding,
                                                                                s_carolFunding));

        // Assert
        Assert.Equal(GossipIngressOutcome.Ignored, refused.Outcome);
        Assert.Equal(GossipMetricReasons.MemoryBudget, refused.LimitReason);
        Assert.False(kit.Store.TryGetChannel(other, out _));
        kit.FundingLookup.Verify(l => l.VerifyAsync(other, It.IsAny<Domain.Crypto.ValueObjects.CompactPubKey>(),
                                                    It.IsAny<Domain.Crypto.ValueObjects.CompactPubKey>(),
                                                    It.IsAny<Domain.Money.LightningMoney?>(),
                                                    It.IsAny<CancellationToken>()), Times.Never);
        Assert.Equal(1, _recorder.Sum("nlightning.gossip.messages.rejected",
                                      (GossipMetrics.TypeTag, "channel_announcement"),
                                      (GossipMetrics.ReasonTag, GossipMetricReasons.MemoryBudget)));
    }

    [Fact]
    public async Task Given_OverTheBudget_When_KnownChannelsAndNodesUpdate_Then_TheyAreStillAppliedButNewNodesAreNot()
    {
        // Arrange
        var (kit, budget) = await CreateKitWithChannelAsync();
        var peer = GraphTestKit.CreatePeer();
        Assert.Equal(GossipIngressOutcome.Accepted,
                     (await ProcessAsync(kit, peer, GraphTestKit.SignedNodeAnnouncement(s_alice, s_now - 1_000)))
                    .Outcome);
        _clock.Now += TimeSpan.FromSeconds(1);
        _reader.WorkingSet = 1_100 * MiB;
        Assert.True(budget.IsOverBudget);
        var direction = GraphTestKit.DirectionOf(s_alice, s_bob);

        // Act
        var update = await ProcessAsync(kit, peer, GraphTestKit.SignedChannelUpdate(s_scid, s_alice, direction,
                                                                                     s_now - 500, feeBaseMsat: 2_000));
        kit.Clock.Now += TimeSpan.FromMinutes(10);
        var knownNode = await ProcessAsync(kit, peer, GraphTestKit.SignedNodeAnnouncement(s_alice, s_now, "again"));
        var newNode = await ProcessAsync(kit, peer, GraphTestKit.SignedNodeAnnouncement(s_bob, s_now));

        // Assert
        Assert.Equal(GossipIngressOutcome.Accepted, update.Outcome);
        Assert.True(kit.Store.TryGetChannel(s_scid, out var channel));
        Assert.Equal(s_now - 500, channel.GetPolicy(direction)!.Timestamp);
        Assert.Equal(GossipIngressOutcome.Accepted, knownNode.Outcome);
        Assert.Equal(GossipMetricReasons.MemoryBudget, newNode.LimitReason);
        Assert.False(kit.Store.TryGetNode(s_bob.PubKey, out _));
    }

    [Fact]
    public async Task Given_OverTheBudget_When_MemoryFallsBelowResume_Then_NewChannelsAreAcceptedAgain()
    {
        // Arrange
        var (kit, budget) = await CreateKitWithChannelAsync();
        var peer = GraphTestKit.CreatePeer();
        var other = new ShortChannelId(111, 1, 0);
        var announcement = GraphTestKit.SignedChannelAnnouncement(other, s_bob, s_carol, s_bobFunding,
                                                                  s_carolFunding);
        _reader.WorkingSet = 1_100 * MiB;
        Assert.Equal(GossipMetricReasons.MemoryBudget,
                     (await kit.Ingress.ProcessAsync(peer.Object, announcement, 1,
                                                     TestContext.Current.CancellationToken)).LimitReason);

        // Act
        _clock.Now += TimeSpan.FromSeconds(1);
        _reader.WorkingSet = 900 * MiB;
        var accepted = await kit.AnnounceAsync(peer.Object, announcement, 1,
                                               cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(GossipIngressOutcome.Accepted, accepted.Outcome);
        Assert.True(kit.Store.TryGetChannel(other, out _));
        Assert.False(budget.IsOverBudget);
    }

    [Fact]
    public async Task Given_APendingAnnouncement_When_ItsFirstUpdateComesOverTheBudget_Then_ItIsNotPromoted()
    {
        // Arrange (NL-406: a new channel enters the graph at its first update, so the budget holds there too)
        var (kit, budget) = await CreateKitWithChannelAsync();
        var peer = GraphTestKit.CreatePeer();
        var other = new ShortChannelId(111, 1, 0);
        var kept = await kit.Ingress.ProcessAsync(peer.Object,
                                                  GraphTestKit.SignedChannelAnnouncement(
                                                      other, s_bob, s_carol, s_bobFunding, s_carolFunding), 0,
                                                  TestContext.Current.CancellationToken);
        Assert.Equal(GossipIngressOutcome.Pending, kept.Outcome);
        _clock.Now += TimeSpan.FromSeconds(1);
        _reader.WorkingSet = 1_100 * MiB;
        Assert.True(budget.IsOverBudget);

        // Act
        var refused = await kit.Ingress.ProcessAsync(peer.Object,
                                                     GraphTestKit.SignedChannelUpdate(
                                                         other, s_bob, GraphTestKit.DirectionOf(s_bob, s_carol),
                                                         s_now - 100), 0,
                                                     TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(GossipMetricReasons.MemoryBudget, refused.LimitReason);
        Assert.False(kit.Store.TryGetChannel(other, out _));
        Assert.True(kit.Ingress.IsPendingAnnouncement(other));
    }

    private GossipMemoryBudget CreateBudget(GossipGraphOptions? options = null,
                                            ILogger<GossipMemoryBudget>? logger = null) =>
        new(Microsoft.Extensions.Options.Options.Create(options ?? new GossipGraphOptions()),
            logger ?? NullLogger<GossipMemoryBudget>.Instance, _reader, _clock, _metrics);

    private async Task<(GraphTestKit Kit, GossipMemoryBudget Budget)> CreateKitWithChannelAsync()
    {
        var budget = CreateBudget();
        _reader.WorkingSet = 100 * MiB;
        var kit = new GraphTestKit(metrics: _metrics, memoryBudget: budget);
        kit.FundingFound();
        var result = await kit.AnnounceAsync(GraphTestKit.CreatePeer(0x70).Object,
                                             GraphTestKit.SignedChannelAnnouncement(s_scid, s_alice, s_bob,
                                                                                    s_aliceFunding, s_bobFunding),
                                             updateTimestamp: s_now - 2_000,
                                             cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(GossipIngressOutcome.Accepted, result.Outcome);
        _clock.Now += TimeSpan.FromSeconds(1);
        return (kit, budget);
    }

    private static Task<GossipIngressResult> ProcessAsync(GraphTestKit kit, Mock<IPeerService> peer,
                                                          Domain.Protocol.Interfaces.IMessage message) =>
        kit.Ingress.ProcessAsync(peer.Object, message, 0, TestContext.Current.CancellationToken);

    private sealed class FakeMemoryReader : IProcessMemoryReader
    {
        public long WorkingSet { get; set; }
        public long ManagedHeap { get; set; }
        public bool Throw { get; set; }
        public int Reads { get; private set; }

        public ProcessMemoryUsage Read()
        {
            Reads++;
            if (Throw)
                throw new InvalidOperationException("no reading");

            return new ProcessMemoryUsage(WorkingSet, ManagedHeap);
        }
    }
}