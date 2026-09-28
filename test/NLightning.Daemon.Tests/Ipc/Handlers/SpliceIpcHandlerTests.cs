using MessagePack;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace NLightning.Daemon.Tests.Ipc.Handlers;

using Daemon.Extensions;
using Daemon.Handlers;
using Daemon.Ipc.Interfaces;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Splicing;
using Domain.Channels.Splicing.Enums;
using Domain.Channels.Splicing.Interfaces;
using Domain.Channels.Splicing.Models;
using Domain.Channels.ValueObjects;
using Domain.Client.Constants;
using Domain.Client.Enums;
using Domain.Client.Requests;
using Domain.Crypto.ValueObjects;
using Domain.Exceptions;
using Domain.Money;
using Transport.Ipc;
using Transport.Ipc.MessagePack;
using Transport.Ipc.Requests;
using Transport.Ipc.Responses;

/// <summary>
/// <c>splicein</c> and <c>spliceout</c> (ClientCommand 33/34, splicing plan SP1-E-T1) over IPC: the request reaches
/// <see cref="ISpliceService"/> as a signed contribution, the result comes back with the txid in display order, and
/// each refusal of the service (SP-S-01/02: not negotiated, <c>shutdown</c> sent, a splice not locked yet) carries the
/// error code the CLI shows.
/// </summary>
public class SpliceIpcHandlerTests
{
    private const string Address = "bcrt1qw508d6qejxtdg4y5r3zarvary0c5xw7kygt080";

    private static readonly MessagePackSerializerOptions s_options = NLightningMessagePackOptions.Options;
    private static readonly ChannelId s_channelId = new(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());
    private static readonly TxId s_txId = new(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray());

    private readonly Mock<ISpliceService> _spliceService = new();
    private SpliceRequest? _request;

    public SpliceIpcHandlerTests()
    {
        MessagePackSerializer.DefaultOptions = s_options;
        _spliceService.Setup(s => s.StartAsync(It.IsAny<SpliceRequest>(), It.IsAny<CancellationToken>()))
                      .Callback<SpliceRequest, CancellationToken>((r, _) => _request = r)
                      .ReturnsAsync((SpliceRequest r, CancellationToken _) =>
                                        new SpliceResult(r.ChannelId, SpliceNegotiationState.Signed, s_txId,
                                                         (ulong)(1_000_000 + r.ContributionSatoshis)));
    }

    [Fact]
    public async Task Given_ASpliceIn_When_Handled_Then_TheServiceGetsAPositiveContributionAndTheResultComesBack()
    {
        // Arrange
        var handler = GetHandler(ClientCommand.SpliceIn);

        // Act
        var response = await handler.HandleAsync(
                           CreateEnvelope(ClientCommand.SpliceIn,
                                          new SpliceInIpcRequest
                                          {
                                              ChannelId = s_channelId,
                                              AmountSat = 100_000,
                                              FeeRatePerKw = 2_500
                                          }), TestContext.Current.CancellationToken);

        // Assert
        var payload = AssertResponse(response);
        Assert.Equal(s_channelId, payload.ChannelId);
        Assert.Equal(SpliceNegotiationState.Signed, payload.State);
        Assert.Equal("1f1e1d1c1b1a191817161514131211100f0e0d0c0b0a09080706050403020100", payload.SpliceTxId);
        Assert.Equal(1_100_000UL, payload.NewCapacitySat);
        Assert.Null(payload.FailureReason);
        Assert.NotNull(_request);
        Assert.Equal(s_channelId, _request.ChannelId);
        Assert.Equal(100_000, _request.ContributionSatoshis);
        Assert.Equal(2_500U, _request.FeeratePerKw);
        Assert.Null(_request.SpliceOutAddress);
    }

    [Fact]
    public async Task Given_ASpliceOutToAnAddress_When_Handled_Then_TheServiceGetsANegativeContributionAndTheAddress()
    {
        // Arrange
        var handler = GetHandler(ClientCommand.SpliceOut);

        // Act
        var response = await handler.HandleAsync(
                           CreateEnvelope(ClientCommand.SpliceOut,
                                          new SpliceOutIpcRequest
                                          {
                                              ChannelId = s_channelId,
                                              AmountSat = 50_000,
                                              Address = Address
                                          }), TestContext.Current.CancellationToken);

        // Assert
        var payload = AssertResponse(response);
        Assert.Equal(950_000UL, payload.NewCapacitySat);
        Assert.NotNull(_request);
        Assert.Equal(-50_000, _request.ContributionSatoshis);
        Assert.Equal(Address, _request.SpliceOutAddress);
        Assert.Null(_request.FeeratePerKw);
    }

