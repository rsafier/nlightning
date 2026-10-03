using MessagePack;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace NLightning.Daemon.Tests.Handlers;

using Application.Channels.Close;
using Daemon.Extensions;
using Daemon.Handlers;
using Daemon.Interfaces;
using Daemon.Ipc.Interfaces;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.ValueObjects;
using Domain.Client.Constants;
using Domain.Client.Enums;
using Domain.Client.Exceptions;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Domain.Protocol.Interfaces;
using Infrastructure.Bitcoin.Wallet.Interfaces;
using Transport.Ipc.MessagePack;
using Transport.Ipc.Requests;
using Transport.Ipc.Responses;

/// <summary>
/// The CloseChannel IPC command (ClientCommand 13, BOLT2 plan N10-T3): the client handler's mapping to
/// <see cref="IChannelCloseService"/>, the wire DTOs and the node composition.
/// </summary>
public class CloseChannelClientHandlerTests
{
    private static readonly ChannelId s_channelId = new(Enumerable.Repeat((byte)0x21, 32).ToArray());
    private static readonly TxId s_txId = new(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());

    [Fact]
    public async Task Given_Request_When_Handled_Then_ServiceGetsTheOptionsAndResultIsMapped()
    {
        // Arrange
        var service = new Mock<IChannelCloseService>();
        ChannelCloseRequest? seen = null;
        service.Setup(s => s.CloseChannelAsync(s_channelId, It.IsAny<ChannelCloseRequest>(),
                                               It.IsAny<CancellationToken>()))
               .Callback((ChannelId _, ChannelCloseRequest r, CancellationToken _) => seen = r)
               .ReturnsAsync(new ChannelCloseResult(s_channelId, ChannelState.Closing, s_txId));
        var handler = new CloseChannelClientHandler(service.Object);

        // Act
        var response = await handler.HandleAsync(new CloseChannelClientRequest(s_channelId)
        {
            FeeRatePerKw = 5_000,
            NoFeeRange = true,
            WaitSeconds = 12
        }, TestContext.Current.CancellationToken);

        // Assert
        Assert.NotNull(seen);
        Assert.Equal(5_000U, seen.FeeRatePerKw);
        Assert.False(seen.SendFeeRange);
        Assert.Equal(TimeSpan.FromSeconds(12), seen.WaitFor);
        Assert.Equal(ChannelState.Closing, response.State);
        Assert.Equal(s_txId, response.ClosingTxId);
    }

