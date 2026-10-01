namespace NLightning.Daemon.Tests.Handlers;

using Application.Payments;
using Daemon.Handlers;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Client.Constants;
using Domain.Client.Enums;
using Domain.Client.Exceptions;
using Domain.Client.Requests;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Payments.Enums;
using Domain.Payments.Interfaces;
using Domain.Payments.Models;
using Domain.Protocol.Onion.Enums;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// The <c>listforwards</c> handler (ClientCommand 40, NL-597): the filters ride the repository query, a
/// <c>short_channel_id</c> channel filter also matches the incoming side through the channel memory, and the summary
/// carries the counts, the fees and the refused counters (NL-598).
/// </summary>
public class ListForwardsClientHandlerTests
{
    private static readonly ChannelId s_incomingChannelId = CreateChannelId(0x21);
    private static readonly ChannelId s_outgoingChannelId = CreateChannelId(0x22);
    private static readonly Hash s_paymentHash = new(Enumerable.Repeat((byte)0xab, 32).ToArray());
    private static readonly Secret s_sharedSecret = new(Enumerable.Repeat((byte)0xcd, 32).ToArray());
    private static readonly DateTimeOffset s_createdAt = DateTimeOffset.FromUnixTimeSeconds(1_790_812_800);

    private readonly Mock<IForwardCircuitDbRepository> _forwardCircuitRepositoryMock = new();
    private readonly Mock<IChannelMemoryRepository> _channelMemoryRepositoryMock = new();

    [Fact]
    public async Task Given_FiltersInTheRequest_When_Handled_Then_TheQueryCarriesThem()
    {
        // Arrange
        var queries = new List<ForwardCircuitListQuery>();
        var tokens = new List<CancellationToken>();
        _forwardCircuitRepositoryMock
           .Setup(x => x.ListAsync(It.IsAny<ForwardCircuitListQuery>(), It.IsAny<CancellationToken>()))
           .Callback((ForwardCircuitListQuery query, CancellationToken token) =>
                     {
                         queries.Add(query);
                         tokens.Add(token);
                     })
           .ReturnsAsync([]);
        _forwardCircuitRepositoryMock
           .Setup(x => x.SummarizeAsync(It.IsAny<ForwardCircuitListQuery>(), It.IsAny<CancellationToken>()))
           .Callback((ForwardCircuitListQuery query, CancellationToken token) =>
                     {
                         queries.Add(query);
                         tokens.Add(token);
                     })
           .ReturnsAsync(new ForwardCircuitTotals(0, 0, 0, 0, 0));
        var handler = CreateHandler();
        var channelId = CreateChannelId(0x23);

        // Act
        await handler.HandleAsync(new ListForwardsClientRequest
        {
            Skip = 5,
            Take = 42,
            Since = DateTimeOffset.FromUnixTimeSeconds(1_000),
            Until = DateTimeOffset.FromUnixTimeSeconds(2_000),
            Status = ForwardCircuitStatus.Fulfilled,
            ChannelId = channelId,
            ChannelScid = new ShortChannelId(800_000, 12, 0)
        }, TestContext.Current.CancellationToken);

        // Assert
        var expected = new ForwardCircuitListQuery(5, 42, DateTimeOffset.FromUnixTimeSeconds(1_000),
                                                   DateTimeOffset.FromUnixTimeSeconds(2_000),
                                                   ForwardCircuitStatus.Fulfilled, channelId,
                                                   new ShortChannelId(800_000, 12, 0));
        Assert.Equal(2, queries.Count);
        Assert.All(queries, query => Assert.Equal(expected, query));
        Assert.All(tokens, token => Assert.Equal(TestContext.Current.CancellationToken, token));
        Assert.Equal(ClientCommand.ListForwards, handler.Command);
    }

