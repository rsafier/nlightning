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

    [Fact]
    public async Task Given_ARefusalWrappingAnInvalidOperationFromOutsideOurCode_When_Handled_Then_AnErrorWithTheStack()
    {
        // Arrange (NL-894): a client handler wraps every InvalidOperationException as a refusal, but this one is a bug
        // (LINQ's Single() on an empty sequence inside a service)
        var bug = Thrown(() => Array.Empty<int>().Single());
        var logger = new CapturingLogger<CloseChannelIpcHandler>();
        var wrapped = new ClientException(ErrorCodes.InvalidOperation, bug.Message, bug);
        var handler = new CloseChannelIpcHandler(logger, CloseChannelThrowing(wrapped));

        // Act
        await handler.HandleAsync(CloseChannelEnvelope(), TestContext.Current.CancellationToken);

        // Assert
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Same(wrapped, entry.Exception);
        Assert.NotNull(entry.Exception!.InnerException!.StackTrace);
    }

    [Fact]
    public async Task Given_ARefusalThrownByOurCode_When_HandledWithDebugOn_Then_OneWarningLineAndTheExceptionAtDebug()
    {
        // Arrange (NL-894): a service's own refusal stays one Warning line; its exception is recoverable at Debug
        var refusal = Thrown(() => RefuseLikeAService());
        var logger = new CapturingLogger<CloseChannelIpcHandler> { MinLevel = LogLevel.Debug };
        var wrapped = new ClientException(ErrorCodes.InvalidOperation, refusal.Message, refusal);
        var handler = new CloseChannelIpcHandler(logger, CloseChannelThrowing(wrapped));

        // Act
        await handler.HandleAsync(CloseChannelEnvelope(), TestContext.Current.CancellationToken);

        // Assert
        var warning = Assert.Single(logger.Entries, e => e.Level >= LogLevel.Information);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Null(warning.Exception);
        Assert.Equal("CloseChannel refused: Channel carries a lease", warning.Message);
        var debug = Assert.Single(logger.Entries, e => e.Level == LogLevel.Debug);
        Assert.Same(refusal, debug.Exception);
    }

    [Fact]
    public void Given_Exceptions_When_AskedWhetherFaults_Then_BugsAndForeignThrowsAreFaultsOursAreNot()
    {
        // Arrange
        var linq = Thrown(() => Array.Empty<int>().First());
        var ours = Thrown(() => RefuseLikeAService());

        // Act & Assert (NL-894)
        Assert.True(IpcRequestLog.IsFault(linq));
        Assert.True(IpcRequestLog.IsFault(new ObjectDisposedException("context")));
        Assert.True(IpcRequestLog.IsFault(new NullReferenceException()));
        Assert.False(IpcRequestLog.IsFault(ours));
        Assert.False(IpcRequestLog.IsFault(new ChannelErrorException("the peer refused")));
        Assert.False(IpcRequestLog.IsFault(new InvalidOperationException("never thrown")));
        Assert.False(IpcRequestLog.IsFault(null));
    }

    private static object RefuseLikeAService() => throw new InvalidOperationException("Channel carries a lease");

    private static Exception Thrown(Func<object> action)
    {
        try
        {
            action();
        }
        catch (Exception e)
        {
            return e;
        }

        throw new InvalidOperationException("nothing was thrown");
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

        public LogLevel MinLevel { get; init; } = LogLevel.Information;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= MinLevel;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                                Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
                _entries.Enqueue((logLevel, formatter(state, exception), exception));
        }
    }
}