    [Fact]
    public async Task Given_ASpliceOutWithoutAnAddress_When_Handled_Then_TheServiceChoosesOurWallet()
    {
        // Arrange
        var handler = GetHandler(ClientCommand.SpliceOut);

        // Act
        var response = await handler.HandleAsync(
                           CreateEnvelope(ClientCommand.SpliceOut,
                                          new SpliceOutIpcRequest { ChannelId = s_channelId, AmountSat = 50_000 }),
                           TestContext.Current.CancellationToken);

        // Assert
        AssertResponse(response);
        Assert.NotNull(_request);
        Assert.Null(_request.SpliceOutAddress);
    }

    [Theory]
    [InlineData("SP-S-01: option_quiesce (35) and option_splice (63) are not both negotiated with the peer")]
    [InlineData("SP-S-01: we sent shutdown on this channel")]
    [InlineData("SP-S-01: a negotiated splice is not locked yet; use RBF to change it")]
    [InlineData("SP-S-02: 900000 sat is above our balance of 700000 sat")]
    public async Task Given_TheServiceRefusesARule_When_SplicingIn_Then_InvalidOperationWithItsReason(string reason)
    {
        // Arrange
        _spliceService.Setup(s => s.StartAsync(It.IsAny<SpliceRequest>(), It.IsAny<CancellationToken>()))
                      .ThrowsAsync(new InvalidOperationException(reason));
        var handler = GetHandler(ClientCommand.SpliceIn);

        // Act
        var response = await handler.HandleAsync(
                           CreateEnvelope(ClientCommand.SpliceIn,
                                          new SpliceInIpcRequest { ChannelId = s_channelId, AmountSat = 10_000 }),
                           TestContext.Current.CancellationToken);

        // Assert
        var error = AssertError(response);
        Assert.Equal(ErrorCodes.InvalidOperation, error.Code);
        Assert.Equal(reason, error.Message);
    }

    [Theory]
    [InlineData("SP-S-01: option_quiesce (35) and option_splice (63) are not both negotiated with the peer")]
    [InlineData("SP-S-01: we sent shutdown on this channel")]
    [InlineData("SP-S-01: a negotiated splice is not locked yet; use RBF to change it")]
    public async Task Given_TheServiceRefusesARule_When_SplicingOut_Then_InvalidOperationWithItsReason(string reason)
    {
        // Arrange
        _spliceService.Setup(s => s.StartAsync(It.IsAny<SpliceRequest>(), It.IsAny<CancellationToken>()))
                      .ThrowsAsync(new InvalidOperationException(reason));
        var handler = GetHandler(ClientCommand.SpliceOut);

        // Act
        var response = await handler.HandleAsync(
                           CreateEnvelope(ClientCommand.SpliceOut,
                                          new SpliceOutIpcRequest
                                          {
                                              ChannelId = s_channelId,
                                              AmountSat = 10_000,
                                              Address = Address
                                          }), TestContext.Current.CancellationToken);

        // Assert
        var error = AssertError(response);
        Assert.Equal(ErrorCodes.InvalidOperation, error.Code);
        Assert.Equal(reason, error.Message);
    }

    [Fact]
    public async Task Given_AnUnknownChannel_When_Splicing_Then_InvalidChannel()
    {
        // Arrange
        _spliceService.Setup(s => s.StartAsync(It.IsAny<SpliceRequest>(), It.IsAny<CancellationToken>()))
                      .ThrowsAsync(new KeyNotFoundException("no such channel"));
        var handler = GetHandler(ClientCommand.SpliceIn);

        // Act
        var response = await handler.HandleAsync(
                           CreateEnvelope(ClientCommand.SpliceIn,
                                          new SpliceInIpcRequest { ChannelId = s_channelId, AmountSat = 10_000 }),
                           TestContext.Current.CancellationToken);

        // Assert
        var error = AssertError(response);
        Assert.Equal(ErrorCodes.InvalidChannel, error.Code);
        Assert.Contains(s_channelId.ToString(), error.Message);
    }