    [Fact]
    public async Task Given_APage_When_Handled_Then_TheForwardsAndSummaryAreMapped()
    {
        // Arrange
        var fulfilled = CreateCircuit(1);
        fulfilled.AddOutgoingHtlc(s_outgoingChannelId, 11);
        fulfilled.MarkFulfilled(s_createdAt.AddSeconds(60));
        var failed = CreateCircuit(2);
        failed.AddOutgoingHtlc(s_outgoingChannelId, 12);
        failed.MarkFailed(s_outgoingChannelId, 12, s_createdAt.AddSeconds(120),
                          (ushort)FailureCode.TemporaryChannelFailure);
        _forwardCircuitRepositoryMock
           .Setup(x => x.ListAsync(It.IsAny<ForwardCircuitListQuery>(), It.IsAny<CancellationToken>()))
           .ReturnsAsync([fulfilled, failed]);
        _forwardCircuitRepositoryMock
           .Setup(x => x.SummarizeAsync(It.IsAny<ForwardCircuitListQuery>(), It.IsAny<CancellationToken>()))
           .ReturnsAsync(new ForwardCircuitTotals(1, 2, 2, 1, 15_000));
        var counterMock = new Mock<IRefusedHtlcCounter>();
        counterMock.Setup(x => x.Snapshot())
                   .Returns(new Dictionary<RefusedHtlcReason, long>
                    {
                        { RefusedHtlcReason.ShutdownDrain, 2 },
                        { RefusedHtlcReason.UnknownNextChannel, 1 }
                    });
        var handler = CreateHandler(counterMock.Object, withMemoryRepository: false);

        // Act
        var response = await handler.HandleAsync(new ListForwardsClientRequest { Take = 10 },
                                                 TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(2, response.Forwards.Count);
        var fulfilledInfo = response.Forwards[0];
        Assert.Equal(s_incomingChannelId, fulfilledInfo.IncomingChannelId);
        Assert.Equal(1UL, fulfilledInfo.IncomingHtlcId);
        Assert.Equal(120_000UL, fulfilledInfo.IncomingAmount.MilliSatoshi);
        Assert.Equal(500u, fulfilledInfo.IncomingCltvExpiry);
        Assert.Equal(new ShortChannelId(800_000, 12, 0), fulfilledInfo.OutgoingShortChannelId);
        Assert.Equal(s_outgoingChannelId, fulfilledInfo.OutgoingChannelId);
        Assert.Equal(11UL, fulfilledInfo.OutgoingHtlcId);
        Assert.Equal(100_000UL, fulfilledInfo.OutgoingAmount.MilliSatoshi);
        Assert.Equal(480u, fulfilledInfo.OutgoingCltvExpiry);
        Assert.Equal(20_000UL, fulfilledInfo.Fee.MilliSatoshi);
        Assert.Equal(s_paymentHash, fulfilledInfo.PaymentHash);
        Assert.Equal(s_createdAt, fulfilledInfo.CreatedAt);
        Assert.Equal(s_createdAt.AddSeconds(60), fulfilledInfo.ResolvedAt);
        Assert.Equal(ForwardCircuitStatus.Fulfilled, fulfilledInfo.Status);
        Assert.Null(fulfilledInfo.FailureCode);
        Assert.Null(fulfilledInfo.FailureCodeName);
        Assert.Null(fulfilledInfo.FailureSource);
        Assert.Null(fulfilledInfo.IncomingChannelScid);
        Assert.Null(fulfilledInfo.OutgoingChannelScid);
        Assert.Null(fulfilledInfo.FailureSourceScid);
        var failedInfo = response.Forwards[1];
        Assert.Equal(ForwardCircuitStatus.Failed, failedInfo.Status);
        Assert.Equal((ushort)FailureCode.TemporaryChannelFailure, failedInfo.FailureCode);
        Assert.Equal("TemporaryChannelFailure", failedInfo.FailureCodeName);
        Assert.Equal(s_outgoingChannelId, failedInfo.FailureSource);
        Assert.Equal(s_createdAt.AddSeconds(120), failedInfo.ResolvedAt);
        var summary = response.Summary;
        Assert.Equal((1, 2, 2, 1), (summary.Pending, summary.Offered, summary.Fulfilled, summary.Failed));
        Assert.Equal(6, summary.Total);
        Assert.Equal(15_000, summary.FulfilledFeesMsat);
        Assert.Equal(3, summary.RefusedTotal);
        Assert.Collection(summary.RefusedByReason,
                          refused =>
                          {
                              Assert.Equal("UnknownNextChannel", refused.Reason);
                              Assert.Equal(1, refused.Count);
                          },
                          refused =>
                          {
                              Assert.Equal("ShutdownDrain", refused.Reason);
                              Assert.Equal(2, refused.Count);
                          });
    }

    [Theory]
    [InlineData(-1, 100)]
    [InlineData(0, 0)]
    public async Task Given_AnInvalidPage_When_Handled_Then_ThrowsClientException(int skip, int take)
    {
        // Arrange
        var handler = CreateHandler();
        var request = new ListForwardsClientRequest { Skip = skip, Take = take };

        // Act
        var exception = await Assert.ThrowsAsync<ClientException>(() => handler.HandleAsync(
                                                                     request, TestContext.Current.CancellationToken));

        // Assert
        Assert.Equal(ErrorCodes.InvalidOperation, exception.ErrorCode);
        _forwardCircuitRepositoryMock.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Given_AChannelScidInMemoryRepo_When_Handled_Then_TheFilterMatchesTheIncomingSideToo()
    {
        // Arrange
        var channel = CreateChannel(CreateChannelId(0x07), ChannelState.Open);
        channel.ShortChannelId = new ShortChannelId(800_000, 12, 0);
        _channelMemoryRepositoryMock.Setup(x => x.FindChannels(It.IsAny<Func<ChannelModel, bool>>()))
                                    .Returns((Func<ChannelModel, bool> predicate) =>
                                                 new[] { channel }.Where(predicate).ToList());
        ForwardCircuitListQuery? query = null;
        _forwardCircuitRepositoryMock
           .Setup(x => x.ListAsync(It.IsAny<ForwardCircuitListQuery>(), It.IsAny<CancellationToken>()))
           .Callback((ForwardCircuitListQuery captured, CancellationToken _) => query = captured)
           .ReturnsAsync([]);
        _forwardCircuitRepositoryMock
           .Setup(x => x.SummarizeAsync(It.IsAny<ForwardCircuitListQuery>(), It.IsAny<CancellationToken>()))
           .ReturnsAsync(new ForwardCircuitTotals(0, 0, 0, 0, 0));
        var handler = CreateHandler();

        // Act
        await handler.HandleAsync(new ListForwardsClientRequest { ChannelScid = new ShortChannelId(800_000, 12, 0) },
                                  TestContext.Current.CancellationToken);

        // Assert: the scid names our channel, so its channel id rides the query besides the scid
        Assert.NotNull(query);
        Assert.Equal(channel.ChannelId, query.ChannelId);
        Assert.Equal(new ShortChannelId(800_000, 12, 0), query.ChannelScid);
    }

    [Fact]
    public async Task Given_ALoadedChannelWithAScid_When_Handled_Then_TheForwardsCarryTheScid()
    {
        // Arrange
        var channel = CreateChannel(CreateChannelId(0x07), ChannelState.Open);
        channel.ShortChannelId = new ShortChannelId(800_000, 12, 0);
        var found = channel;
        _channelMemoryRepositoryMock.Setup(x => x.TryGetChannel(It.IsAny<ChannelId>(), out found)).Returns(true);
        _channelMemoryRepositoryMock.Setup(x => x.FindChannels(It.IsAny<Func<ChannelModel, bool>>())).Returns([]);
        var fulfilled = CreateCircuit(1);
        fulfilled.AddOutgoingHtlc(channel.ChannelId, 11);
        fulfilled.MarkFulfilled(s_createdAt.AddSeconds(60));
        _forwardCircuitRepositoryMock
           .Setup(x => x.ListAsync(It.IsAny<ForwardCircuitListQuery>(), It.IsAny<CancellationToken>()))
           .ReturnsAsync([fulfilled]);
        _forwardCircuitRepositoryMock
           .Setup(x => x.SummarizeAsync(It.IsAny<ForwardCircuitListQuery>(), It.IsAny<CancellationToken>()))
           .ReturnsAsync(new ForwardCircuitTotals(0, 0, 1, 0, 20_000));
        var handler = CreateHandler();

        // Act
        var response = await handler.HandleAsync(new ListForwardsClientRequest(),
                                                 TestContext.Current.CancellationToken);

        // Assert
        var forward = Assert.Single(response.Forwards);
        Assert.Equal("800000x12x0", forward.IncomingChannelScid);
        Assert.Equal("800000x12x0", forward.OutgoingChannelScid);
        Assert.Null(forward.FailureSourceScid);
    }

    [Fact]
    public async Task Given_AChannelMissingFromTheMemoryRepo_When_Handled_Then_TheScidFieldsFallBackToNull()
    {
        // Arrange
        ChannelModel? notFound = null;
        _channelMemoryRepositoryMock.Setup(x => x.TryGetChannel(It.IsAny<ChannelId>(), out notFound))
                                    .Returns(false);
        _channelMemoryRepositoryMock.Setup(x => x.FindChannels(It.IsAny<Func<ChannelModel, bool>>())).Returns([]);
        var fulfilled = CreateCircuit(1);
        fulfilled.AddOutgoingHtlc(s_outgoingChannelId, 11);
        fulfilled.MarkFulfilled(s_createdAt.AddSeconds(60));
        _forwardCircuitRepositoryMock
           .Setup(x => x.ListAsync(It.IsAny<ForwardCircuitListQuery>(), It.IsAny<CancellationToken>()))
           .ReturnsAsync([fulfilled]);
        _forwardCircuitRepositoryMock
           .Setup(x => x.SummarizeAsync(It.IsAny<ForwardCircuitListQuery>(), It.IsAny<CancellationToken>()))
           .ReturnsAsync(new ForwardCircuitTotals(0, 0, 1, 0, 20_000));
        var handler = CreateHandler();

        // Act
        var response = await handler.HandleAsync(new ListForwardsClientRequest(),
                                                 TestContext.Current.CancellationToken);

        // Assert
        var forward = Assert.Single(response.Forwards);
        Assert.Null(forward.IncomingChannelScid);
        Assert.Null(forward.OutgoingChannelScid);
        Assert.Null(forward.FailureSourceScid);
    }

    private ListForwardsClientHandler CreateHandler(IRefusedHtlcCounter? refusedCounter = null,
                                                    bool withMemoryRepository = true) =>
        new(_forwardCircuitRepositoryMock.Object, NullLogger<ListForwardsClientHandler>.Instance,
            withMemoryRepository ? _channelMemoryRepositoryMock.Object : null, refusedCounter);

    private static ForwardCircuitModel CreateCircuit(ulong incomingHtlcId) =>
        new(s_incomingChannelId, incomingHtlcId, LightningMoney.MilliSatoshis(120_000), 500, s_paymentHash,
            s_sharedSecret, new ShortChannelId(800_000, 12, 0), LightningMoney.MilliSatoshis(100_000), 480,
            s_createdAt);

    private static ChannelModel CreateChannel(ChannelId channelId, ChannelState state)
    {
        var peerId = new CompactPubKey([0x02, .. Enumerable.Repeat((byte)0x11, 32)]);
        return new ChannelModel(new ChannelParams(), channelId, null, null, true, null, null,
                                LightningMoney.Satoshis(100_000),
                                new ChannelKeySetModel(0, peerId, peerId, peerId, peerId, peerId, peerId), 0, 0,
                                LightningMoney.Zero, null, 0, peerId, 0, state, ChannelVersion.V1);
    }

    private static ChannelId CreateChannelId(byte fill) => new(Enumerable.Repeat(fill, 32).ToArray());
}