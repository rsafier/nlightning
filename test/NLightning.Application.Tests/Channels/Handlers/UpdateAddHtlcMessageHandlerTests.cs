using Microsoft.Extensions.Logging.Abstractions;

namespace NLightning.Application.Tests.Channels.Handlers;

using Application.Channels.Handlers;
using Domain.Channels.Enums;
using Domain.Exceptions;
using Domain.Money;
using Domain.Node.Options;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using static NormalOperationTestContext;

public class UpdateAddHtlcMessageHandlerTests
{
    private readonly NormalOperationTestContext _context = new();

    [Fact]
    public async Task Given_OpenChannel_When_AddReceived_Then_PersistedBeforeTheSnapshotChangesAndNothingIsSent()
    {
        // Arrange
        var handler = CreateHandler();
        var before = _context.State;

        // Act
        var replies = await handler.HandleAsync(CreateAdd(0, 50_000_000), ChannelState.Open, new FeatureOptions(),
                                                PeerNodeId);

        // Assert
        Assert.Empty(replies);
        Assert.Equal(["apply", "save"], _context.Calls);
        var (next, transition, _) = Assert.Single(_context.Applied);
        Assert.Same(next, _context.State);
        Assert.NotSame(before, _context.State);
        var htlc = Assert.Single(transition.UpsertedHtlcs);
        Assert.Equal(HtlcState.RcvdAddHtlc, htlc.State);
        Assert.Equal(50_000_000UL, htlc.AmountMsat);
        Assert.Equal(1UL, _context.State.RemoteNextHtlcId);
        _context.ChannelMemoryRepository.Verify(r => r.UpdateChannel(_context.Channel), Times.Once);
    }

    [Theory]
    [InlineData(ChannelState.ReadyForThem)]
    [InlineData(ChannelState.ReadyForUs)]
    [InlineData(ChannelState.V1FundingSigned)]
    public async Task Given_ChannelNotOpen_When_Add_Then_WarningAndCloseWithoutPersisting(ChannelState state)
    {
        // Arrange - B2-NO-02: HTLCs only after both channel_ready
        var context = new NormalOperationTestContext(state: state);
        var handler = new UpdateAddHtlcMessageHandler(NullLogger<UpdateAddHtlcMessageHandler>.Instance,
                                                      context.CreateTransitions());

        // Act
        var exception = await Assert.ThrowsAsync<ChannelWarningException>(
                            () => handler.HandleAsync(CreateAdd(0, 50_000_000), state, new FeatureOptions(),
                                                      PeerNodeId));

        // Assert
        Assert.True(exception.CloseConnection);
        Assert.Equal(TestChannelId, exception.ChannelId);
        Assert.Empty(context.Calls);
    }

    [Fact]
    public async Task Given_FailedChannel_When_Add_Then_ChannelErrorIsSentAgain()
    {
        // Arrange - N6-T3: a failed channel refuses every update
        var context = new NormalOperationTestContext(state: ChannelState.Failed);
        var handler = new UpdateAddHtlcMessageHandler(NullLogger<UpdateAddHtlcMessageHandler>.Instance,
                                                      context.CreateTransitions());

        // Act
        var exception = await Assert.ThrowsAsync<ChannelErrorException>(
                            () => handler.HandleAsync(CreateAdd(0, 50_000_000), ChannelState.Failed,
                                                      new FeatureOptions(), PeerNodeId));

        // Assert
        Assert.Equal(TestChannelId, exception.ChannelId);
        Assert.Equal(ChannelFailedException.DefaultPeerMessage, exception.PeerMessage);
        Assert.Empty(context.Calls);
    }

    [Fact]
    public async Task Given_HtlcsDisabled_When_Add_Then_IgnoredWithAWarningAsBefore()
    {
        // Arrange
        _context.NodeOptions.EnableHtlcs = false;
        var handler = CreateHandler();

        // Act
        var exception = await Assert.ThrowsAsync<ChannelWarningException>(
                            () => handler.HandleAsync(CreateAdd(0, 50_000_000), ChannelState.Open,
                                                      new FeatureOptions(), PeerNodeId));

        // Assert
        Assert.False(exception.CloseConnection);
        Assert.Contains("not supported yet", exception.PeerMessage);
        Assert.Empty(_context.Calls);
    }

