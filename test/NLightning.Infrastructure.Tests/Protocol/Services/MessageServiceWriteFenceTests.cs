using Microsoft.Extensions.Logging.Abstractions;
using NLightning.Tests.Utils.Mocks;

namespace NLightning.Infrastructure.Tests.Protocol.Services;

using Domain.Exceptions;
using Domain.Node.Fencing;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using Domain.Serialization.Interfaces;
using Domain.Transport;
using Infrastructure.Protocol.Services;

/// <summary>
/// NL-1343: the node write fence is asked in <see cref="MessageService.SendMessageAsync"/>, the one place every message
/// to a peer passes. Without a fence nothing changes (no await, the write as before); an allowing fence is asked once
/// per message; a refusal writes nothing, is raised (or thrown) as a <see cref="ConnectionException"/> carrying the
/// <see cref="NodeFencedException"/>, and every later send on that connection is refused without asking again.
/// </summary>
public class MessageServiceWriteFenceTests
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(5);

    private readonly Mock<ITransportService> _transport = new();
    private readonly List<IMessage> _written = [];

    public MessageServiceWriteFenceTests()
    {
        _transport.SetupGet(t => t.IsConnected).Returns(true);
        _transport.Setup(t => t.WriteMessageAsync(It.IsAny<IMessage>(), It.IsAny<CancellationToken>()))
                  .Callback<IMessage, CancellationToken>((m, _) =>
                   {
                       lock (_written)
                           _written.Add(m);
                   })
                  .Returns(Task.CompletedTask);
    }

    [Fact]
    public void Given_NoFence_When_AMessageIsSent_Then_ItIsWrittenSynchronously()
    {
        // Arrange
        using var service = CreateService(null);
        var ping = new PingMessage(new PingPayload());

        // Act
        var send = service.SendMessageAsync(ping, cancellationToken: TestContext.Current.CancellationToken);

        // Assert: unregistered = a null check, the send completes with the transport's write
        Assert.True(send.IsCompletedSuccessfully);
        Assert.Equal([ping], _written);
    }

    [Fact]
    public async Task Given_AnAllowingFence_When_MessagesAreSent_Then_EachIsCheckedOnceAndWritten()
    {
        // Arrange
        var fence = new FakeNodeWriteFence();
        using var service = CreateService(fence);
        var ping = new PingMessage(new PingPayload());
        var pong = new PongMessage(new PongPayload(4));

        // Act
        await service.SendMessageAsync(ping, cancellationToken: TestContext.Current.CancellationToken);
        await service.SendMessageAsync(pong, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal([ping, pong], _written);
        Assert.Equal([NodeEffect.PeerSend, NodeEffect.PeerSend], fence.Effects);
    }

    [Fact]
    public async Task Given_ARefusingFence_When_AMessageIsSent_Then_NothingIsWrittenAndTheRefusalIsRaised()
    {
        // Arrange
        var fence = new FakeNodeWriteFence { Refuse = true };
        using var service = CreateService(fence);
        var raised = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.OnExceptionRaised += (_, e) => raised.TrySetResult(e);

        // Act
        await service.SendMessageAsync(new PingMessage(new PingPayload()),
                                       cancellationToken: TestContext.Current.CancellationToken);
        var exception = await raised.Task.WaitAsync(s_timeout, TestContext.Current.CancellationToken);

        // Assert: a connection failure (it closes the connection) that carries the fence's refusal
        Assert.Empty(_written);
        Assert.IsType<ConnectionException>(exception);
        Assert.IsType<NodeFencedException>(exception.InnerException?.InnerException);
    }

    [Fact]
    public async Task Given_ARefusedSend_When_MoreAreSent_Then_NoneIsWrittenAndTheFenceIsNotAskedAgain()
    {
        // Arrange: the fence refuses once, then would allow (another instance took over: the connection must close)
        var fence = new FakeNodeWriteFence { Refuse = true };
        using var service = CreateService(fence);
        await service.SendMessageAsync(new PingMessage(new PingPayload()),
                                       cancellationToken: TestContext.Current.CancellationToken);
        fence.Refuse = false;

        // Act
        await service.SendMessageAsync(new PongMessage(new PongPayload(4)),
                                       cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(_written);
        Assert.Equal([NodeEffect.PeerSend], fence.Effects);
    }

    [Fact]
    public async Task Given_ARefusingFence_When_ASendAsksForTheException_Then_TheRefusalIsThrown()
    {
        // Arrange: our init is sent this way
        var fence = new FakeNodeWriteFence { Refuse = true };
        using var service = CreateService(fence);

        // Act
        var exception = await Assert.ThrowsAsync<ConnectionException>(
                            () => service.SendMessageAsync(new PingMessage(new PingPayload()), throwOnException: true,
                                                           TestContext.Current.CancellationToken));

        // Assert
        Assert.IsType<NodeFencedException>(exception.InnerException);
        Assert.Empty(_written);
    }

    private MessageService CreateService(INodeWriteFence? fence) =>
        new(NullLogger<IMessageService>.Instance, new Mock<IMessageSerializer>().Object, _transport.Object, null,
            fence);
}