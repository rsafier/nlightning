using MessagePack;
using Microsoft.Extensions.DependencyInjection;
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
using Domain.Node.Options;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Transport.Ipc;
using Transport.Ipc.MessagePack;
using Transport.Ipc.Requests;
using Transport.Ipc.Responses;

/// <summary>
/// <c>bumpsplice</c> (ClientCommand 37, wave SPR lane SPR-B) over IPC: the request reaches
/// <see cref="ISpliceService.BumpAsync(SpliceBumpRequest, CancellationToken)"/> with its feerate and fee cap, the new
/// attempt comes back in the splice response, and the RBF refusals (no pending splice, feerate below the IT-RBF-01
/// minimum, the fee cap, quiescence not negotiated) carry the error code the CLI shows. The splice service is a mock:
/// its RBF is lane SPR-A's (a service without it answers "not available").
/// </summary>
public class BumpSpliceIpcHandlerTests
{
    private static readonly MessagePackSerializerOptions s_options = NLightningMessagePackOptions.Options;
    private static readonly ChannelId s_channelId = new(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());
    private static readonly TxId s_txId = new(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray());

    private readonly Mock<ISpliceService> _spliceService = new();
    private SpliceBumpRequest? _request;

    public BumpSpliceIpcHandlerTests()
    {
        MessagePackSerializer.DefaultOptions = s_options;
        _spliceService.Setup(s => s.BumpAsync(It.IsAny<SpliceBumpRequest>(), It.IsAny<CancellationToken>()))
                      .Callback<SpliceBumpRequest, CancellationToken>((r, _) => _request = r)
                      .ReturnsAsync((SpliceBumpRequest r, CancellationToken _) =>
                                        new SpliceResult(r.ChannelId, SpliceNegotiationState.Signed, s_txId,
                                                         1_099_000));
    }

    [Fact]
    public async Task Given_ABump_When_Handled_Then_TheServiceGetsTheFeerateAndCapAndTheNewAttemptComesBack()
    {
        // Arrange
        var handler = GetHandler();

        // Act
        var response = await handler.HandleAsync(
                           CreateEnvelope(new BumpSpliceIpcRequest
                           {
                               ChannelId = s_channelId,
                               FeeRatePerKw = 2_604,
                               MaxFeeSat = 5_000
                           }), TestContext.Current.CancellationToken);

        // Assert
        var payload = AssertResponse(response);
        Assert.Equal(s_channelId, payload.ChannelId);
        Assert.Equal(SpliceNegotiationState.Signed, payload.State);
        Assert.Equal("1f1e1d1c1b1a191817161514131211100f0e0d0c0b0a09080706050403020100", payload.SpliceTxId);
        Assert.Equal(1_099_000UL, payload.NewCapacitySat);
        Assert.Null(payload.FailureReason);
        Assert.NotNull(_request);
        Assert.Equal(new SpliceBumpRequest(s_channelId, 2_604, 5_000), _request);
        _spliceService.Verify(s => s.StartAsync(It.IsAny<SpliceRequest>(), It.IsAny<CancellationToken>()),
                              Times.Never);
    }

    [Fact]
    public async Task Given_ABumpWithoutAFeeCap_When_Handled_Then_NoCapAndTheContributionIsKept()
    {
        // Arrange
        var handler = GetHandler();

        // Act
        var response = await handler.HandleAsync(
                           CreateEnvelope(new BumpSpliceIpcRequest { ChannelId = s_channelId, FeeRatePerKw = 3_000 }),
                           TestContext.Current.CancellationToken);

        // Assert
        AssertResponse(response);
        Assert.NotNull(_request);
        Assert.Null(_request.MaxFeeSatoshis);
        Assert.Null(_request.ContributionSatoshis);
        Assert.Equal(3_000U, _request.FeeratePerKw);
    }

    [Theory]
    [InlineData("SPR: channel has no pending splice to bump")]
    [InlineData("IT-RBF-01: feerate 2550 sat/kw is below the minimum 2604 sat/kw of the latest attempt")]
    [InlineData("SPR: our share of the new fee 6200 sat is above the cap of 5000 sat")]
    [InlineData("SP-S-01: option_quiesce (35) and option_splice (63) are not both negotiated with the peer")]
    [InlineData("SP-LK-04: splice_locked was sent for a pending attempt")]
    [InlineData("SPR-T2: the splice has 8 RBF attempts (Splice:MaxRbfAttempts)")]
    public async Task Given_TheServiceRefusesTheBump_When_Handled_Then_InvalidOperationWithItsReason(string reason)
    {
        // Arrange
        _spliceService.Setup(s => s.BumpAsync(It.IsAny<SpliceBumpRequest>(), It.IsAny<CancellationToken>()))
                      .ThrowsAsync(new InvalidOperationException(reason));
        var handler = GetHandler();

        // Act
        var response = await handler.HandleAsync(
                           CreateEnvelope(new BumpSpliceIpcRequest
                           {
                               ChannelId = s_channelId,
                               FeeRatePerKw = 2_550,
                               MaxFeeSat = 5_000
                           }), TestContext.Current.CancellationToken);

        // Assert
        var error = AssertError(response);
        Assert.Equal(ErrorCodes.InvalidOperation, error.Code);
        Assert.Equal(reason, error.Message);
    }

