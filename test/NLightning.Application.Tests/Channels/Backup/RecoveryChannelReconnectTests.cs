using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Tests.Channels.Backup;

using Application.Channels.Backup;
using Application.Channels.Managers;
using Application.Channels.Services;
using Application.Protocol.Factories;
using Domain.Bitcoin.Interfaces;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Node.Options;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using Domain.Serialization.Interfaces;
using Infrastructure.Bitcoin.Wallet.Interfaces;
using Infrastructure.Crypto.Hashes;

/// <summary>
/// The <see cref="ChannelManager"/> hook of recovery channels: every connection to the peer sends the BOLT 2 "we lost
/// data" <c>channel_reestablish</c> first, then the stored error; an ordinary failed channel only gets its error.
/// </summary>
public class RecoveryChannelReconnectTests
{
    private readonly BackupTestData _data = new();
    private readonly List<ChannelModel> _channels = [];
    private readonly Mock<IChannelMemoryRepository> _memory = new();
    private readonly Mock<IMessageSerializer> _serializer = new();
    private readonly Mock<ILightningSigner> _signer = new();
    private readonly List<(CompactPubKey Peer, IChannelMessage Message)> _raised = [];

    public RecoveryChannelReconnectTests()
    {
        _memory.Setup(m => m.FindChannels(It.IsAny<Func<ChannelModel, bool>>()))
               .Returns((Func<ChannelModel, bool> predicate) => _channels.Where(predicate).ToList());
        _signer.Setup(s => s.GetPerCommitmentPoint(It.IsAny<ChannelId>(), 0))
               .Returns(BackupTestData.Key(0x02, 50, 0));
    }

    [Fact]
    public async Task Given_ARecoveryChannel_When_ThePeerConnects_Then_DataLossReestablishSentAndTheErrorReturned()
    {
        // Arrange
        var recovery = CreateRecoveryChannel(1);
        recovery.MarkErrorSent(new byte[] { 0x00, 0x11 });
        _channels.Add(recovery);
        var storedError = new ErrorMessage(new ErrorPayload(recovery.ChannelId, RecoveryChannels.PeerErrorMessage));
        _serializer.Setup(s => s.DeserializeMessageAsync(It.IsAny<Stream>())).ReturnsAsync(storedError);
        var manager = CreateManager();

        // Act
        var errors = await manager.OnPeerConnectedAsync(recovery.RemoteNodeId);

        // Assert: the reestablish is raised (outbox) before the error is returned (queued after it)
        Assert.Same(storedError, Assert.Single(errors));
        var (peer, message) = Assert.Single(_raised);
        Assert.Equal(recovery.RemoteNodeId, peer);
        var reestablish = Assert.IsType<ChannelReestablishMessage>(message);
        Assert.Equal(recovery.ChannelId, reestablish.Payload.ChannelId);
        Assert.Equal(0UL, reestablish.Payload.NextCommitmentNumber);
        Assert.Equal(0UL, reestablish.Payload.NextRevocationNumber);
        Assert.Equal(new byte[32], reestablish.Payload.YourLastPerCommitmentSecret.ToArray());
        Assert.Equal(BackupTestData.Key(0x02, 50, 0), reestablish.Payload.MyCurrentPerCommitmentPoint);
    }

    [Fact]
    public async Task Given_TheSignerDoesNotKnowTheChannel_When_ThePeerConnects_Then_OurPaymentBasepointIsThePoint()
    {
        // Arrange
        var recovery = CreateRecoveryChannel(1);
        _channels.Add(recovery);
        _signer.Setup(s => s.GetPerCommitmentPoint(It.IsAny<ChannelId>(), It.IsAny<ulong>()))
               .Throws(new InvalidOperationException("unknown channel"));
        var manager = CreateManager();

        // Act
        await manager.OnPeerConnectedAsync(recovery.RemoteNodeId);

        // Assert
        var reestablish = Assert.IsType<ChannelReestablishMessage>(Assert.Single(_raised).Message);
        Assert.Equal(recovery.LocalKeySet.PaymentCompactBasepoint, reestablish.Payload.MyCurrentPerCommitmentPoint);
        Assert.Equal(0UL, reestablish.Payload.NextCommitmentNumber);
    }

    [Fact]
    public async Task Given_AFailedChannelWithoutDataLoss_When_ThePeerConnects_Then_OnlyItsError()
    {
        // Arrange (regression guard: B2-RE-05 is unchanged for every channel that is not a recovery one)
        var failed = _data.AddChannel(2, state: ChannelState.Failed);
        _channels.Add(failed);
        var manager = CreateManager();

        // Act
        var errors = await manager.OnPeerConnectedAsync(failed.RemoteNodeId);

        // Assert
        Assert.Single(errors);
        Assert.Empty(_raised);
    }

    private ChannelModel CreateRecoveryChannel(byte tag)
    {
        var entry = ChannelBackupService.CreateEntry(_data.AddChannel(tag), null);
        using var sha256 = new Sha256();
        return RecoveryChannels.Create(entry, _data.Signer.Object.GetChannelBasepoints(entry.KeyIndex), sha256);
    }

    private ChannelManager CreateManager()
    {
        var services = new ServiceCollection();
        services.AddSingleton(_serializer.Object);
        services.AddSingleton<IMessageFactory>(new MessageFactory(Options.Create(new NodeOptions())));
        services.AddScoped<ChannelDomainEventQueue>();
        services.AddScoped(_ => new Mock<IUnitOfWork>().Object);

        var manager = new ChannelManager(new Mock<IBlockchainMonitor>().Object, new ChannelLockProvider(),
                                         _memory.Object, NullLogger<ChannelManager>.Instance, _signer.Object,
                                         services.BuildServiceProvider());
        manager.OnResponseMessageReady += (_, args) => _raised.Add((args.PeerPubKey, args.ResponseMessage));
        return manager;
    }
}