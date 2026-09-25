using MessagePack;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace NLightning.Daemon.Tests.Ipc.Handlers;

using Daemon.Interfaces;
using Daemon.Ipc.Handlers;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Enums;
using Domain.Channels.ValueObjects;
using Domain.Client.Constants;
using Domain.Client.Enums;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Transport.Ipc;
using Transport.Ipc.MessagePack;
using Transport.Ipc.Requests;
using Transport.Ipc.Responses;

public class ListChannelsIpcHandlerTests
{
    private static readonly MessagePackSerializerOptions s_options = NLightningMessagePackOptions.Options;

    public ListChannelsIpcHandlerTests()
    {
        MessagePackSerializer.DefaultOptions = s_options;
    }

    [Fact]
    public async Task Given_ClientHandlerListsAChannel_When_HandleAsync_Then_EveryFieldCrossesTheWire()
    {
        // Arrange
        var channelId = new ChannelId(Enumerable.Repeat((byte)7, 32).ToArray());
        var peerId = CreatePubKey(3);
        var fundingTxId = new TxId(Enumerable.Repeat((byte)9, 32).ToArray());
        var clientHandlerMock =
            new Mock<IClientCommandHandler<ListChannelsClientRequest, ListChannelsClientResponse>>();
        ListChannelsClientRequest? received = null;
        clientHandlerMock.Setup(x => x.HandleAsync(It.IsAny<ListChannelsClientRequest>(),
                                                   It.IsAny<CancellationToken>()))
                         .Callback((ListChannelsClientRequest r, CancellationToken _) => received = r)
                         .ReturnsAsync(new ListChannelsClientResponse([
                             new ChannelInfoClientResponse
                             {
                                 ChannelId = channelId,
                                 PeerId = peerId,
                                 State = ChannelState.Open,
                                 IsInitiator = true,
                                 IsPeerConnected = true,
                                 ShortChannelId = new ShortChannelId(120, 3, 1),
                                 FundingTxId = fundingTxId,
                                 FundingOutputIndex = 1,
                                 Capacity = LightningMoney.Satoshis(1_000_000),
                                 LocalBalance = LightningMoney.MilliSatoshis(699_999_001),
                                 RemoteBalance = LightningMoney.MilliSatoshis(300_000_999),
                                 LocalCommitmentNumber = 4,
                                 RemoteCommitmentNumber = 5,
                                 OfferedHtlcCount = 1,
                                 ReceivedHtlcCount = 2,
                                 DataLossDetected = true
                             }
                         ]));
        var handler = new ListChannelsIpcHandler(NullLogger<ListChannelsIpcHandler>.Instance,
                                                 BuildProvider(clientHandlerMock.Object));

        // Act
        var response = await handler.HandleAsync(CreateEnvelope(new ListChannelsIpcRequest { PeerId = peerId }),
                                                 TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(IpcEnvelopeKind.Response, response.Kind);
        Assert.Equal(ClientCommand.ListChannels, response.Command);
        Assert.NotNull(received);
        Assert.Equal(peerId, received.PeerId);

        var payload = MessagePackSerializer.Deserialize<ListChannelsIpcResponse>(
            response.Payload, s_options, TestContext.Current.CancellationToken);
        var channel = Assert.Single(payload.Channels);
        Assert.Equal(channelId, channel.ChannelId);
        Assert.Equal(peerId, channel.PeerId);
        Assert.Equal(ChannelState.Open, channel.State);
        Assert.True(channel.IsInitiator);
        Assert.True(channel.IsPeerConnected);
        Assert.Equal((120UL << 40) | (3UL << 16) | 1UL, channel.ShortChannelId);
        Assert.Equal(fundingTxId, channel.FundingTxId);
        Assert.Equal((ushort)1, channel.FundingOutputIndex);
        Assert.Equal(LightningMoney.Satoshis(1_000_000), channel.Capacity);
        Assert.Equal(699_999_001UL, channel.LocalBalance.MilliSatoshi);
        Assert.Equal(300_000_999UL, channel.RemoteBalance.MilliSatoshi);
        Assert.Equal(4UL, channel.LocalCommitmentNumber);
        Assert.Equal(5UL, channel.RemoteCommitmentNumber);
        Assert.Equal(1, channel.OfferedHtlcCount);
        Assert.Equal(2, channel.ReceivedHtlcCount);
        Assert.True(channel.DataLossDetected);
    }

    [Fact]
    public async Task Given_NoPeerFilterAndNoChannels_When_HandleAsync_Then_EmptyListAndNoFilter()
    {
        // Arrange
        var clientHandlerMock =
            new Mock<IClientCommandHandler<ListChannelsClientRequest, ListChannelsClientResponse>>();
        ListChannelsClientRequest? received = null;
        clientHandlerMock.Setup(x => x.HandleAsync(It.IsAny<ListChannelsClientRequest>(),
                                                   It.IsAny<CancellationToken>()))
                         .Callback((ListChannelsClientRequest r, CancellationToken _) => received = r)
                         .ReturnsAsync(new ListChannelsClientResponse([]));
        var handler = new ListChannelsIpcHandler(NullLogger<ListChannelsIpcHandler>.Instance,
                                                 BuildProvider(clientHandlerMock.Object));

        // Act
        var response = await handler.HandleAsync(CreateEnvelope(new ListChannelsIpcRequest()),
                                                 TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(IpcEnvelopeKind.Response, response.Kind);
        Assert.Null(received!.PeerId);
        var payload = MessagePackSerializer.Deserialize<ListChannelsIpcResponse>(
            response.Payload, s_options, TestContext.Current.CancellationToken);
        Assert.Empty(payload.Channels);
    }

    [Fact]
    public async Task Given_ClientHandlerThrows_When_HandleAsync_Then_ServerError()
    {
        // Arrange
        var clientHandlerMock =
            new Mock<IClientCommandHandler<ListChannelsClientRequest, ListChannelsClientResponse>>();
        clientHandlerMock.Setup(x => x.HandleAsync(It.IsAny<ListChannelsClientRequest>(),
                                                   It.IsAny<CancellationToken>()))
                         .ThrowsAsync(new InvalidOperationException("db down"));
        var handler = new ListChannelsIpcHandler(NullLogger<ListChannelsIpcHandler>.Instance,
                                                 BuildProvider(clientHandlerMock.Object));

        // Act
        var response = await handler.HandleAsync(CreateEnvelope(new ListChannelsIpcRequest()),
                                                 TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(IpcEnvelopeKind.Error, response.Kind);
        var error = MessagePackSerializer.Deserialize<IpcError>(response.Payload, s_options,
                                                                TestContext.Current.CancellationToken);
        Assert.Equal(ErrorCodes.ServerError, error.Code);
        Assert.Contains("db down", error.Message);
    }

    private static IServiceProvider BuildProvider<T>(T clientHandler) where T : class
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => clientHandler);
        return services.BuildServiceProvider();
    }

    private static IpcEnvelope CreateEnvelope(ListChannelsIpcRequest request)
    {
        return new IpcEnvelope
        {
            Version = 1,
            Command = ClientCommand.ListChannels,
            CorrelationId = Guid.NewGuid(),
            Kind = IpcEnvelopeKind.Request,
            Payload = MessagePackSerializer.Serialize(request, s_options, TestContext.Current.CancellationToken)
        };
    }

    private static CompactPubKey CreatePubKey(byte fill)
    {
        var bytes = Enumerable.Repeat(fill, 33).ToArray();
        bytes[0] = 0x02;
        return new CompactPubKey(bytes);
    }
}