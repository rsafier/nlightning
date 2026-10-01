using Microsoft.Extensions.Logging.Abstractions;

namespace NLightning.Application.Tests.Node.Services;

using Application.InteractiveTx.Interfaces;
using Application.InteractiveTx.Models;
using Application.Node.Services;
using Domain.Bitcoin.Transactions.Enums;
using Domain.Channels.Commitments;
using Domain.Channels.Enums;
using Domain.Channels.Models;
using Domain.Channels.Quiescence;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Infrastructure.Bitcoin.Wallet.Interfaces;
using Infrastructure.Repositories.Memory;

/// <summary>
/// The node's busy state for <c>shutdown --wait</c> (NL-592): HTLCs in flight per channel (the count the first pass
/// refused on), mid-flight negotiations (an attempt in progress, an abort awaiting its echo, an RBF request awaiting
/// its ack, any quiescence, an unsigned open), the nearest <c>cltv_expiry</c> and the blocks until our deadline.
/// </summary>
public sealed class NodeBusyStateMonitorTests
{
    private readonly ChannelMemoryRepository _channels = new(NullLogger<ChannelMemoryRepository>.Instance);
    private readonly Mock<IBlockchainMonitor> _blockchainMonitorMock = new();
    private readonly Mock<IInteractiveTxDriver> _driverMock = new();
    private readonly Mock<Domain.Channels.Quiescence.IQuiescenceService> _quiescenceMock = new();

    private NodeBusyStateMonitor CreateMonitor() =>
        new(_channels, NullLogger<NodeBusyStateMonitor>.Instance, _driverMock.Object, _quiescenceMock.Object,
            blockchainMonitor: _blockchainMonitorMock.Object);

    [Fact]
    public void Given_NoChannels_When_Snapshotted_Then_NotBusy()
    {
        // Act
        var state = CreateMonitor().Snapshot();

        // Assert
        Assert.False(state.IsBusy);
        Assert.Equal(0, state.ChannelCount);
        Assert.Equal(0, state.HtlcsInFlight);
        Assert.Equal(-1, state.BlocksUntilDeadline);
    }

    [Fact]
    public void Given_HtlcsInFlight_When_Snapshotted_Then_BusyWithPerChannelCountsAndNearestExpiry()
    {
        // Arrange: two pending HTLCs each way on channel 1 (one settled, not in flight), one pending on channel 2
        _channels.AddChannel(Channel(1, ChannelState.Open, Outgoing(0), SettledOutgoing(1),
                              Incoming(0)));
        _channels.AddChannel(Channel(2, ChannelState.Open, Incoming(5, cltv: 480)));

        // Act
        var state = CreateMonitor().Snapshot();

        // Assert
        Assert.True(state.IsBusy);
        Assert.Equal(2, state.ChannelCount);
        Assert.Equal(3, state.HtlcsInFlight);
        Assert.Equal(2, state.Channels.Count);
        Assert.Equal(2, state.Channels.Single(c => c.ChannelId == Id(1).ToString()).HtlcsInFlight);
        Assert.Equal(1, state.Channels.Single(c => c.ChannelId == Id(2).ToString()).HtlcsInFlight);
        Assert.Equal(480u, state.NearestCltvExpiry);
    }

    [Fact]
    public void Given_AHeight_When_Snapshotted_Then_BlocksUntilTheEarliestDeadline()
    {
        // Arrange: the incoming HTLC's fail-back deadline is cltv 500 - FailBackBlocks (34) = 466; at height 460 six
        // blocks remain until we must act on it
        _channels.AddChannel(Channel(1, ChannelState.Open, Outgoing(0), Incoming(0)));
        _blockchainMonitorMock.SetupGet(m => m.LastProcessedBlockHeight).Returns(460u);

        // Act
        var state = CreateMonitor().Snapshot();

        // Assert
        Assert.Equal(6, state.BlocksUntilDeadline);
        Assert.Equal(500u, state.NearestCltvExpiry);
    }

    [Fact]
    public void Given_ClosedAndStaleChannels_When_Snapshotted_Then_Ignored()
    {
        // Arrange
        _channels.AddChannel(Channel(1, ChannelState.Closed, Incoming(0)));
        _channels.AddChannel(Channel(2, ChannelState.Stale, Incoming(0)));

        // Act
        var state = CreateMonitor().Snapshot();

        // Assert
        Assert.False(state.IsBusy);
        Assert.Equal(0, state.ChannelCount);
    }

    [Fact]
    public void Given_AnUnsignedOpen_When_Snapshotted_Then_Negotiating()
    {
        // Arrange
        _channels.AddChannel(Channel(1, ChannelState.V1FundingCreated));

        // Act
        var state = CreateMonitor().Snapshot();

        // Assert
        Assert.True(state.IsBusy);
        Assert.Equal(1, state.NegotiationCount);
        Assert.True(state.Channels.Single().Negotiating);
    }

    [Fact]
    public void Given_AQuiescentChannel_When_Snapshotted_Then_Negotiating()
    {
        // Arrange: quiescence holds the channel for a splice/RBF/probe until it ends
        _channels.AddChannel(Channel(1, ChannelState.Open));
        _quiescenceMock.Setup(q => q.GetState(Id(1)))
                       .Returns(MakeQuiescent());

        // Act
        var state = CreateMonitor().Snapshot();

        // Assert
        Assert.Equal(1, state.NegotiationCount);
    }

    [Fact]
    public void Given_AnAttemptInProgressOnTheDriver_When_Snapshotted_Then_Negotiating()
    {
        // Arrange
        _channels.AddChannel(Channel(1, ChannelState.Open));
        _driverMock.Setup(d => d.IsNegotiating(Id(1))).Returns(true);

        // Act
        var state = CreateMonitor().Snapshot();

        // Assert
        Assert.Equal(1, state.NegotiationCount);
    }

