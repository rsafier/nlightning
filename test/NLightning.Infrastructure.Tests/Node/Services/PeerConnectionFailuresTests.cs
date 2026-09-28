using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace NLightning.Infrastructure.Tests.Node.Services;

using Domain.Crypto.ValueObjects;
using Domain.Exceptions;
using Domain.Node.Options;
using Domain.Protocol.Constants;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using Infrastructure.Exceptions;
using Infrastructure.Node.Services;

/// <summary>
/// NL-532: routine peer disconnections are not logged at Error level. The 24 h mainnet soak logged ACINQ closing the
/// stream (<see cref="EndOfStreamException"/> behind <see cref="ConnectionException"/>) and a missed <c>pong</c> as
/// errors; a remote close is Information, a ping timeout, reset or a condition we raised about the peer Warning, and
/// Error stays for our own failures.
/// </summary>
public class PeerConnectionFailuresTests
{
    private static readonly CompactPubKey s_peerPubKey =
        Convert.FromHexString("034f355bdcb7cc0af728ef3cceb9615d90684bb5b2ca5f859ab0f0b704075871aa");

    public static TheoryData<string, Exception, LogLevel> Failures => new()
    {
        {
            "the soak's ACINQ close", new ConnectionException("Error received from transportService",
                                                                new ConnectionException("Error reading response",
                                                                                        new EndOfStreamException())),
            LogLevel.Information
        },
        {
            "the read loop's end of stream", new ConnectionException("Error received from transportService",
                                                                       new PeerClosedConnectionException(
                                                                           "Peer closed the connection")),
            LogLevel.Information
        },
        { "the soak's missed pong", new PingTimeoutException("Pong message not received"), LogLevel.Warning },
        {
            "a reset", new ConnectionException("Error writing message",
                                               new IOException("reset",
                                                               new SocketException(
                                                                   (int)SocketError.ConnectionReset))),
            LogLevel.Warning
        },
        { "a handshake timeout", new ConnectionTimeoutException("Timeout while reading"), LogLevel.Warning },
        { "a peer that sent no init", new ConnectionException("Expected init as the first message"), LogLevel.Warning },
        { "a warning we raised", new WarningException("Incompatible features"), LogLevel.Warning },
        {
            "our bug behind a send", new ConnectionException("Failed to send message", new NullReferenceException()),
            LogLevel.Error
        },
        { "our bug", new InvalidOperationException("broken"), LogLevel.Error }
    };

    [Theory]
    [MemberData(nameof(Failures))]
    public void Given_AConnectionFailure_When_Classified_Then_ItsLogLevel(string name, Exception exception,
                                                                         LogLevel expected)
    {
        // Act
        var level = PeerConnectionFailures.GetLogLevel(exception);

        // Assert
        Assert.True(expected == level, $"{name}: {level}, expected {expected}");
        Assert.Equal(expected == LogLevel.Information, PeerConnectionFailures.IsClosedByPeer(exception));
    }

    [Fact]
    public void Given_ThePeerClosedTheStream_When_PeerServiceHandlesIt_Then_InformationAndNoError()
    {
        // Arrange
        var (communication, logger, peerService) = CreatePeerService();
        using var _ = peerService;
        var raised = new List<Exception>();
        peerService.OnExceptionRaised += (_, e) => raised.Add(e);
        var closed = new ConnectionException("Error received from transportService",
                                             new PeerClosedConnectionException("Peer closed the connection",
                                                                               new EndOfStreamException()));

        // Act
        communication.Raise(x => x.ExceptionRaised += null, communication.Object, closed);

        // Assert: still forwarded, logged at Information only
        Assert.Same(closed, Assert.Single(raised));
        VerifyLogged(logger, LogLevel.Information, Times.Once());
        VerifyLogged(logger, LogLevel.Warning, Times.Never());
        VerifyLogged(logger, LogLevel.Error, Times.Never());
    }

    [Fact]
    public void Given_APingTimeout_When_PeerServiceHandlesIt_Then_Warning()
    {
        // Arrange
        var (communication, logger, peerService) = CreatePeerService();
        using var _ = peerService;

        // Act
        communication.Raise(x => x.ExceptionRaised += null, communication.Object,
                            new PingTimeoutException("Pong message not received within network timeout."));

        // Assert
        VerifyLogged(logger, LogLevel.Warning, Times.Once());
        VerifyLogged(logger, LogLevel.Error, Times.Never());
    }

    [Fact]
    public void Given_OurOwnFailure_When_PeerServiceHandlesIt_Then_Error()
    {
        // Arrange
        var (communication, logger, peerService) = CreatePeerService();
        using var _ = peerService;

        // Act
        communication.Raise(x => x.ExceptionRaised += null, communication.Object,
                            new ConnectionException("Failed to send message", new NullReferenceException()));

        // Assert
        VerifyLogged(logger, LogLevel.Error, Times.Once());
    }

    [Fact]
    public void Given_ThePeersWarning_When_Received_Then_LoggedAsAWarningNotAnError()
    {
        // Arrange
        var (communication, logger, peerService) = CreatePeerService();
        using var _ = peerService;
        communication.Raise(x => x.MessageReceived += null, communication.Object,
                            new InitMessage(new InitPayload(new FeatureOptions
                            {
                                ChainHashes = [ChainConstants.Regtest]
                            }.GetNodeFeatures())));

        // Act
        communication.Raise(x => x.MessageReceived += null, communication.Object,
                            new WarningMessage(new ErrorPayload("slow down")));

        // Assert
        VerifyLogged(logger, LogLevel.Warning, Times.Once());
        VerifyLogged(logger, LogLevel.Error, Times.Never());
    }

    private static (Mock<Domain.Node.Interfaces.IPeerCommunicationService> Communication,
        Mock<ILogger<PeerService>> Logger, PeerService PeerService) CreatePeerService()
    {
        var communication = new Mock<Domain.Node.Interfaces.IPeerCommunicationService>();
        communication.SetupGet(x => x.PeerCompactPubKey).Returns(s_peerPubKey);
        communication.Setup(x => x.InitializeAsync(It.IsAny<TimeSpan>())).Returns(Task.CompletedTask);
        var logger = new Mock<ILogger<PeerService>>();
        logger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
        var peerService = new PeerService(communication.Object,
                                          new FeatureOptions { ChainHashes = [ChainConstants.Regtest] },
                                          logger.Object, TimeSpan.FromSeconds(1));
        return (communication, logger, peerService);
    }

    private static void VerifyLogged(Mock<ILogger<PeerService>> logger, LogLevel level, Times times) =>
        logger.Verify(l => l.Log(level, It.IsAny<EventId>(), It.IsAny<It.IsAnyType>(), It.IsAny<Exception?>(),
                                 It.IsAny<Func<It.IsAnyType, Exception?, string>>()), times);
}