    [Fact]
    public async Task Given_AnUnknownChannel_When_Bumping_Then_InvalidChannel()
    {
        // Arrange
        _spliceService.Setup(s => s.BumpAsync(It.IsAny<SpliceBumpRequest>(), It.IsAny<CancellationToken>()))
                      .ThrowsAsync(new KeyNotFoundException("no such channel"));
        var handler = GetHandler();

        // Act
        var response = await handler.HandleAsync(
                           CreateEnvelope(new BumpSpliceIpcRequest { ChannelId = s_channelId, FeeRatePerKw = 2_604 }),
                           TestContext.Current.CancellationToken);

        // Assert
        var error = AssertError(response);
        Assert.Equal(ErrorCodes.InvalidChannel, error.Code);
        Assert.Contains(s_channelId.ToString(), error.Message);
    }

    [Fact]
    public async Task Given_ASpliceServiceWithoutRbf_When_Bumping_Then_NotAvailable()
    {
        // Arrange: the interface's default BumpAsync (a service before lane SPR-A) throws NotImplementedException
        var handler = GetHandler(new ServiceWithoutRbf());

        // Act
        var response = await handler.HandleAsync(
                           CreateEnvelope(new BumpSpliceIpcRequest { ChannelId = s_channelId, FeeRatePerKw = 2_604 }),
                           TestContext.Current.CancellationToken);

        // Assert
        var error = AssertError(response);
        Assert.Equal(ErrorCodes.InvalidOperation, error.Code);
        Assert.Contains("not available", error.Message);
    }

