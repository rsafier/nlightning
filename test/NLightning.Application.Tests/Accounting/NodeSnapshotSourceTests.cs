using Microsoft.Extensions.DependencyInjection;
using NLightning.Tests.Utils.Channels;
using NLightning.Tests.Utils.Mocks;

namespace NLightning.Application.Tests.Accounting;

using Application.Accounting;
using Channels.Close;
using Channels.Fees;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.Transactions.Outputs;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Commitments;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Money;
using Domain.Onchain.Enums;
using Domain.Onchain.Interfaces;
using Domain.Onchain.Models;
using Domain.Persistence.Interfaces;
using Infrastructure.Bitcoin.Wallet.Interfaces;

/// <summary>
/// The balance snapshot (NL-602, IPC 42): channel buckets from memory with in-flight HTLCs from the snapshot, a
/// force-closed channel's off-chain balance replaced by its unspent outputs of ours, a channel that is no longer loaded
/// listed by its outputs, and the wallet's <c>walletbalance</c> numbers.
/// </summary>
public class NodeSnapshotSourceTests
{
    private static readonly ChannelId s_closedOnChain = new(Enumerable.Repeat((byte)0x71, 32).ToArray());
    private static readonly ChannelId s_notLoaded = new(Enumerable.Repeat((byte)0x72, 32).ToArray());
    private static readonly ChannelId s_closed = new(Enumerable.Repeat((byte)0x73, 32).ToArray());
    private static readonly TxId s_commitment = new(Enumerable.Repeat((byte)0x31, 32).ToArray());

    private readonly Mock<IChannelMemoryRepository> _channels = new();
    private readonly Mock<IOnchainResolutionDbRepository> _onchain = new();
    private readonly Mock<IUtxoMemoryRepository> _utxos = new();
    private readonly Mock<IBlockchainMonitor> _monitor = new();
    private readonly ManualTimeProvider _clock = new();
    private readonly List<ChannelModel> _loaded = [];
    private readonly List<OutputResolutionModel> _outputs = [];

    public NodeSnapshotSourceTests()
    {
        _channels.Setup(c => c.FindChannels(It.IsAny<Func<ChannelModel, bool>>()))
                 .Returns((Func<ChannelModel, bool> predicate) => _loaded.Where(predicate).ToList());
        _onchain.Setup(o => o.GetUnresolvedOutputsAsync()).ReturnsAsync(() => _outputs);
        _utxos.Setup(u => u.GetConfirmedBalance(It.IsAny<uint>())).Returns(LightningMoney.Zero);
        _utxos.Setup(u => u.GetUnconfirmedBalance(It.IsAny<uint>())).Returns(LightningMoney.Zero);
        _utxos.Setup(u => u.GetConfirmedBalance(800_000)).Returns(LightningMoney.Satoshis(2_000));
        _utxos.Setup(u => u.GetUnconfirmedBalance(800_000)).Returns(LightningMoney.Satoshis(300));
        _utxos.Setup(u => u.GetLockedBalance()).Returns(LightningMoney.Satoshis(100));
        _monitor.SetupGet(m => m.LastProcessedBlockHeight).Returns(800_000);
    }

