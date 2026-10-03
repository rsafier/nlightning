using MessagePack;
using Microsoft.Extensions.Logging.Abstractions;

namespace NLightning.Daemon.Tests.Services.Ipc;

using Application.Node.Services;
using Daemon.Ipc.Interfaces;
using Daemon.Services.Ipc;
using Domain.Client.Constants;
using Domain.Client.Enums;
using Transport.Ipc;
using Transport.Ipc.MessagePack;

/// <summary>
/// NL-591: while the node drains for its shutdown the router refuses the commands that start something.
/// </summary>
public class IpcRequestRouterDrainTests
{
    private readonly NodeDrainState _drain = new();

    [Theory]
    [InlineData(ClientCommand.PayInvoice)]
    [InlineData(ClientCommand.OpenChannel)]
    [InlineData(ClientCommand.Keysend)]
    [InlineData(ClientCommand.SpliceIn)]
    [InlineData(ClientCommand.Withdraw)]
    public async Task Given_TheNodeDraining_When_ACommandThatStartsSomethingArrives_Then_RefusedBeforeItsHandler(
        ClientCommand command)
    {
        // Arrange
        _drain.TryBeginDrain();
        var handler = CreateHandler(command);
        var router = new IpcRequestRouter([handler.Object], NullLogger<IpcRequestRouter>.Instance, _drain);

        // Act
        var response = await router.RouteAsync(CreateEnvelope(command), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(IpcEnvelopeKind.Error, response.Kind);
        var error = MessagePackSerializer.Deserialize<IpcError>(response.Payload, NLightningMessagePackOptions.Options,
                                                                TestContext.Current.CancellationToken);
        Assert.Equal(ErrorCodes.InvalidOperation, error.Code);
        Assert.Contains("shutting down", error.Message);
        handler.Verify(h => h.HandleAsync(It.IsAny<IpcEnvelope>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(ClientCommand.ListChannels)]
    [InlineData(ClientCommand.CloseChannel)]
    [InlineData(ClientCommand.Shutdown)]
    public async Task Given_TheNodeDraining_When_AReadOrCloseCommandArrives_Then_Routed(ClientCommand command)
    {
        // Arrange
        _drain.TryBeginDrain();
        var handler = CreateHandler(command);
        var router = new IpcRequestRouter([handler.Object], NullLogger<IpcRequestRouter>.Instance, _drain);

        // Act
        var response = await router.RouteAsync(CreateEnvelope(command), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(IpcEnvelopeKind.Response, response.Kind);
    }

    [Fact]
    public async Task Given_NoDrain_When_PayInvoiceArrives_Then_Routed()
    {
        // Arrange
        var handler = CreateHandler(ClientCommand.PayInvoice);
        var router = new IpcRequestRouter([handler.Object], NullLogger<IpcRequestRouter>.Instance, _drain);

        // Act
        var response = await router.RouteAsync(CreateEnvelope(ClientCommand.PayInvoice),
                                               TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(IpcEnvelopeKind.Response, response.Kind);
    }

    private static Mock<IIpcCommandHandler> CreateHandler(ClientCommand command)
    {
        var handler = new Mock<IIpcCommandHandler>();
        handler.SetupGet(h => h.Command).Returns(command);
        handler.Setup(h => h.HandleAsync(It.IsAny<IpcEnvelope>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync((IpcEnvelope request, CancellationToken _) => new IpcEnvelope
               {
                   Version = request.Version,
                   Command = request.Command,
                   CorrelationId = request.CorrelationId,
                   Kind = IpcEnvelopeKind.Response,
                   Payload = []
               });
        return handler;
    }

    private static IpcEnvelope CreateEnvelope(ClientCommand command) => new()
    {
        Version = 1,
        Command = command,
        CorrelationId = Guid.NewGuid(),
        Kind = IpcEnvelopeKind.Request,
        Payload = []
    };
}