    [Fact]
    public void Given_AnAbortAwaitingItsEcho_When_Snapshotted_Then_Negotiating()
    {
        // Arrange: our tx_abort is not answered yet; the negotiation can still come back
        _channels.AddChannel(Channel(1, ChannelState.Open));
        _driverMock.Setup(d => d.IsNegotiating(Id(1))).Returns(false);
        _driverMock.Setup(d => d.GetInfo(Id(1)))
                   .Returns(new InteractiveTxNegotiationInfo(Id(1), Guid.NewGuid(), null, true, true, false, [], [],
                                                             []));

        // Act
        var state = CreateMonitor().Snapshot();

        // Assert
        Assert.Equal(1, state.NegotiationCount);
    }

    [Fact]
    public void Given_AnIdleOpenChannel_When_Snapshotted_Then_NotBusy()
    {
        // Arrange: an open channel without HTLCs or a negotiation, signed and confirmed
        _channels.AddChannel(Channel(1, ChannelState.Open));
        _quiescenceMock.Setup(q => q.GetState(Id(1)))
                       .Returns(QuiescenceState.None);
        _driverMock.Setup(d => d.IsNegotiating(Id(1))).Returns(false);

        // Act
        var state = CreateMonitor().Snapshot();

        // Assert
        Assert.False(state.IsBusy);
    }

    [Fact]
    public void Given_StreamsOfHtlcsInFlight_When_Snapshotted_Then_TheExpiryCoversIncomingAndOutgoing()
    {
        // Arrange: the nearest expiry is the incoming one's 480 even though an outgoing 490 exists (the outgoing
        // deadline is later still: expiry + grace)
        _channels.AddChannel(Channel(1, ChannelState.Open, Outgoing(9, cltv: 490),
                              Incoming(0, cltv: 480)));

        // Act
        var state = CreateMonitor().Snapshot();

        // Assert
        Assert.Equal(480u, state.NearestCltvExpiry);
    }

    private static ChannelId Id(byte fill) => new(Enumerable.Repeat(fill, 32).ToArray());

    private static HtlcRecord Outgoing(ulong id, uint cltv = 500) =>
        Htlc(id, HtlcDirection.Outgoing, HtlcState.SentAddAckRevocation, cltv);

    private static HtlcRecord SettledOutgoing(ulong id, uint cltv = 500) =>
        Htlc(id, HtlcDirection.Outgoing, HtlcState.RcvdRemoveAckRevocation, cltv,
             HtlcRemoval.Fulfill(new Secret(new byte[32])));

    private static HtlcRecord Incoming(ulong id, uint cltv = 500) =>
        Htlc(id, HtlcDirection.Incoming, HtlcState.RcvdAddAckRevocation, cltv);

    private static HtlcRecord Htlc(ulong id, HtlcDirection direction, HtlcState state, uint cltv,
                                   HtlcRemoval? removal = null) =>
        new(direction, id, 10_000, new Hash(Enumerable.Repeat((byte)(id + 1), 32).ToArray()), cltv, state, removal);

    private static ChannelModel Channel(byte fill, Domain.Channels.Enums.ChannelState state,
                                        params HtlcRecord[] htlcs)
    {
        var peerId = CreatePubKey(fill);
        var channel = new ChannelModel(new ChannelParams(), Id(fill), null, null, true, null, null,
                                       LightningMoney.Satoshis(100_000),
                                       new ChannelKeySetModel(0, peerId, peerId, peerId, peerId, peerId, peerId), 0,
                                       0, LightningMoney.Zero, null, 0, peerId, 0, state, ChannelVersion.V1);
        if (htlcs.Length > 0)
            channel.UpdateCommitments(Snapshot(Id(fill), htlcs));

        return channel;
    }

    private static ChannelCommitments Snapshot(ChannelId channelId, IReadOnlyList<HtlcRecord> htlcs)
    {
        var party = new CommitmentParty(354, 10_000, 1_000, 30, 1_000_000_000);
        var @params = new CommitmentParams(true, 1_000_000, false, party, party);
        const ulong localMsat = 700_000_000;
        const ulong remoteMsat = 300_000_000;
        var nextOutgoing = htlcs.Where(h => h.Direction == HtlcDirection.Outgoing).Select(h => h.Id + 1)
                                .DefaultIfEmpty().Max();
        var nextIncoming = htlcs.Where(h => h.Direction == HtlcDirection.Incoming).Select(h => h.Id + 1)
                                .DefaultIfEmpty().Max();
        return ChannelCommitments.Restore(channelId, @params, localMsat, remoteMsat, htlcs,
                                          [new FeeUpdate(0, 1_000, HtlcState.SentAddAckRevocation)], nextOutgoing,
                                          nextIncoming,
                                          new LocalCommit(1, new CommitmentSpec(CommitmentSide.Local, 1_000, localMsat,
                                                                                remoteMsat, []), null),
                                          new RemoteCommit(1, new CommitmentSpec(CommitmentSide.Remote, 1_000,
                                                                                 localMsat, remoteMsat, []),
                                                           CreatePubKey(9)),
                                          null, CreatePubKey(10));
    }

    private static CompactPubKey CreatePubKey(byte fill)
    {
        var bytes = Enumerable.Repeat(fill, 33).ToArray();
        bytes[0] = 0x02;
        return new CompactPubKey(bytes);
    }

    private static Domain.Channels.Quiescence.QuiescenceState MakeQuiescent() =>
        new()
        {
            SentStfuInitiator = false,
            ReceivedStfuInitiator = true,
            Initiator = QuiescenceInitiator.Remote
        };
}