    [Fact]
    public async Task Given_AnOpenChannelWithHtlcs_When_Snapshotted_Then_ItsBalancesAndInFlightAreReported()
    {
        // Arrange: 600k/400k sat settled, our 10k sat HTLC and their 5k sat HTLC in flight, one of ours already final
        var channel = Channel(FeeTestKit.ChannelId, ChannelState.Open, 1_000_000);
        channel.ShortChannelId = new ShortChannelId(800_000, 5, 1);
        channel.UpdateCommitments(FeeTestKit.Create(600_000, 400_000, 2_500,
                                                    htlcs:
                                                    [
                                                        FeeTestKit.Outgoing(0, 10_000),
                                                        FeeTestKit.Incoming(0, 5_000),
                                                        FeeTestKit.Outgoing(1, 1_000,
                                                                            HtlcState.RcvdRemoveAckRevocation)
                                                        with
                                                        {
                                                            Removal = HtlcRemoval.Fail(new byte[1])
                                                        }
                                                    ]));
        _loaded.Add(channel);

        // Act
        var snapshot = await CreateSource().TakeSnapshotAsync(TestContext.Current.CancellationToken);

        // Assert
        var bucket = Assert.Single(snapshot.Channels);
        Assert.Equal(FeeTestKit.ChannelId, bucket.ChannelId);
        Assert.Equal(new ShortChannelId(800_000, 5, 1), bucket.ShortChannelId);
        Assert.Equal(ChannelState.Open, bucket.State);
        Assert.Equal(channel.RemoteNodeId, bucket.Counterparty);
        Assert.Equal(1_000_000_000, bucket.CapacityMsat);
        Assert.Equal(600_000_000, bucket.LocalBalanceMsat);
        Assert.Equal(400_000_000, bucket.RemoteBalanceMsat);
        Assert.Equal(10_000_000, bucket.LocalInFlightMsat);
        Assert.Equal(5_000_000, bucket.RemoteInFlightMsat);
        Assert.True(bucket.IsLoaded);
        Assert.Equal(0, bucket.PendingSweepCount);
        Assert.Equal(_clock.GetUtcNow(), snapshot.TakenAt);
        Assert.Equal(800_000u, snapshot.BlockHeight);
    }

    [Fact]
    public async Task Given_ForceClosedChannels_When_Snapshotted_Then_OnlyOurUnspentOutputsArePendingOnChain()
    {
        // Arrange: one force-closed channel still loaded, one whose outputs are all that is left, one Closed
        _loaded.Add(Channel(s_closedOnChain, ChannelState.OnchainResolving, 500_000));
        _loaded.Add(Channel(s_closed, ChannelState.Closed, 100_000));
        _outputs.Add(Output(s_closedOnChain, 0, OutputDescriptorKind.DelayedToLocal, 50_000));
        _outputs.Add(Output(s_closedOnChain, 1, OutputDescriptorKind.LocalOfferedHtlc, 7_000,
                            OutputResolutionState.Waiting));
        _outputs.Add(Output(s_closedOnChain, 2, OutputDescriptorKind.PeerOutput, 300_000));
        _outputs.Add(Output(s_closedOnChain, 3, OutputDescriptorKind.OurAnchor, 330,
                            OutputResolutionState.Resolved));
        _outputs.Add(Output(s_closedOnChain, 4, OutputDescriptorKind.RemoteOfferedHtlc, 4_000));
        _outputs.Add(Output(s_notLoaded, 0, OutputDescriptorKind.RevokedToLocal, 20_000,
                            OutputResolutionState.Broadcast));

        // Act
        var snapshot = await CreateSource().TakeSnapshotAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(2, snapshot.Channels.Count);
        var loaded = Assert.Single(snapshot.Channels, c => c.ChannelId == s_closedOnChain);
        Assert.Equal(ChannelState.OnchainResolving, loaded.State);
        Assert.Equal(0, loaded.LocalBalanceMsat);
        Assert.Equal(0, loaded.RemoteBalanceMsat);
        Assert.Equal(500_000_000, loaded.CapacityMsat);
        Assert.Equal(50_000_000, loaded.PendingOnchainMsat);
        Assert.Equal(7_000_000, loaded.PendingHtlcOnchainMsat);
        Assert.Equal(4_000_000, loaded.PendingUncountedMsat);
        Assert.Equal(3, loaded.PendingSweepCount);
        Assert.True(loaded.IsLoaded);
        var gone = Assert.Single(snapshot.Channels, c => c.ChannelId == s_notLoaded);
        Assert.False(gone.IsLoaded);
        Assert.Equal(ChannelState.OnchainResolving, gone.State);
        Assert.Null(gone.Counterparty);
        // NL-618: a revoked commitment's output is booked only once claimed, so it is not pending in the books' sense
        Assert.Equal(0, gone.PendingOnchainMsat);
        Assert.Equal(20_000_000, gone.PendingUncountedMsat);
        Assert.Equal(50_000_000, snapshot.PendingOnchainMsat);
        Assert.Equal(7_000_000, snapshot.PendingHtlcOnchainMsat);
        Assert.Equal(24_000_000, snapshot.PendingUncountedMsat);
        Assert.Equal(4, snapshot.PendingSweepCount);
        Assert.Equal(0, snapshot.ChannelLocalMsat);
    }

