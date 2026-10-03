using System.Collections.Concurrent;
using MessagePack;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace NLightning.Daemon.Tests.Ipc.Handlers;

using Daemon.Interfaces;
using Daemon.Ipc.Handlers;
using Domain.Channels.ValueObjects;
using Domain.Client.Constants;
using Domain.Client.Enums;
using Domain.Client.Exceptions;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Domain.Exceptions;
using Domain.Money;
using Transport.Ipc;
using Transport.Ipc.MessagePack;
using Transport.Ipc.Requests;

/// <summary>
/// NL-883 (the 2026-10-03 Mutinynet test): a refused or invalid operator request is logged as one Warning line without
/// a stack trace by every IPC handler; a fault of ours keeps an Error with the exception.
/// </summary>
public class IpcRequestLogTests
{
    private static readonly MessagePackSerializerOptions s_options = NLightningMessagePackOptions.Options;
    private static readonly ChannelId s_channelId = new(Enumerable.Repeat((byte)0x3d, 32).ToArray());

    public IpcRequestLogTests()
    {
        MessagePackSerializer.DefaultOptions = s_options;
    }

    [Fact]
    public async Task Given_AnOpenChannelRefusal_When_Handled_Then_OneWarningLineWithoutTheException()
    {
        // Arrange: the live refusal that was logged at Error with its stack
        var logger = new CapturingLogger<OpenChannelIpcHandler>();
        var handler = new OpenChannelIpcHandler(logger, OpenChannelThrowing(
                                                    new ClientException(ErrorCodes.InvalidOperation,
                                                                        "02c8 sells no rate for 400000 sat")));

        // Act
        await handler.HandleAsync(OpenChannelEnvelope(), TestContext.Current.CancellationToken);

        // Assert
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Null(entry.Exception);
        Assert.Equal("OpenChannel refused: 02c8 sells no rate for 400000 sat", entry.Message);
    }

    [Fact]
    public async Task Given_AnOpenChannelThePeerFails_When_Handled_Then_OneWarningLineWithoutTheException()
    {
        // Arrange: the peer answers open_channel with an error
        var logger = new CapturingLogger<OpenChannelIpcHandler>();
        var handler = new OpenChannelIpcHandler(logger, OpenChannelThrowing(
                                                    new ChannelErrorException("the peer refused the channel")));

        // Act
        await handler.HandleAsync(OpenChannelEnvelope(), TestContext.Current.CancellationToken);

        // Assert
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Null(entry.Exception);
        Assert.Contains("the peer refused the channel", entry.Message);
    }

    [Fact]
    public async Task Given_AnOpenChannelFault_When_Handled_Then_AnErrorWithTheException()
    {
        // Arrange
        var fault = new NullReferenceException("a bug");
        var logger = new CapturingLogger<OpenChannelIpcHandler>();
        var handler = new OpenChannelIpcHandler(logger, OpenChannelThrowing(fault));

        // Act
        await handler.HandleAsync(OpenChannelEnvelope(), TestContext.Current.CancellationToken);

        // Assert
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Same(fault, entry.Exception);
    }

    [Fact]
    public async Task Given_ACloseChannelRefusal_When_Handled_Then_OneWarningLineWithoutTheException()
    {
        // Arrange: the lease guard's refusal, wrapped by the client handler with its cause
        var logger = new CapturingLogger<CloseChannelIpcHandler>();
        var refusal = new ClientException(ErrorCodes.InvalidOperation, "Channel carries a lease",
                                          new InvalidOperationException("Channel carries a lease"));
        var handler = new CloseChannelIpcHandler(logger, CloseChannelThrowing(refusal));

        // Act
        var response = await handler.HandleAsync(CloseChannelEnvelope(), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(IpcEnvelopeKind.Error, response.Kind);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Null(entry.Exception);
        Assert.Equal("CloseChannel refused: Channel carries a lease", entry.Message);
    }

    [Fact]
    public async Task Given_AServerErrorClientException_When_Handled_Then_AnErrorWithTheException()
    {
        // Arrange: a client handler's wrapped fault (PayInvoice's "unexpected error" shape)
        var logger = new CapturingLogger<CloseChannelIpcHandler>();
        var fault = new ClientException(ErrorCodes.ServerError, "unexpected", new InvalidOperationException("bug"));
        var handler = new CloseChannelIpcHandler(logger, CloseChannelThrowing(fault));

        // Act
        await handler.HandleAsync(CloseChannelEnvelope(), TestContext.Current.CancellationToken);

        // Assert
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Same(fault, entry.Exception);
    }

    private static IServiceProvider OpenChannelThrowing(Exception exception)
    {
        var clientHandler = new Mock<IClientCommandHandler<OpenChannelClientRequest, OpenChannelClientResponse>>();
        clientHandler.Setup(x => x.HandleAsync(It.IsAny<OpenChannelClientRequest>(), It.IsAny<CancellationToken>()))
                     .ThrowsAsync(exception);
        return new ServiceCollection().AddScoped(_ => clientHandler.Object).BuildServiceProvider();
    }

    private static IServiceProvider CloseChannelThrowing(Exception exception)
    {
        var clientHandler = new Mock<IClientCommandHandler<CloseChannelClientRequest, CloseChannelClientResponse>>();
        clientHandler.Setup(x => x.HandleAsync(It.IsAny<CloseChannelClientRequest>(), It.IsAny<CancellationToken>()))
                     .ThrowsAsync(exception);
        return new ServiceCollection().AddScoped(_ => clientHandler.Object).BuildServiceProvider();
    }

    private static IpcEnvelope OpenChannelEnvelope() =>
        Envelope(ClientCommand.OpenChannel,
                 MessagePackSerializer.Serialize(new OpenChannelIpcRequest
                 {
                     NodeInfo = "peer@127.0.0.1:9735",
                     Amount = LightningMoney.Satoshis(100_000)
                 }, s_options, TestContext.Current.CancellationToken));

    private static IpcEnvelope CloseChannelEnvelope() =>
        Envelope(ClientCommand.CloseChannel,
                 MessagePackSerializer.Serialize(new CloseChannelIpcRequest { ChannelId = s_channelId }, s_options,
                                                 TestContext.Current.CancellationToken));

    private static IpcEnvelope Envelope(ClientCommand command, byte[] payload) => new()
    {
        Version = 1,
        Command = command,
        CorrelationId = Guid.NewGuid(),
        Kind = IpcEnvelopeKind.Request,
        Payload = payload
    };

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        private readonly ConcurrentQueue<(LogLevel Level, string Message, Exception? Exception)> _entries = new();

        public IReadOnlyList<(LogLevel Level, string Message, Exception? Exception)> Entries => _entries.ToArray();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                                Func<TState, Exception?, string> formatter) =>
            _entries.Enqueue((logLevel, formatter(state, exception), exception));
    }
}