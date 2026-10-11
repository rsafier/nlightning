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
using Domain.Payments.Trampoline;
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

    [Fact]
    public async Task Given_TrampolineRelays_When_Handled_Then_TheyAreListedWithTheForwardFilters()
    {
        // Arrange (NL-875 TR3-T3): a fulfilled relay of two parts on one channel
        _forwardCircuitRepositoryMock
           .Setup(x => x.ListAsync(It.IsAny<ForwardCircuitListQuery>(), It.IsAny<CancellationToken>()))
           .ReturnsAsync([]);
        _forwardCircuitRepositoryMock
           .Setup(x => x.SummarizeAsync(It.IsAny<ForwardCircuitListQuery>(), It.IsAny<CancellationToken>()))
           .ReturnsAsync(new ForwardCircuitTotals(0, 0, 0, 0, 0));
        var nextNode = new CompactPubKey([0x03, .. Enumerable.Repeat((byte)0x44, 32)]);
        var relay = new TrampolineRelayModel(s_paymentHash, nextNode, LightningMoney.MilliSatoshis(1_000_000), 600,
                                             LightningMoney.MilliSatoshis(1_010_000), s_createdAt);
        relay.MarkSending();
        relay.MarkFulfilled(s_sharedSecret, LightningMoney.MilliSatoshis(5_000), s_createdAt.AddSeconds(3));
        TrampolineRelayPartModel[] parts =
        [
            new(s_paymentHash, s_incomingChannelId, 1, LightningMoney.MilliSatoshis(400_000), 1_200, s_sharedSecret,
                s_sharedSecret, null),
            new(s_paymentHash, s_incomingChannelId, 2, LightningMoney.MilliSatoshis(610_000), 1_200, s_sharedSecret,
                s_sharedSecret, null)
        ];
        var queries = new List<TrampolineRelayListQuery>();
        var relays = new Mock<ITrampolineRelayDbRepository>();
        relays.Setup(x => x.ListAsync(It.IsAny<TrampolineRelayListQuery>(), It.IsAny<CancellationToken>()))
              .Callback((TrampolineRelayListQuery query, CancellationToken _) => queries.Add(query))
              .ReturnsAsync([relay]);
        relays.Setup(x => x.GetPartsAsync(s_paymentHash)).ReturnsAsync(parts);
        var handler = new ListForwardsClientHandler(_forwardCircuitRepositoryMock.Object,
                                                    NullLogger<ListForwardsClientHandler>.Instance,
                                                    trampolineRelayRepository: relays.Object);

        // Act
        var response = await handler.HandleAsync(new ListForwardsClientRequest
        {
            Take = 10,
            Status = ForwardCircuitStatus.Fulfilled,
            ChannelId = s_incomingChannelId
        }, TestContext.Current.CancellationToken);

        // Assert
        var query = Assert.Single(queries);
        Assert.Equal(new TrampolineRelayListQuery(0, 10, null, null, TrampolineRelayStatus.Fulfilled,
                                                  s_incomingChannelId), query);
        var listed = Assert.Single(response.TrampolineRelays);
        Assert.Equal(TrampolineRelayStatus.Fulfilled, listed.Status);
        Assert.Equal(2, listed.Parts);
        Assert.Equal([s_incomingChannelId], listed.IncomingChannelIds);
        Assert.Equal(LightningMoney.MilliSatoshis(1_010_000), listed.IncomingAmount);
        Assert.Equal(LightningMoney.MilliSatoshis(1_000_000), listed.AmountOut);
        Assert.Equal(LightningMoney.MilliSatoshis(5_000), listed.FeeEarned);
        Assert.Equal(nextNode, listed.NextNodeId);
        Assert.Empty(response.Forwards);
    }

    [Fact]
    public async Task Given_RelaysAndReplacedAttempts_When_Handled_Then_TheTotalsCountThemAndTheAttemptsAreListed()
    {
        // Arrange (NL-981, NL-899): forwards 1 pending / 2 fulfilled (3,000 msat) / 1 failed; relays 1 fulfilled
        // (5,000 msat), 1 failed and 1 replaced attempt; the page lists the relay and the attempt, newest first
        _forwardCircuitRepositoryMock
           .Setup(x => x.ListAsync(It.IsAny<ForwardCircuitListQuery>(), It.IsAny<CancellationToken>()))
           .ReturnsAsync([]);
        _forwardCircuitRepositoryMock
           .Setup(x => x.SummarizeAsync(It.IsAny<ForwardCircuitListQuery>(), It.IsAny<CancellationToken>()))
           .ReturnsAsync(new ForwardCircuitTotals(1, 0, 2, 1, 3_000));
        var nextNode = new CompactPubKey([0x03, .. Enumerable.Repeat((byte)0x44, 32)]);
        var relay = new TrampolineRelayModel(s_paymentHash, nextNode, LightningMoney.MilliSatoshis(1_000_000), 600,
                                             LightningMoney.MilliSatoshis(1_010_000), s_createdAt.AddMinutes(5));
        relay.MarkSending();
        relay.MarkFulfilled(s_sharedSecret, LightningMoney.MilliSatoshis(5_000), s_createdAt.AddMinutes(6));
        var attempt = new TrampolineRelayAttemptModel(s_paymentHash, 1, null, LightningMoney.MilliSatoshis(1_000_000),
                                                      600, LightningMoney.MilliSatoshis(1_001_000),
                                                      LightningMoney.MilliSatoshis(1_001_000), 2,
                                                      [s_incomingChannelId, s_outgoingChannelId], 0x2019,
                                                      "fee or expiry insufficient", s_createdAt,
                                                      s_createdAt.AddSeconds(1));
        var queries = new List<TrampolineRelayListQuery>();
        var relays = new Mock<ITrampolineRelayDbRepository>();
        relays.Setup(x => x.ListAsync(It.IsAny<TrampolineRelayListQuery>(), It.IsAny<CancellationToken>()))
              .Callback((TrampolineRelayListQuery query, CancellationToken _) => queries.Add(query))
              .ReturnsAsync([relay]);
        relays.Setup(x => x.ListReplacedAttemptsAsync(It.IsAny<TrampolineRelayListQuery>(),
                                                      It.IsAny<CancellationToken>()))
              .Callback((TrampolineRelayListQuery query, CancellationToken _) => queries.Add(query))
              .ReturnsAsync([attempt]);
        relays.Setup(x => x.SummarizeAsync(It.IsAny<TrampolineRelayListQuery>(), It.IsAny<CancellationToken>()))
              .Callback((TrampolineRelayListQuery query, CancellationToken _) => queries.Add(query))
              .ReturnsAsync(new TrampolineRelayTotals(0, 0, 1, 2, 5_000));
        relays.Setup(x => x.GetPartsAsync(s_paymentHash)).ReturnsAsync([]);
        var handler = new ListForwardsClientHandler(_forwardCircuitRepositoryMock.Object,
                                                    NullLogger<ListForwardsClientHandler>.Instance,
                                                    trampolineRelayRepository: relays.Object);

        // Act
        var page = await handler.HandleAsync(new ListForwardsClientRequest { Take = 10 },
                                             TestContext.Current.CancellationToken);
        var second = await handler.HandleAsync(new ListForwardsClientRequest { Skip = 1, Take = 1 },
                                               TestContext.Current.CancellationToken);

        // Assert: the totals count the relays with the forwards, and the relays' share is kept apart
        Assert.Equal(1, page.Summary.Pending);
        Assert.Equal(3, page.Summary.Fulfilled);
        Assert.Equal(3, page.Summary.Failed);
        Assert.Equal(8_000, page.Summary.FulfilledFeesMsat);
        Assert.Equal(new TrampolineRelayTotals(0, 0, 1, 2, 5_000), page.Summary.TrampolineRelays);
        Assert.Collection(page.TrampolineRelays,
                          r =>
                          {
                              Assert.Equal(TrampolineRelayStatus.Fulfilled, r.Status);
                              Assert.Null(r.ReplacedAttempt);
                          },
                          r =>
                          {
                              Assert.Equal(TrampolineRelayStatus.Failed, r.Status);
                              Assert.Equal(1, r.ReplacedAttempt);
                              Assert.Equal(2, r.Parts);
                              Assert.Equal([s_incomingChannelId, s_outgoingChannelId], r.IncomingChannelIds);
                              Assert.Equal((ushort)0x2019, r.FailureCode);
                              Assert.Null(r.FeeEarned);
                          });

        // Both sources are read from the top through the end of the page, then the page is cut
        Assert.All(queries.Take(3), q => Assert.Equal(new TrampolineRelayListQuery(0, 10), q));
        Assert.All(queries.Skip(3), q => Assert.Equal(new TrampolineRelayListQuery(0, 2), q));
        Assert.Equal(1, Assert.Single(second.TrampolineRelays).ReplacedAttempt);
    }

    [Fact]
    public async Task Given_ARepositoryThatCannotSum_When_Handled_Then_TheRelaysAddNothingToTheTotals()
    {
        // Arrange: a test double without the relay sums (the interface default throws NotSupportedException)
        _forwardCircuitRepositoryMock
           .Setup(x => x.ListAsync(It.IsAny<ForwardCircuitListQuery>(), It.IsAny<CancellationToken>()))
           .ReturnsAsync([]);
        _forwardCircuitRepositoryMock
           .Setup(x => x.SummarizeAsync(It.IsAny<ForwardCircuitListQuery>(), It.IsAny<CancellationToken>()))
           .ReturnsAsync(new ForwardCircuitTotals(0, 0, 2, 0, 3_000));
        var relays = new Mock<ITrampolineRelayDbRepository>();
        relays.Setup(x => x.ListAsync(It.IsAny<TrampolineRelayListQuery>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync([]);
        relays.Setup(x => x.ListReplacedAttemptsAsync(It.IsAny<TrampolineRelayListQuery>(),
                                                      It.IsAny<CancellationToken>()))
              .ReturnsAsync([]);
        relays.Setup(x => x.SummarizeAsync(It.IsAny<TrampolineRelayListQuery>(), It.IsAny<CancellationToken>()))
              .ThrowsAsync(new NotSupportedException());
        var handler = new ListForwardsClientHandler(_forwardCircuitRepositoryMock.Object,
                                                    NullLogger<ListForwardsClientHandler>.Instance,
                                                    trampolineRelayRepository: relays.Object);

        // Act
        var response = await handler.HandleAsync(new ListForwardsClientRequest(),
                                                 TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(2, response.Summary.Fulfilled);
        Assert.Equal(3_000, response.Summary.FulfilledFeesMsat);
        Assert.Equal(default, response.Summary.TrampolineRelays);
    }

    [Fact]
    public async Task Given_AScidNamingNoChannel_When_Handled_Then_NoRelayIsListed()
    {
        // Arrange: a relay has no outgoing channel, so only an incoming channel can match it
        _forwardCircuitRepositoryMock
           .Setup(x => x.ListAsync(It.IsAny<ForwardCircuitListQuery>(), It.IsAny<CancellationToken>()))
           .ReturnsAsync([]);
        _forwardCircuitRepositoryMock
           .Setup(x => x.SummarizeAsync(It.IsAny<ForwardCircuitListQuery>(), It.IsAny<CancellationToken>()))
           .ReturnsAsync(new ForwardCircuitTotals(0, 0, 0, 0, 0));
        _channelMemoryRepositoryMock.Setup(x => x.FindChannels(It.IsAny<Func<ChannelModel, bool>>())).Returns([]);
        var relays = new Mock<ITrampolineRelayDbRepository>(MockBehavior.Strict);
        var handler = new ListForwardsClientHandler(_forwardCircuitRepositoryMock.Object,
                                                    NullLogger<ListForwardsClientHandler>.Instance,
                                                    _channelMemoryRepositoryMock.Object,
                                                    trampolineRelayRepository: relays.Object);

        // Act
        var response = await handler.HandleAsync(new ListForwardsClientRequest
        {
            ChannelScid = new ShortChannelId(900_000, 1, 0)
        }, TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(response.TrampolineRelays);
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