    [Fact]
    public async Task Given_AWallet_When_Snapshotted_Then_ItsBucketAndTheTotalAddUp()
    {
        // Arrange
        var channel = Channel(FeeTestKit.ChannelId, ChannelState.Open, 1_000_000);
        channel.UpdateCommitments(FeeTestKit.Create(600_000, 400_000, 2_500));
        _loaded.Add(channel);
        _outputs.Add(Output(s_notLoaded, 0, OutputDescriptorKind.PaymentToRemote, 20_000));

        // Act
        var snapshot = await CreateSource().TakeSnapshotAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(2_000_000, snapshot.Wallet.ConfirmedMsat);
        Assert.Equal(300_000, snapshot.Wallet.UnconfirmedMsat);
        Assert.Equal(100_000, snapshot.Wallet.LockedMsat);
        Assert.Equal(600_000_000, snapshot.ChannelLocalMsat);
        Assert.Equal(600_000_000 + 20_000_000 + 2_000_000 + 300_000, snapshot.TotalMsat);
        Assert.Null(Assert.Single(snapshot.Channels, c => c.ChannelId == FeeTestKit.ChannelId).ShortChannelId);
    }

    [Fact]
    public async Task Given_NoScopeFactoryAndNoMonitor_When_Snapshotted_Then_OnlyMemoryIsReadAtHeightZero()
    {
        // Arrange
        _loaded.Add(Channel(s_closedOnChain, ChannelState.OnchainResolving, 500_000));
        _utxos.Setup(u => u.GetConfirmedBalance(0)).Returns(LightningMoney.Satoshis(5));
        var source = new NodeSnapshotSource(_channels.Object, _utxos.Object);

        // Act
        var snapshot = await source.TakeSnapshotAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(0u, snapshot.BlockHeight);
        Assert.Equal(5_000, snapshot.Wallet.ConfirmedMsat);
        Assert.Equal(0, Assert.Single(snapshot.Channels).PendingOnchainMsat);
        _onchain.Verify(o => o.GetUnresolvedOutputsAsync(), Times.Never);
    }

    [Theory]
    [InlineData(OutputDescriptorKind.DelayedToLocal, true, false)]
    [InlineData(OutputDescriptorKind.PaymentToRemote, true, false)]
    [InlineData(OutputDescriptorKind.RevokedHtlc, true, false)]
    [InlineData(OutputDescriptorKind.OurAnchor, true, false)]
    [InlineData(OutputDescriptorKind.RemoteOfferedHtlc, true, true)]
    [InlineData(OutputDescriptorKind.LocalReceivedHtlc, true, true)]
    [InlineData(OutputDescriptorKind.PeerOutput, false, false)]
    [InlineData(OutputDescriptorKind.PeerAnchor, false, false)]
    [InlineData(OutputDescriptorKind.Unknown, false, false)]
    public void Given_AnOutputKind_When_Classified_Then_OursAndHtlcFollowTheDescriptor(OutputDescriptorKind kind,
        bool ours, bool htlc)
    {
        // Act & Assert
        Assert.Equal(ours, NodeSnapshotSource.IsOurs(kind));
        Assert.Equal(htlc, NodeSnapshotSource.IsHtlc(kind));
    }