    [Fact]
    public async Task Given_TooLittleInTheWallet_When_SplicingIn_Then_NotEnoughBalance()
    {
        // Arrange
        _spliceService.Setup(s => s.StartAsync(It.IsAny<SpliceRequest>(), It.IsAny<CancellationToken>()))
                      .ThrowsAsync(new InsufficientFundsException(LightningMoney.Satoshis(100_500),
                                                                  LightningMoney.Satoshis(20_000)));
        var handler = GetHandler(ClientCommand.SpliceIn);

        // Act
        var response = await handler.HandleAsync(
                           CreateEnvelope(ClientCommand.SpliceIn,
                                          new SpliceInIpcRequest { ChannelId = s_channelId, AmountSat = 100_000 }),
                           TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(ErrorCodes.NotEnoughBalance, AssertError(response).Code);
    }

    [Fact]
    public async Task Given_AnAddressTheServiceRefuses_When_SplicingOut_Then_InvalidAddress()
    {
        // Arrange
        _spliceService.Setup(s => s.StartAsync(It.IsAny<SpliceRequest>(), It.IsAny<CancellationToken>()))
                      .ThrowsAsync(new ArgumentException("not a regtest address",
                                                         nameof(SpliceRequest.SpliceOutAddress)));
        var handler = GetHandler(ClientCommand.SpliceOut);

        // Act
        var response = await handler.HandleAsync(
                           CreateEnvelope(ClientCommand.SpliceOut,
                                          new SpliceOutIpcRequest
                                          {
                                              ChannelId = s_channelId,
                                              AmountSat = 10_000,
                                              Address = "tb1qnotours"
                                          }), TestContext.Current.CancellationToken);

        // Assert
        var error = AssertError(response);
        Assert.Equal(ErrorCodes.InvalidAddress, error.Code);
        Assert.StartsWith("not a regtest address", error.Message);
    }

    public static TheoryData<ArgumentException> NotAboutTheAddress => new()
    {
        new ArgumentOutOfRangeException(nameof(SpliceRequest.FeeratePerKw), "feerate out of range"),
        new ArgumentNullException(nameof(SpliceRequest.SpliceOutAddress), "a bug inside the service"),
        new ArgumentException("contribution too large", nameof(SpliceRequest.ContributionSatoshis)),
        new ArgumentException("no parameter named")
    };

    [Theory]
    [MemberData(nameof(NotAboutTheAddress))]
    public async Task Given_AnArgumentExceptionNotAboutTheAddress_When_SplicingOut_Then_InvalidOperation(
        ArgumentException exception)
    {
        // Arrange
        _spliceService.Setup(s => s.StartAsync(It.IsAny<SpliceRequest>(), It.IsAny<CancellationToken>()))
                      .ThrowsAsync(exception);
        var handler = GetHandler(ClientCommand.SpliceOut);

        // Act
        var response = await handler.HandleAsync(
                           CreateEnvelope(ClientCommand.SpliceOut,
                                          new SpliceOutIpcRequest
                                          {
                                              ChannelId = s_channelId,
                                              AmountSat = 10_000,
                                              Address = Address
                                          }), TestContext.Current.CancellationToken);

        // Assert
        var error = AssertError(response);
        Assert.Equal(ErrorCodes.InvalidOperation, error.Code);
        Assert.Equal(exception.Message, error.Message);
    }

    [Fact]
    public async Task Given_AnEmptyAddress_When_SplicingOut_Then_InvalidAddressBeforeTheService()
    {
        // Arrange
        var handler = GetHandler(ClientCommand.SpliceOut);

        // Act
        var response = await handler.HandleAsync(
                           CreateEnvelope(ClientCommand.SpliceOut,
                                          new SpliceOutIpcRequest
                                          {
                                              ChannelId = s_channelId,
                                              AmountSat = 10_000,
                                              Address = " "
                                          }), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(ErrorCodes.InvalidAddress, AssertError(response).Code);
        _spliceService.Verify(s => s.StartAsync(It.IsAny<SpliceRequest>(), It.IsAny<CancellationToken>()),
                              Times.Never);
    }

    [Theory]
    [InlineData(0UL, null)]
    [InlineData(2_100_000_000_000_001UL, null)]
    [InlineData(10_000UL, 252U)]
    [InlineData(10_000UL, 250_001U)]
    public async Task Given_ArgumentsOutOfBounds_When_SplicingIn_Then_RefusedBeforeTheService(ulong amountSat,
        uint? feeRatePerKw)
    {
        // Arrange
        var handler = GetHandler(ClientCommand.SpliceIn);

        // Act
        var response = await handler.HandleAsync(
                           CreateEnvelope(ClientCommand.SpliceIn,
                                          new SpliceInIpcRequest
                                          {
                                              ChannelId = s_channelId,
                                              AmountSat = amountSat,
                                              FeeRatePerKw = feeRatePerKw
                                          }), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(ErrorCodes.InvalidOperation, AssertError(response).Code);
        _spliceService.Verify(s => s.StartAsync(It.IsAny<SpliceRequest>(), It.IsAny<CancellationToken>()),
                              Times.Never);
    }

    [Fact]
    public async Task Given_ThePeerAborts_When_SplicingOut_Then_TheResponseCarriesAbortedAndTheReason()
    {
        // Arrange
        _spliceService.Setup(s => s.StartAsync(It.IsAny<SpliceRequest>(), It.IsAny<CancellationToken>()))
                      .ReturnsAsync(new SpliceResult(s_channelId, SpliceNegotiationState.Aborted,
                                                     FailureReason: "tx_abort: feerate too low"));
        var handler = GetHandler(ClientCommand.SpliceOut);

        // Act
        var response = await handler.HandleAsync(
                           CreateEnvelope(ClientCommand.SpliceOut,
                                          new SpliceOutIpcRequest { ChannelId = s_channelId, AmountSat = 10_000 }),
                           TestContext.Current.CancellationToken);

        // Assert
        var payload = AssertResponse(response);
        Assert.Equal(SpliceNegotiationState.Aborted, payload.State);
        Assert.Null(payload.SpliceTxId);
        Assert.Null(payload.NewCapacitySat);
        Assert.Equal("tx_abort: feerate too low", payload.FailureReason);
    }

    [Fact]
    public async Task Given_TheSpliceStopsAtCommitmentSigned_When_SplicingIn_Then_TheAnswerNamesItAndTheReconnection()
    {
        // Arrange: the peer disconnected after both commitment_signed; the service resolves the wait there and keeps the
        // splice for the reconnection (day-0 step 5 (a))
        _spliceService.Setup(s => s.StartAsync(It.IsAny<SpliceRequest>(), It.IsAny<CancellationToken>()))
                      .ReturnsAsync(new SpliceResult(s_channelId, SpliceNegotiationState.CommitmentSigned, s_txId,
                                                     1_100_000,
                                                     "stopped before tx_signatures: Disconnected"));
        var handler = GetHandler(ClientCommand.SpliceIn);

        // Act
        var response = await handler.HandleAsync(
                           CreateEnvelope(ClientCommand.SpliceIn,
                                          new SpliceInIpcRequest { ChannelId = s_channelId, AmountSat = 100_000 }),
                           TestContext.Current.CancellationToken);

        // Assert
        var payload = AssertResponse(response);
        Assert.Equal(SpliceNegotiationState.CommitmentSigned, payload.State);
        Assert.Equal("1f1e1d1c1b1a191817161514131211100f0e0d0c0b0a09080706050403020100", payload.SpliceTxId);
        Assert.Equal("Splice 1f1e1d1c1b1a191817161514131211100f0e0d0c0b0a09080706050403020100 stopped at "
                   + "CommitmentSigned (stopped before tx_signatures: Disconnected); it is kept and completes when the "
                   + "peer reconnects (channel_reestablish), see listchannels.", payload.FailureReason);
    }

    [Fact]
    public async Task Given_TheNegotiationOutlastsTheWait_When_SplicingIn_Then_ItsCurrentStateAndTheSpliceGoesOn()
    {
        // Arrange: the service never completes; the negotiation has its transaction already
        var pending = new TaskCompletionSource<SpliceResult>();
        _spliceService.Setup(s => s.StartAsync(It.IsAny<SpliceRequest>(), It.IsAny<CancellationToken>()))
                      .Returns(pending.Task);
        _spliceService.Setup(s => s.GetNegotiation(s_channelId))
                      .Returns(() => CreateNegotiation(10_000, SpliceNegotiationState.CommitmentSigned,
                                                       DateTimeOffset.UtcNow));
        var handler = new SpliceInClientHandler(_spliceService.Object, NullLogger<SpliceInClientHandler>.Instance,
                                                TimeSpan.FromMilliseconds(50));

        // Act
        var response = await handler.HandleAsync(new SpliceInClientRequest(s_channelId, 10_000),
                                                 TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(SpliceNegotiationState.CommitmentSigned, response.State);
        Assert.Equal(s_txId, response.SpliceTxId);
        Assert.Null(response.FailureReason);
        Assert.False(pending.Task.IsCompleted);
    }

    [Fact]
    public async Task Given_APreviousSignedSpliceDuringTheWait_When_SplicingIn_Then_AwaitingQuiescenceWithoutItsTxId()
    {
        // Arrange: the service has not registered this call's negotiation yet (waiting for quiescence); the stored one
        // is the previous splice's, signed an hour ago
        var pending = new TaskCompletionSource<SpliceResult>();
        _spliceService.Setup(s => s.StartAsync(It.IsAny<SpliceRequest>(), It.IsAny<CancellationToken>()))
                      .Returns(pending.Task);
        _spliceService.Setup(s => s.GetNegotiation(s_channelId))
                      .Returns(CreateNegotiation(20_000, SpliceNegotiationState.Signed,
                                                 DateTimeOffset.UtcNow.AddHours(-1)));
        var handler = new SpliceInClientHandler(_spliceService.Object, NullLogger<SpliceInClientHandler>.Instance,
                                                TimeSpan.FromMilliseconds(50));

        // Act
        var response = await handler.HandleAsync(new SpliceInClientRequest(s_channelId, 10_000),
                                                 TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(SpliceNegotiationState.AwaitingQuiescence, response.State);
        Assert.Null(response.SpliceTxId);
        Assert.Null(response.NewCapacitySat);
    }

    [Fact]
    public async Task Given_ASpliceOutNegotiationOfThePeerDuringTheWait_When_SplicingIn_Then_ItIsNotReported()
    {
        // Arrange: a fresh negotiation, but in the other direction (not this call's)
        var pending = new TaskCompletionSource<SpliceResult>();
        _spliceService.Setup(s => s.StartAsync(It.IsAny<SpliceRequest>(), It.IsAny<CancellationToken>()))
                      .Returns(pending.Task);
        _spliceService.Setup(s => s.GetNegotiation(s_channelId))
                      .Returns(() => CreateNegotiation(-10_000, SpliceNegotiationState.CommitmentSigned,
                                                       DateTimeOffset.UtcNow));
        var handler = new SpliceInClientHandler(_spliceService.Object, NullLogger<SpliceInClientHandler>.Instance,
                                                TimeSpan.FromMilliseconds(50));

        // Act
        var response = await handler.HandleAsync(new SpliceInClientRequest(s_channelId, 10_000),
                                                 TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(SpliceNegotiationState.AwaitingQuiescence, response.State);
        Assert.Null(response.SpliceTxId);
    }

    [Fact]
    public async Task Given_TheServiceTimesOutItself_When_SplicingIn_Then_InvalidOperationNotStillRunning()
    {
        // Arrange: the service ended the splice with its own timeout (quiescence never came)
        _spliceService.Setup(s => s.StartAsync(It.IsAny<SpliceRequest>(), It.IsAny<CancellationToken>()))
                      .ThrowsAsync(new TimeoutException("quiescence timed out"));
        _spliceService.Setup(s => s.GetNegotiation(s_channelId))
                      .Returns(() => CreateNegotiation(10_000, SpliceNegotiationState.AwaitingQuiescence,
                                                       DateTimeOffset.UtcNow));
        var handler = GetHandler(ClientCommand.SpliceIn);

        // Act
        var response = await handler.HandleAsync(
                           CreateEnvelope(ClientCommand.SpliceIn,
                                          new SpliceInIpcRequest { ChannelId = s_channelId, AmountSat = 10_000 }),
                           TestContext.Current.CancellationToken);

        // Assert
        var error = AssertError(response);
        Assert.Equal(ErrorCodes.InvalidOperation, error.Code);
        Assert.Equal("quiescence timed out", error.Message);
    }

    [Fact]
    public async Task Given_TheSpliceFailsAfterTheWait_When_ItEnds_Then_TheFailureIsLogged()
    {
        // Arrange
        var pending = new TaskCompletionSource<SpliceResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _spliceService.Setup(s => s.StartAsync(It.IsAny<SpliceRequest>(), It.IsAny<CancellationToken>()))
                      .Returns(pending.Task);
        var logger = new Mock<ILogger<SpliceInClientHandler>>();
        logger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
        var failure = new InvalidOperationException("the signer refused");
        var logged = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        logger.Setup(l => l.Log(LogLevel.Warning, It.IsAny<EventId>(), It.IsAny<It.IsAnyType>(),
                                It.IsAny<Exception?>(), It.IsAny<Func<It.IsAnyType, Exception?, string>>()))
              .Callback(new InvocationAction(i => logged.TrySetResult((Exception?)i.Arguments[3])));
        var handler = new SpliceInClientHandler(_spliceService.Object, logger.Object, TimeSpan.FromMilliseconds(50));
        var response = await handler.HandleAsync(new SpliceInClientRequest(s_channelId, 10_000),
                                                 TestContext.Current.CancellationToken);
        Assert.Equal(SpliceNegotiationState.AwaitingQuiescence, response.State);

        // Act
        pending.SetException(failure);

        // Assert
        var exception = await logged.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Same(failure, exception);
    }

    [Fact]
    public async Task Given_NoSpliceService_When_Splicing_Then_NotAvailable()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSpliceIpcServices();
        var handler = services.BuildServiceProvider().GetServices<IIpcCommandHandler>()
                              .Single(h => h.Command == ClientCommand.SpliceOut);

        // Act
        var response = await handler.HandleAsync(
                           CreateEnvelope(ClientCommand.SpliceOut,
                                          new SpliceOutIpcRequest { ChannelId = s_channelId, AmountSat = 10_000 }),
                           TestContext.Current.CancellationToken);

        // Assert
        var error = AssertError(response);
        Assert.Equal(ErrorCodes.InvalidOperation, error.Code);
        Assert.Contains(nameof(ISpliceService), error.Message);
    }

    [Fact]
    public void Given_TheRegistrationCalledTwice_When_Composed_Then_OneHandlerPerSpliceCommand()
    {
        // Arrange
        var services = BuildServices();
        services.AddSpliceIpcServices();

        // Act
        var provider = services.BuildServiceProvider();

        // Assert
        var handlers = provider.GetServices<IIpcCommandHandler>().ToList();
        Assert.Single(handlers, h => h.Command == ClientCommand.SpliceIn);
        Assert.Single(handlers, h => h.Command == ClientCommand.SpliceOut);
    }

    [Fact]
    public void Given_AResponseWithoutOptionalFields_When_RoundTripped_Then_Equal()
    {
        // Arrange
        var response = new SpliceIpcResponse
        {
            ChannelId = s_channelId,
            State = SpliceNegotiationState.AwaitingQuiescence
        };

        // Act
        var bytes = MessagePackSerializer.Serialize(response, s_options, TestContext.Current.CancellationToken);
        var read = MessagePackSerializer.Deserialize<SpliceIpcResponse>(bytes, s_options,
                                                                        TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(s_channelId, read.ChannelId);
        Assert.Equal(SpliceNegotiationState.AwaitingQuiescence, read.State);
        Assert.Null(read.SpliceTxId);
        Assert.Null(read.NewCapacitySat);
        Assert.Null(read.FailureReason);
    }

    private static SpliceNegotiationModel CreateNegotiation(long contribution, SpliceNegotiationState state,
                                                            DateTimeOffset createdAt)
    {
        return new SpliceNegotiationModel(s_channelId, true, contribution, 0, 2_500, 0,
                                          new CompactPubKey([0x02, .. new byte[32]]), 1, null, false, false, null,
                                          state, s_txId, createdAt);
    }

    private static SpliceIpcResponse AssertResponse(IpcEnvelope response)
    {
        Assert.Equal(IpcEnvelopeKind.Response, response.Kind);
        return MessagePackSerializer.Deserialize<SpliceIpcResponse>(response.Payload, s_options,
                                                                    TestContext.Current.CancellationToken);
    }

    private static IpcError AssertError(IpcEnvelope response)
    {
        Assert.Equal(IpcEnvelopeKind.Error, response.Kind);
        return MessagePackSerializer.Deserialize<IpcError>(response.Payload, s_options,
                                                           TestContext.Current.CancellationToken);
    }

    private IIpcCommandHandler GetHandler(ClientCommand command)
    {
        var provider = BuildServices().BuildServiceProvider();
        return provider.GetServices<IIpcCommandHandler>().Single(h => h.Command == command);
    }

    private ServiceCollection BuildServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(_spliceService.Object);
        services.AddSpliceIpcServices();
        return services;
    }

    private static IpcEnvelope CreateEnvelope<T>(ClientCommand command, T request)
    {
        return new IpcEnvelope
        {
            Version = 1,
            Command = command,
            CorrelationId = Guid.NewGuid(),
            Kind = IpcEnvelopeKind.Request,
            Payload = MessagePackSerializer.Serialize(request, s_options, TestContext.Current.CancellationToken)
        };
    }
}