    [Fact]
    public async Task Given_NoWait_When_Handled_Then_DefaultWait()
    {
        // Arrange
        var service = new Mock<IChannelCloseService>();
        ChannelCloseRequest? seen = null;
        service.Setup(s => s.CloseChannelAsync(s_channelId, It.IsAny<ChannelCloseRequest>(),
                                               It.IsAny<CancellationToken>()))
               .Callback((ChannelId _, ChannelCloseRequest r, CancellationToken _) => seen = r)
               .ReturnsAsync(new ChannelCloseResult(s_channelId, ChannelState.ShuttingDown, null));

        // Act
        await new CloseChannelClientHandler(service.Object)
           .HandleAsync(new CloseChannelClientRequest(s_channelId), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(TimeSpan.FromSeconds(CloseChannelClientHandler.DefaultWaitSeconds), seen!.WaitFor);
        Assert.True(seen.SendFeeRange);
        Assert.Null(seen.FeeRatePerKw);
    }

    [Theory]
    [InlineData(typeof(KeyNotFoundException), ErrorCodes.InvalidChannel)]
    [InlineData(typeof(InvalidOperationException), ErrorCodes.InvalidOperation)]
    public async Task Given_ServiceRefuses_When_Handled_Then_ClientErrorCode(Type exceptionType, string code)
    {
        // Arrange
        var service = new Mock<IChannelCloseService>();
        service.Setup(s => s.CloseChannelAsync(It.IsAny<ChannelId>(), It.IsAny<ChannelCloseRequest>(),
                                               It.IsAny<CancellationToken>()))
               .ThrowsAsync((Exception)Activator.CreateInstance(exceptionType, "refused")!);

        // Act
        var exception = await Assert.ThrowsAsync<ClientException>(
                            () => new CloseChannelClientHandler(service.Object)
                                 .HandleAsync(new CloseChannelClientRequest(s_channelId),
                                              TestContext.Current.CancellationToken));

        // Assert
        Assert.Equal(code, exception.ErrorCode);
    }

    [Theory]
    [InlineData(301U, null)]
    [InlineData(null, 0U)]
    public async Task Given_InvalidArguments_When_Handled_Then_InvalidOperation(uint? wait, uint? feerate)
    {
        // Arrange
        var service = new Mock<IChannelCloseService>(MockBehavior.Strict);

        // Act
        var exception = await Assert.ThrowsAsync<ClientException>(
                            () => new CloseChannelClientHandler(service.Object)
                                 .HandleAsync(new CloseChannelClientRequest(s_channelId)
                                 {
                                     WaitSeconds = wait,
                                     FeeRatePerKw = feerate
                                 }, TestContext.Current.CancellationToken));

        // Assert
        Assert.Equal(ErrorCodes.InvalidOperation, exception.ErrorCode);
    }

    [Fact]
    public void Given_IpcDtos_When_RoundTripped_Then_EveryFieldIsPreserved()
    {
        // Arrange
        var options = NLightningMessagePackOptions.Options;
        var request = new CloseChannelIpcRequest
        {
            ChannelId = s_channelId,
            FeeRatePerKw = 7_500,
            NoFeeRange = true,
            WaitSeconds = 45
        };
        var response = CloseChannelIpcResponse.FromClientResponse(
            new CloseChannelClientResponse(s_channelId, ChannelState.Closing, s_txId));

        // Act
        var ct = TestContext.Current.CancellationToken;
        var request2 = MessagePackSerializer.Deserialize<CloseChannelIpcRequest>(
            MessagePackSerializer.Serialize(request, options, ct), options, ct);
        var response2 = MessagePackSerializer.Deserialize<CloseChannelIpcResponse>(
            MessagePackSerializer.Serialize(response, options, ct), options, ct);

        // Assert
        var clientRequest = request2.ToClientRequest();
        Assert.Equal(s_channelId, clientRequest.ChannelId);
        Assert.Equal(7_500U, clientRequest.FeeRatePerKw);
        Assert.True(clientRequest.NoFeeRange);
        Assert.Equal(45U, clientRequest.WaitSeconds);
        Assert.Equal(s_channelId, response2.ChannelId);
        Assert.Equal(ChannelState.Closing, response2.State);
        // Display order: the txid bytes reversed
        Assert.Equal(Convert.ToHexString(Enumerable.Range(1, 32).Reverse().Select(i => (byte)i).ToArray())
                            .ToLowerInvariant(), response2.ClosingTxId);
    }

    [Fact]
    public void Given_NodeServices_When_Composed_Then_CloseChannelResolves()
    {
        // Arrange
        var configuration = new ConfigurationBuilder()
                           .AddInMemoryCollection(new Dictionary<string, string?>
                           {
                               ["Node:Network"] = "regtest",
                               ["Database:Provider"] = "Sqlite",
                               ["Database:ConnectionString"] = "Data Source=:memory:",
                               ["Node:Close:SendFeeRange"] = "false",
                               ["Node:Close:ConfirmationDepth"] = "3"
                           })
                           .Build();
        var services = new ServiceCollection();
        services.AddNltgNodeServices(configuration, new Mock<ISecureKeyManager>().Object);
        services.AddSingleton(new Mock<IBitcoinChainService>().Object);
        services.AddSingleton(new Mock<IBlockchainMonitor>().Object);
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using var scope = provider.CreateScope();

        // Act
        var commands = provider.GetServices<IIpcCommandHandler>().Select(h => h.Command).ToList();
        var handler = scope.ServiceProvider
                           .GetRequiredService<IClientCommandHandler<CloseChannelClientRequest,
                                CloseChannelClientResponse>>();
        var options = provider.GetRequiredService<IOptions<ChannelCloseOptions>>().Value;

        // Assert
        Assert.Contains(ClientCommand.CloseChannel, commands);
        Assert.Equal(commands.Count, commands.Distinct().Count());
        Assert.IsType<CloseChannelClientHandler>(handler);
        Assert.IsType<ChannelCloseService>(provider.GetRequiredService<IChannelCloseService>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<ChannelCloseCoordinator>());
        Assert.False(options.SendFeeRange);
        Assert.Equal(3U, options.ConfirmationDepth);
    }
}