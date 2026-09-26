using MessagePack;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace NLightning.Daemon.Tests.Ipc.Handlers;

using Daemon.Interfaces;
using Daemon.Ipc.Handlers;
using Domain.Channels.ValueObjects;
using Domain.Client.Constants;
using Domain.Client.Enums;
using Domain.Client.Exceptions;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Domain.Money;
using Transport.Ipc;
using Transport.Ipc.MessagePack;
using Transport.Ipc.Requests;
using Transport.Ipc.Responses;

public class OpenChannelIpcHandlerTests
{
    private static readonly MessagePackSerializerOptions s_options = NLightningMessagePackOptions.Options;

    public OpenChannelIpcHandlerTests()
    {
        MessagePackSerializer.DefaultOptions = s_options;
    }

    [Fact]
    public async Task GivenSubstituteClientHandler_WhenOpenChannelHandleAsync_ThenUsesItAndReturnsResponse()
    {
        // Arrange
        var channelId = new ChannelId(Enumerable.Repeat((byte)7, 32).ToArray());
        var clientHandlerMock =
            new Mock<IClientCommandHandler<OpenChannelClientRequest, OpenChannelClientResponse>>();
        clientHandlerMock.Setup(x => x.HandleAsync(It.IsAny<OpenChannelClientRequest>(),
                                                   It.IsAny<CancellationToken>()))
                         .ReturnsAsync(new OpenChannelClientResponse(channelId));
        var handler = new OpenChannelIpcHandler(NullLogger<OpenChannelIpcHandler>.Instance,
                                                BuildProvider(clientHandlerMock.Object));

        // Act
        var response = await handler.HandleAsync(CreateOpenChannelEnvelope(), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(IpcEnvelopeKind.Response, response.Kind);
        var payload = MessagePackSerializer.Deserialize<OpenChannelIpcResponse>(
            response.Payload, s_options, TestContext.Current.CancellationToken);
        Assert.Equal(channelId, payload.ChannelId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(20_000L)]
    public async Task Given_PushAmount_When_OpenChannelHandleAsync_Then_ClientRequestCarriesIt(long? pushSats)
    {
        // Arrange
        OpenChannelClientRequest? received = null;
        var clientHandlerMock =
            new Mock<IClientCommandHandler<OpenChannelClientRequest, OpenChannelClientResponse>>();
        clientHandlerMock.Setup(x => x.HandleAsync(It.IsAny<OpenChannelClientRequest>(),
                                                   It.IsAny<CancellationToken>()))
                         .Callback<OpenChannelClientRequest, CancellationToken>((r, _) => received = r)
                         .ReturnsAsync(new OpenChannelClientResponse(ChannelId.Zero));
        var handler = new OpenChannelIpcHandler(NullLogger<OpenChannelIpcHandler>.Instance,
                                                BuildProvider(clientHandlerMock.Object));
        var push = pushSats is null ? null : LightningMoney.Satoshis(pushSats.Value);

        // Act
        await handler.HandleAsync(CreateOpenChannelEnvelope(push), TestContext.Current.CancellationToken);

        // Assert
        Assert.NotNull(received);
        Assert.Equal(LightningMoney.Satoshis(100_000), received.FundingAmount);
        Assert.Equal(push, received.PushAmount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Given_PublicFlag_When_OpenChannelHandleAsync_Then_ClientRequestCarriesIt(bool isPublic)
    {
        // Arrange (G1-T1: key 4 of OpenChannelIpcRequest)
        OpenChannelClientRequest? received = null;
        var clientHandlerMock =
            new Mock<IClientCommandHandler<OpenChannelClientRequest, OpenChannelClientResponse>>();
        clientHandlerMock.Setup(x => x.HandleAsync(It.IsAny<OpenChannelClientRequest>(),
                                                   It.IsAny<CancellationToken>()))
                         .Callback<OpenChannelClientRequest, CancellationToken>((r, _) => received = r)
                         .ReturnsAsync(new OpenChannelClientResponse(ChannelId.Zero));
        var handler = new OpenChannelIpcHandler(NullLogger<OpenChannelIpcHandler>.Instance,
                                                BuildProvider(clientHandlerMock.Object));
        var request = new OpenChannelIpcRequest
        {
            NodeInfo = "02abc@127.0.0.1:9735",
            Amount = LightningMoney.Satoshis(100_000),
            IsPublic = isPublic
        };
        var envelope = new IpcEnvelope
        {
            Version = 1,
            Command = ClientCommand.OpenChannel,
            CorrelationId = Guid.NewGuid(),
            Kind = IpcEnvelopeKind.Request,
            Payload = MessagePackSerializer.Serialize(request, s_options, TestContext.Current.CancellationToken)
        };

        // Act
        await handler.HandleAsync(envelope, TestContext.Current.CancellationToken);

        // Assert
        Assert.NotNull(received);
        Assert.Equal(isPublic, received.IsPublic);
    }

    [Fact]
    public void Given_ARequestWithoutKey4_When_Deserialized_Then_ItIsPrivate()
    {
        // Arrange (an older client: keys 0, 2 and 3 only): the same request without its last array element
        var current = MessagePackSerializer.Serialize(
            new OpenChannelIpcRequest
            {
                NodeInfo = "02abc@127.0.0.1:9735",
                Amount = LightningMoney.Satoshis(1_000),
                PushAmount = LightningMoney.Satoshis(10)
            }, s_options, TestContext.Current.CancellationToken);
        Assert.Equal(0x95, current[0]); // fixarray of 5 (keys 0-4)
        Assert.Equal(0xC2, current[^1]); // key 4: false
        byte[] older = [0x94, .. current[1..^1]];

        // Act
        var request = MessagePackSerializer.Deserialize<OpenChannelIpcRequest>(
            older, s_options, TestContext.Current.CancellationToken);

        // Assert
        Assert.False(request.IsPublic);
        Assert.Equal("02abc@127.0.0.1:9735", request.NodeInfo);
        Assert.Equal(LightningMoney.Satoshis(10), request.PushAmount);
    }

    [Fact]
    public async Task GivenClientException_WhenOpenChannelHandleAsync_ThenErrorCodeIsTheExceptionCode()
    {
        // Arrange
        var clientHandlerMock =
            new Mock<IClientCommandHandler<OpenChannelClientRequest, OpenChannelClientResponse>>();
        clientHandlerMock.Setup(x => x.HandleAsync(It.IsAny<OpenChannelClientRequest>(),
                                                   It.IsAny<CancellationToken>()))
                         .ThrowsAsync(new ClientException(ErrorCodes.NotEnoughBalance, "Not enough funds"));
        var handler = new OpenChannelIpcHandler(NullLogger<OpenChannelIpcHandler>.Instance,
                                                BuildProvider(clientHandlerMock.Object));

        // Act
        var response = await handler.HandleAsync(CreateOpenChannelEnvelope(), TestContext.Current.CancellationToken);

        // Assert
        AssertError(response, ErrorCodes.NotEnoughBalance, "Not enough funds");
    }

    [Fact]
    public async Task GivenClientException_WhenOpenChannelSubscriptionHandleAsync_ThenErrorCodeIsTheExceptionCode()
    {
        // Arrange
        var clientHandlerMock =
            new Mock<IClientCommandHandler<OpenChannelClientSubscriptionRequest,
                OpenChannelClientSubscriptionResponse>>();
        clientHandlerMock.Setup(x => x.HandleAsync(It.IsAny<OpenChannelClientSubscriptionRequest>(),
                                                   It.IsAny<CancellationToken>()))
                         .ThrowsAsync(new ClientException(ErrorCodes.InvalidChannel, "Unknown channel"));
        var handler = new OpenChannelSubscriptionIpcHandler(
            NullLogger<OpenChannelSubscriptionIpcHandler>.Instance, BuildProvider(clientHandlerMock.Object));

        var request = new OpenChannelSubscriptionIpcRequest
        {
            ChannelId = new ChannelId(Enumerable.Repeat((byte)9, 32).ToArray())
        };
        var envelope = new IpcEnvelope
        {
            Version = 1,
            Command = ClientCommand.OpenChannelSubscription,
            CorrelationId = Guid.NewGuid(),
            Kind = IpcEnvelopeKind.Request,
            Payload = MessagePackSerializer.Serialize(request, s_options, TestContext.Current.CancellationToken)
        };

        // Act
        var response = await handler.HandleAsync(envelope, TestContext.Current.CancellationToken);

        // Assert
        AssertError(response, ErrorCodes.InvalidChannel, "Unknown channel");
    }

    private static IServiceProvider BuildProvider<T>(T clientHandler) where T : class
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => clientHandler);
        return services.BuildServiceProvider();
    }

    private static IpcEnvelope CreateOpenChannelEnvelope(LightningMoney? pushAmount = null)
    {
        var request = new OpenChannelIpcRequest
        {
            NodeInfo = "peer@127.0.0.1:9735",
            Amount = LightningMoney.Satoshis(100_000),
            PushAmount = pushAmount
        };

        return new IpcEnvelope
        {
            Version = 1,
            Command = ClientCommand.OpenChannel,
            CorrelationId = Guid.NewGuid(),
            Kind = IpcEnvelopeKind.Request,
            Payload = MessagePackSerializer.Serialize(request, s_options, TestContext.Current.CancellationToken)
        };
    }

    private static void AssertError(IpcEnvelope response, string expectedCode, string expectedMessage)
    {
        Assert.Equal(IpcEnvelopeKind.Error, response.Kind);
        var error = MessagePackSerializer.Deserialize<IpcError>(response.Payload, s_options,
                                                                TestContext.Current.CancellationToken);
        Assert.Equal(expectedCode, error.Code);
        Assert.Equal(expectedMessage, error.Message);
    }
}