    [Theory]
    [InlineData(252U, null)]
    [InlineData(250_001U, null)]
    [InlineData(2_604U, 0UL)]
    [InlineData(2_604U, 2_100_000_000_000_001UL)]
    public async Task Given_ArgumentsOutOfBounds_When_Bumping_Then_RefusedBeforeTheService(uint feeRatePerKw,
        ulong? maxFeeSat)
    {
        // Arrange
        var handler = GetHandler();

        // Act
        var response = await handler.HandleAsync(
                           CreateEnvelope(new BumpSpliceIpcRequest
                           {
                               ChannelId = s_channelId,
                               FeeRatePerKw = feeRatePerKw,
                               MaxFeeSat = maxFeeSat
                           }), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(ErrorCodes.InvalidOperation, AssertError(response).Code);
        _spliceService.Verify(s => s.BumpAsync(It.IsAny<SpliceBumpRequest>(), It.IsAny<CancellationToken>()),
                              Times.Never);
    }

    [Fact]
    public async Task Given_ThePeerAbortsTheRbf_When_Bumping_Then_AbortedAndTheReason()
    {
        // Arrange
        _spliceService.Setup(s => s.BumpAsync(It.IsAny<SpliceBumpRequest>(), It.IsAny<CancellationToken>()))
                      .ReturnsAsync(new SpliceResult(s_channelId, SpliceNegotiationState.Aborted,
                                                     FailureReason: "tx_abort: rbf attempt too recent"));
        var handler = GetHandler();

        // Act
        var response = await handler.HandleAsync(
                           CreateEnvelope(new BumpSpliceIpcRequest { ChannelId = s_channelId, FeeRatePerKw = 2_604 }),
                           TestContext.Current.CancellationToken);

        // Assert
        var payload = AssertResponse(response);
        Assert.Equal(SpliceNegotiationState.Aborted, payload.State);
        Assert.Null(payload.SpliceTxId);
        Assert.Equal("tx_abort: rbf attempt too recent", payload.FailureReason);
    }

    [Fact]
    public async Task Given_TheRbfOutlastsTheWait_When_Bumping_Then_ItsNegotiationAtThatFeerateIsReported()
    {
        // Arrange: the RBF negotiation at 2,604 sat/kw has its transaction already
        var pending = new TaskCompletionSource<SpliceResult>();
        _spliceService.Setup(s => s.BumpAsync(It.IsAny<SpliceBumpRequest>(), It.IsAny<CancellationToken>()))
                      .Returns(pending.Task);
        _spliceService.Setup(s => s.GetNegotiation(s_channelId))
                      .Returns(() => CreateNegotiation(2_604, SpliceNegotiationState.CommitmentSigned,
                                                       DateTimeOffset.UtcNow));
        var handler = new BumpSpliceClientHandler(_spliceService.Object,
                                                  NullLogger<BumpSpliceClientHandler>.Instance,
                                                  TimeSpan.FromMilliseconds(50));

        // Act
        var response = await handler.HandleAsync(new BumpSpliceClientRequest(s_channelId, 2_604),
                                                 TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(SpliceNegotiationState.CommitmentSigned, response.State);
        Assert.Equal(s_txId, response.SpliceTxId);
        Assert.False(pending.Task.IsCompleted);
    }

    [Fact]
    public async Task Given_ThePreviousAttemptDuringTheWait_When_Bumping_Then_ItIsNotReportedAsTheBump()
    {
        // Arrange: the stored negotiation is the attempt being replaced (another feerate)
        var pending = new TaskCompletionSource<SpliceResult>();
        _spliceService.Setup(s => s.BumpAsync(It.IsAny<SpliceBumpRequest>(), It.IsAny<CancellationToken>()))
                      .Returns(pending.Task);
        _spliceService.Setup(s => s.GetNegotiation(s_channelId))
                      .Returns(() => CreateNegotiation(2_500, SpliceNegotiationState.Signed, DateTimeOffset.UtcNow));
        var handler = new BumpSpliceClientHandler(_spliceService.Object,
                                                  NullLogger<BumpSpliceClientHandler>.Instance,
                                                  TimeSpan.FromMilliseconds(50));

        // Act
        var response = await handler.HandleAsync(new BumpSpliceClientRequest(s_channelId, 2_604),
                                                 TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(SpliceNegotiationState.AwaitingQuiescence, response.State);
        Assert.Null(response.SpliceTxId);
    }

    [Fact]
    public async Task Given_NoSpliceService_When_Bumping_Then_NotAvailable()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSpliceIpcServices();
        var handler = services.BuildServiceProvider().GetServices<IIpcCommandHandler>()
                              .Single(h => h.Command == ClientCommand.BumpSplice);

        // Act
        var response = await handler.HandleAsync(
                           CreateEnvelope(new BumpSpliceIpcRequest { ChannelId = s_channelId, FeeRatePerKw = 2_604 }),
                           TestContext.Current.CancellationToken);

        // Assert
        var error = AssertError(response);
        Assert.Equal(ErrorCodes.InvalidOperation, error.Code);
        Assert.Contains(nameof(ISpliceService), error.Message);
    }

    [Fact]
    public void Given_TheRegistrationCalledTwice_When_Composed_Then_OneBumpSpliceHandler()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(_spliceService.Object);
        services.AddSpliceIpcServices();
        services.AddSpliceIpcServices();

        // Act
        var handlers = services.BuildServiceProvider().GetServices<IIpcCommandHandler>().ToList();

        // Assert
        Assert.Single(handlers, h => h.Command == ClientCommand.BumpSplice);
    }

    [Fact]
    public void Given_ABumpRequest_When_RoundTripped_Then_Keys0To2Kept()
    {
        // Arrange
        var request = new BumpSpliceIpcRequest { ChannelId = s_channelId, FeeRatePerKw = 2_604, MaxFeeSat = 5_000 };

        // Act
        var bytes = MessagePackSerializer.Serialize(request, s_options, TestContext.Current.CancellationToken);
        var read = MessagePackSerializer.Deserialize<BumpSpliceIpcRequest>(bytes, s_options,
                                                                           TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(s_channelId, read.ChannelId);
        Assert.Equal(2_604U, read.FeeRatePerKw);
        Assert.Equal(5_000UL, read.MaxFeeSat);
        Assert.Equal(new SpliceBumpRequest(s_channelId, 2_604, 5_000), read.ToClientRequest().ToSpliceBumpRequest());
    }

    private static SpliceNegotiationModel CreateNegotiation(uint feeratePerKw, SpliceNegotiationState state,
                                                            DateTimeOffset createdAt)
    {
        return new SpliceNegotiationModel(s_channelId, true, 10_000, 0, feeratePerKw, 0,
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

    private IIpcCommandHandler GetHandler(ISpliceService? spliceService = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(spliceService ?? _spliceService.Object);
        services.AddSpliceIpcServices();
        return services.BuildServiceProvider().GetServices<IIpcCommandHandler>()
                       .Single(h => h.Command == ClientCommand.BumpSplice);
    }

    private static IpcEnvelope CreateEnvelope(BumpSpliceIpcRequest request)
    {
        return new IpcEnvelope
        {
            Version = 1,
            Command = ClientCommand.BumpSplice,
            CorrelationId = Guid.NewGuid(),
            Kind = IpcEnvelopeKind.Request,
            Payload = MessagePackSerializer.Serialize(request, s_options, TestContext.Current.CancellationToken)
        };
    }

    /// <summary>A splice service that does not override the RBF members (as <c>SpliceService</c> before SPR-A).</summary>
    private sealed class ServiceWithoutRbf : ISpliceService
    {
        public Task<SpliceResult> StartAsync(SpliceRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<IChannelMessage>> HandleSpliceInitAsync(
            SpliceInitMessage message, FeatureOptions negotiatedFeatures,
            CompactPubKey peerPubKey, IUnitOfWork unitOfWork,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<IChannelMessage>> HandleSpliceAckAsync(
            SpliceAckMessage message, FeatureOptions negotiatedFeatures,
            CompactPubKey peerPubKey, IUnitOfWork unitOfWork,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public SpliceNegotiationModel? GetNegotiation(ChannelId channelId) => null;
    }
}