    [Fact]
    public async Task Given_WrongId_When_Add_Then_WarningAndCloseAndTheStateIsUnchanged()
    {
        // Arrange - B2-ADD-R07: ids start at 0 and grow by one
        var handler = CreateHandler();
        var before = _context.State;

        // Act
        var exception = await Assert.ThrowsAsync<ChannelWarningException>(
                            () => handler.HandleAsync(CreateAdd(5, 50_000_000), ChannelState.Open,
                                                      new FeatureOptions(), PeerNodeId));

        // Assert
        Assert.True(exception.CloseConnection);
        Assert.Contains("B2-ADD-R07", exception.Message);
        Assert.Same(before, _context.State);
        Assert.Empty(_context.Calls);
    }

    [Fact]
    public async Task Given_AmountBelowOurMinimum_When_Add_Then_WarningAndClose()
    {
        // Arrange - B2-ADD-R01: 0 msat is below any htlc_minimum_msat
        var handler = CreateHandler();

        // Act
        var exception = await Assert.ThrowsAsync<ChannelWarningException>(
                            () => handler.HandleAsync(CreateAdd(0, 0), ChannelState.Open, new FeatureOptions(),
                                                      PeerNodeId));

        // Assert
        Assert.True(exception.CloseConnection);
        Assert.Contains("B2-ADD-R01", exception.Message);
    }

    [Fact]
    public async Task Given_PersistFails_When_Add_Then_TheSnapshotIsUnchanged()
    {
        // Arrange - I2: memory changes only after the save
        _context.FailSaves();
        var handler = CreateHandler();
        var before = _context.State;

        // Act
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => handler.HandleAsync(CreateAdd(0, 50_000_000), ChannelState.Open, new FeatureOptions(),
                                      PeerNodeId));

        // Assert
        Assert.Same(before, _context.State);
        Assert.Empty(_context.Events.Drain());
    }

    [Fact]
    public async Task Given_ChannelWithoutSnapshot_When_Add_Then_IgnoredWithAWarning()
    {
        // Arrange - a channel opened before the commitment state was wired (NL-232) cannot run the engine
        var context = new NormalOperationTestContext();
        var withoutSnapshot = new NormalOperationTestContext();
        context.ChannelMemoryRepository
               .Setup(r => r.TryGetChannel(TestChannelId, out It.Ref<Domain.Channels.Models.ChannelModel>.IsAny!))
               .Returns(new TryGetDelegate((Domain.Channels.ValueObjects.ChannelId _,
                                            out Domain.Channels.Models.ChannelModel channel) =>
                {
                    channel = CreateChannelWithoutSnapshot(withoutSnapshot);
                    return true;
                }));
        var handler = new UpdateAddHtlcMessageHandler(NullLogger<UpdateAddHtlcMessageHandler>.Instance,
                                                      context.CreateTransitions());

        // Act
        var exception = await Assert.ThrowsAsync<ChannelWarningException>(
                            () => handler.HandleAsync(CreateAdd(0, 50_000_000), ChannelState.Open,
                                                      new FeatureOptions(), PeerNodeId));

        // Assert
        Assert.False(exception.CloseConnection);
        Assert.Contains("no commitment state", exception.Message);
        Assert.Empty(context.Calls);
    }

    private delegate bool TryGetDelegate(Domain.Channels.ValueObjects.ChannelId channelId,
                                         out Domain.Channels.Models.ChannelModel channel);

    private static Domain.Channels.Models.ChannelModel CreateChannelWithoutSnapshot(NormalOperationTestContext source)
    {
        var channel = source.Channel;
        return new Domain.Channels.Models.ChannelModel(channel.ChannelParams, channel.ChannelId,
                                                       channel.CommitmentNumber, channel.FundingOutput,
                                                       channel.IsInitiator, null, null, channel.LocalBalance,
                                                       channel.LocalKeySet, 0, 0, channel.RemoteBalance,
                                                       channel.RemoteKeySet, 0, channel.RemoteNodeId, 0,
                                                       ChannelState.Open, channel.Version);
    }

    private UpdateAddHtlcMessageHandler CreateHandler() =>
        new(NullLogger<UpdateAddHtlcMessageHandler>.Instance, _context.CreateTransitions());

    internal static UpdateAddHtlcMessage CreateAdd(ulong id, ulong amountMsat) =>
        new(new UpdateAddHtlcPayload(LightningMoney.MilliSatoshis(amountMsat), TestChannelId, 600, id,
                                     HashOf(SecretOf(0x01)), Onion));
}