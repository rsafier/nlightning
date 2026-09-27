namespace NLightning.Application.Tests.Node.PeerStorage;

using Domain.Channels.Enums;
using Domain.Node.PeerStorage;
using Gossip.Sync;

public class ChannelListPeerBackupBlobProviderTests
{
    [Fact]
    public async Task Given_OpenChannels_When_ABlobIsBuiltAndRead_Then_EveryChannelAndPeerComesBack()
    {
        // Arrange
        using var context = new PeerStorageTestContext();
        var a = context.AddChannel(new FakeGossipPeer(1).PeerPubKey);
        var b = context.AddChannel(new FakeGossipPeer(2).PeerPubKey, ChannelState.OnchainResolving);

        // Act
        var blob = await context.BlobProvider.CreateBlobAsync(TestContext.Current.CancellationToken);
        var contents = await context.BlobProvider.TryReadBlobAsync(blob!.Blob, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(PeerStorageConstants.MaxBlobLength, blob.Blob.Length);
        Assert.NotNull(contents);
        Assert.Equal(context.Time.GetUtcNow().ToUnixTimeSeconds(), contents.CreatedAt.ToUnixTimeSeconds());
        Assert.Equal(2, contents.Channels.Count);
        Assert.Contains(new PeerBackupChannel(a.ChannelId, a.RemoteNodeId), contents.Channels);
        Assert.Contains(new PeerBackupChannel(b.ChannelId, b.RemoteNodeId), contents.Channels);
    }

    [Theory]
    [InlineData(ChannelState.V1Opening)]
    [InlineData(ChannelState.V1FundingCreated)]
    [InlineData(ChannelState.Closed)]
    [InlineData(ChannelState.Stale)]
    public async Task Given_OnlyChannelsNotWorthABackup_When_ABlobIsBuilt_Then_Null(ChannelState state)
    {
        // Arrange
        using var context = new PeerStorageTestContext();
        context.AddChannel(new FakeGossipPeer(3).PeerPubKey, state);

        // Act
        var blob = await context.BlobProvider.CreateBlobAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Null(blob);
    }

    [Fact]
    public async Task Given_TheSameChannels_When_BuiltLater_Then_TheBlobDiffersButTheFingerprintDoesNot()
    {
        // Arrange
        using var context = new PeerStorageTestContext();
        context.AddChannel(new FakeGossipPeer(4).PeerPubKey);
        var first = await context.BlobProvider.CreateBlobAsync(TestContext.Current.CancellationToken);

        // Act
        context.Time.Advance(TimeSpan.FromHours(1));
        var second = await context.BlobProvider.CreateBlobAsync(TestContext.Current.CancellationToken);
        context.AddChannel(new FakeGossipPeer(5).PeerPubKey);
        var third = await context.BlobProvider.CreateBlobAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.NotEqual(first!.Blob, second!.Blob);
        Assert.Equal(first.Fingerprint, second.Fingerprint);
        Assert.NotEqual(first.Fingerprint, third!.Fingerprint);
    }

    [Fact]
    public async Task Given_AnAlteredOrForeignBlob_When_Read_Then_Null()
    {
        // Arrange
        using var context = new PeerStorageTestContext(nodeSeed: 1);
        using var other = new PeerStorageTestContext(nodeSeed: 2);
        context.AddChannel(new FakeGossipPeer(6).PeerPubKey);
        other.AddChannel(new FakeGossipPeer(6).PeerPubKey);
        var ours = (await context.BlobProvider.CreateBlobAsync(TestContext.Current.CancellationToken))!.Blob;
        var theirs = (await other.BlobProvider.CreateBlobAsync(TestContext.Current.CancellationToken))!.Blob;
        ours[100] ^= 0x80;

        // Act & Assert
        Assert.Null(await context.BlobProvider.TryReadBlobAsync(ours, TestContext.Current.CancellationToken));
        Assert.Null(await context.BlobProvider.TryReadBlobAsync(theirs, TestContext.Current.CancellationToken));
        Assert.Null(await context.BlobProvider.TryReadBlobAsync(new byte[] { 1, 2, 3 },
                                                                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Given_MoreChannelsThanFit_When_ABlobIsBuilt_Then_ItHoldsAsManyAsFit()
    {
        // Arrange
        using var context = new PeerStorageTestContext();
        var peer = new FakeGossipPeer(7).PeerPubKey;
        for (var i = 0; i < context.BlobProvider.MaxChannels + 3; i++)
            context.Channels.Add(PeerStorageTestContext.CreateChannel(peer, ChannelState.Open));

        // Act
        var blob = await context.BlobProvider.CreateBlobAsync(TestContext.Current.CancellationToken);
        var contents = await context.BlobProvider.TryReadBlobAsync(blob!.Blob, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(PeerStorageConstants.MaxBlobLength, blob.Blob.Length);
        Assert.Equal(context.BlobProvider.MaxChannels, contents!.Channels.Count);
    }
}