    [Fact]
    public async Task Given_HtlcsWhosePreimageIsKnown_When_Snapshotted_Then_TheyAreReportedApart()
    {
        // Arrange: our 10k sat HTLC the peer fulfilled and our 3k sat one still open; their 5k sat HTLC we accepted as
        // the final node, their 2k sat one we fulfilled and their 4k sat one we know nothing about (NL-602 A3-T6)
        var preimage = new Secret(Enumerable.Repeat((byte)0x5a, 32).ToArray());
        var channel = Channel(FeeTestKit.ChannelId, ChannelState.Open, 1_000_000);
        channel.UpdateCommitments(FeeTestKit.Create(600_000, 400_000, 2_500,
                                                    htlcs:
                                                    [
                                                        FeeTestKit.Outgoing(0, 10_000) with { KnownPreimage = preimage },
                                                        FeeTestKit.Outgoing(1, 3_000),
                                                        FeeTestKit.Incoming(0, 5_000) with { KnownPreimage = preimage },
                                                        FeeTestKit.Incoming(1, 2_000, HtlcState.SentRemoveHtlc) with
                                                        {
                                                            Removal = HtlcRemoval.Fulfill(preimage)
                                                        },
                                                        FeeTestKit.Incoming(2, 4_000)
                                                    ]));
        _loaded.Add(channel);

        // Act
        var snapshot = await CreateSource().TakeSnapshotAsync(TestContext.Current.CancellationToken);

        // Assert
        var bucket = Assert.Single(snapshot.Channels);
        Assert.Equal(13_000_000, bucket.LocalInFlightMsat);
        Assert.Equal(10_000_000, bucket.LocalInFlightFulfilledMsat);
        Assert.Equal(11_000_000, bucket.RemoteInFlightMsat);
        Assert.Equal(7_000_000, bucket.RemoteInFlightPreimageMsat);
    }

    private NodeSnapshotSource CreateSource()
    {
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.SetupGet(u => u.OnchainResolutionDbRepository).Returns(_onchain.Object);
        var provider = new FakeServiceProvider();
        provider.AddService(typeof(IUnitOfWork), unitOfWork.Object);
        return new NodeSnapshotSource(_channels.Object, _utxos.Object,
                                      provider.GetRequiredService<IServiceScopeFactory>(), _monitor.Object, _clock);
    }

    private static ChannelModel Channel(ChannelId channelId, ChannelState state, long capacitySat)
    {
        var key = FeeTestKit.Point(0x01);
        var channelParams = TestChannelParams.Create(LightningMoney.Satoshis(10_000), LightningMoney.Satoshis(2_500),
                                                     LightningMoney.Satoshis(1), LightningMoney.Satoshis(546), 30,
                                                     LightningMoney.Satoshis(100_000), 3, false,
                                                     LightningMoney.Satoshis(546), 144, FeatureSupport.No);
        var keySet = new ChannelKeySetModel(0, key, key, key, key, key, key);
        var funding = new FundingOutputInfo(LightningMoney.Satoshis(capacitySat), key, FeeTestKit.Point(0x02));
        return new ChannelModel(channelParams, channelId, null, funding, true, null, null,
                                LightningMoney.Satoshis(capacitySat), keySet, 0, 0, LightningMoney.Zero, null, 0,
                                FeeTestKit.Point(0x09), 0, state, ChannelVersion.V1);
    }

    private static OutputResolutionModel Output(ChannelId channelId, uint vout, OutputDescriptorKind kind,
                                                ulong amountSat,
                                                OutputResolutionState state = OutputResolutionState.Pending) =>
        new()
        {
            TransactionId = s_commitment,
            OutputIndex = vout,
            ChannelId = channelId,
            Descriptor = kind,
            DescriptorData = new OutputDescriptorData(amountSat, new byte[22], null, 0, false, null, null).Encode(),
            State = state
        };
}