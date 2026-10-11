using NLightning.Tests.Utils.Mocks;

namespace NLightning.Application.Tests.Node.PeerStorage;

using Domain.Node.Fencing;
using Domain.Protocol.Messages;
using Gossip.Sync;

/// <summary>
/// NL-1341: our <c>peer_storage</c> backup goes to a peer only when the node write fence allows it, so a fenced
/// instance never replaces the peer's copy with an older backup.
/// </summary>
public class PeerStorageWriteFenceTests
{
    [Fact]
    public async Task Given_ARefusingFence_When_APeerThatOffersStorageConnects_Then_OurBackupIsNotSent()
    {
        // Arrange
        var fence = new FakeNodeWriteFence { Refuse = true };
        using var context = new PeerStorageTestContext(writeFence: fence);
        var peer = new FakeGossipPeer(23);
        context.AddChannel(peer.PeerPubKey);

        // Act
        context.Service.OnPeerInitialized(peer);

        // Assert
        Assert.True(await peer.NothingSentWithinAsync(TimeSpan.FromMilliseconds(200)));
        Assert.Contains(NodeEffect.PeerSend, fence.Effects);
    }

    [Fact]
    public async Task Given_AFenceThatAllowsIt_When_APeerThatOffersStorageConnects_Then_OurBackupIsSent()
    {
        // Arrange
        var fence = new FakeNodeWriteFence();
        using var context = new PeerStorageTestContext(writeFence: fence);
        var peer = new FakeGossipPeer(24);
        context.AddChannel(peer.PeerPubKey);

        // Act
        context.Service.OnPeerInitialized(peer);

        // Assert
        await peer.NextAsync<PeerStorageMessage>();
        Assert.Equal([NodeEffect.PeerSend], fence.Effects);
    }
}