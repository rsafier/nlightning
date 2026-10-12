using System.Buffers.Binary;
using Microsoft.Extensions.DependencyInjection;
using NBitcoin;

namespace NLightning.Application.Tests.Node.PeerStorage;

using Application.Node.PeerStorage;
using Domain.Bitcoin.Transactions.Outputs;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.Splicing;
using Domain.Channels.Splicing.Enums;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Node.PeerStorage;
using Domain.Persistence.Interfaces;
using Gossip.Sync;

/// <summary>
/// Peer backup blob version 2 (lane SP2-E, NL-478): each channel carries its current funding outpoint and our funding
/// key index, a splice changes the fingerprint (so the blob is sent again), and version 1 blobs are still read.
/// </summary>
public class PeerBackupBlobVersion2Tests
{
    private static readonly TxId s_fundingTxId = new(Enumerable.Repeat((byte)0xF1, 32).ToArray());
    private static readonly TxId s_spliceTxId = new(Enumerable.Repeat((byte)0xF2, 32).ToArray());

    [Fact]
    public async Task Given_AFundedChannel_When_ABlobIsBuiltAndRead_Then_ItsFundingOutpointAndKeyIndexComeBack()
    {
        // Arrange
        using var context = new PeerStorageTestContext();
        var channel = Funded(context.AddChannel(new FakeGossipPeer(1).PeerPubKey), s_fundingTxId, 1);

        // Act
        var blob = await context.BlobProvider.CreateBlobAsync(TestContext.Current.CancellationToken);
        var contents = await context.BlobProvider.TryReadBlobAsync(blob!.Blob, TestContext.Current.CancellationToken);

        // Assert: never spliced, so our funding key is the key set's: index 0
        Assert.Equal(PeerStorageConstants.MaxBlobLength, blob.Blob.Length);
        var entry = Assert.Single(contents!.Channels);
        Assert.Equal(new PeerBackupChannel(channel.ChannelId, channel.RemoteNodeId, s_fundingTxId, 1, 0), entry);
    }

    [Fact]
    public async Task Given_ATaprootChannel_When_ABlobIsBuiltAndRead_Then_ItIsMarkedSimpleTaproot()
    {
        // Arrange (NL-877 T5: flag bit 2 of the entry)
        using var context = new PeerStorageTestContext();
        var taproot = Funded(context.AddChannel(new FakeGossipPeer(4).PeerPubKey, simpleTaproot: true), s_fundingTxId,
                             0);
        Funded(context.AddChannel(new FakeGossipPeer(5).PeerPubKey), s_spliceTxId, 1);

        // Act
        var blob = await context.BlobProvider.CreateBlobAsync(TestContext.Current.CancellationToken);
        var contents = await context.BlobProvider.TryReadBlobAsync(blob!.Blob, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(2, contents!.Channels.Count);
        Assert.Equal(new PeerBackupChannel(taproot.ChannelId, taproot.RemoteNodeId, s_fundingTxId, 0, 0, true),
                     contents.Channels.Single(c => c.ChannelId == taproot.ChannelId));
        Assert.False(contents.Channels.Single(c => c.ChannelId != taproot.ChannelId).IsSimpleTaproot);
    }

    [Fact]
    public async Task Given_ASpliceLock_When_TheBlobIsBuiltAgain_Then_ItHoldsTheNewOutpointAndKeyIndexUnderANewFingerprint()
    {
        // Arrange: the stored funding row of the splice says key index 3
        var fundings = new Dictionary<ChannelId, FundingSet>();
        var channels = new List<ChannelModel>();
        using var context = new PeerStorageTestContext();
        var provider = CreateProvider(context, channels, fundings);
        var channel = Funded(PeerStorageTestContext.CreateChannel(new FakeGossipPeer(2).PeerPubKey, ChannelState.Open),
                             s_fundingTxId, 0);
        channels.Add(channel);
        var before = await provider.CreateBlobAsync(TestContext.Current.CancellationToken);

        // Act
        var rotated = new CompactPubKey(new Key().PubKey.ToBytes());
        channel.ReplaceFundingOutput(new FundingOutputInfo(LightningMoney.Satoshis(1_200_000), rotated,
                                                           channel.RemoteFundingPubKey!.Value, s_spliceTxId, 2));
        fundings[channel.ChannelId] = FundingSet.Single(
            new ChannelFunding(s_spliceTxId, 2, 1_200_000, rotated, channel.RemoteFundingPubKey!.Value, 3, 0, 0,
                               ChannelFundingKind.Splice, ChannelFundingStatus.Current));
        var after = await provider.CreateBlobAsync(TestContext.Current.CancellationToken);
        var contents = await provider.TryReadBlobAsync(after!.Blob, TestContext.Current.CancellationToken);

        // Assert
        Assert.NotEqual(before!.Fingerprint, after.Fingerprint);
        var entry = Assert.Single(contents!.Channels);
        Assert.Equal(s_spliceTxId, entry.FundingTxId);
        Assert.Equal((ushort)2, entry.FundingOutputIndex);
        Assert.Equal(3u, entry.LocalFundingKeyIndex);
        Assert.Equal(after.Fingerprint, contents.Fingerprint);
    }

    [Fact]
    public async Task Given_ARotatedKeyWithoutAStoredRow_When_ABlobIsBuilt_Then_TheKeyIndexIsLeftUnknown()
    {
        // Arrange: nothing says which index the rotated key has
        using var context = new PeerStorageTestContext();
        var channel = context.AddChannel(new FakeGossipPeer(3).PeerPubKey);
        channel.AddFundingOutput(new FundingOutputInfo(LightningMoney.Satoshis(1_000_000),
                                                       new CompactPubKey(new Key().PubKey.ToBytes()),
                                                       new CompactPubKey(new Key().PubKey.ToBytes()), s_fundingTxId,
                                                       0));

        // Act
        var blob = await context.BlobProvider.CreateBlobAsync(TestContext.Current.CancellationToken);
        var contents = await context.BlobProvider.TryReadBlobAsync(blob!.Blob, TestContext.Current.CancellationToken);

        // Assert
        var entry = Assert.Single(contents!.Channels);
        Assert.Equal(s_fundingTxId, entry.FundingTxId);
        Assert.Null(entry.LocalFundingKeyIndex);
    }

    [Fact]
    public async Task Given_AVersion1Blob_When_Read_Then_ItsChannelsComeBackWithoutFundingFields()
    {
        // Arrange: a blob written before version 2 (channel id and peer only)
        using var context = new PeerStorageTestContext();
        var channelId = new ChannelId(Enumerable.Repeat((byte)0x44, 32).ToArray());
        var peer = new FakeGossipPeer(4).PeerPubKey;
        var plaintext = new byte[context.Cipher.MaxPlaintextLength];
        "NLPB"u8.CopyTo(plaintext);
        plaintext[4] = 1;
        BinaryPrimitives.WriteUInt64BigEndian(plaintext.AsSpan(5), 1_790_000_000);
        BinaryPrimitives.WriteUInt16BigEndian(plaintext.AsSpan(13), 1);
        ((ReadOnlySpan<byte>)channelId).CopyTo(plaintext.AsSpan(15));
        ((ReadOnlySpan<byte>)peer).CopyTo(plaintext.AsSpan(15 + 32));
        var blob = context.Cipher.Encrypt(plaintext);

        // Act
        var contents = await context.BlobProvider.TryReadBlobAsync(blob, TestContext.Current.CancellationToken);

        // Assert
        Assert.NotNull(contents);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_790_000_000), contents.CreatedAt);
        Assert.Equal(new PeerBackupChannel(channelId, peer), Assert.Single(contents.Channels));
    }

    [Fact]
    public async Task Given_AnUnknownBlobVersion_When_Read_Then_Null()
    {
        // Arrange
        using var context = new PeerStorageTestContext();
        var plaintext = new byte[context.Cipher.MaxPlaintextLength];
        "NLPB"u8.CopyTo(plaintext);
        plaintext[4] = 3;

        // Act / Assert
        Assert.Null(await context.BlobProvider.TryReadBlobAsync(context.Cipher.Encrypt(plaintext),
                                                                TestContext.Current.CancellationToken));
    }

    private static ChannelModel Funded(ChannelModel channel, TxId txId, ushort index)
    {
        channel.AddFundingOutput(new FundingOutputInfo(LightningMoney.Satoshis(1_000_000),
                                                       channel.LocalKeySet.FundingCompactPubKey,
                                                       new CompactPubKey(new Key().PubKey.ToBytes()), txId, index));
        return channel;
    }

    /// <summary>A provider over <paramref name="channels"/> that reads the stored fundings from a unit of work.</summary>
    private static ChannelListPeerBackupBlobProvider CreateProvider(PeerStorageTestContext context,
                                                                    List<ChannelModel> channels,
                                                                    Dictionary<ChannelId, FundingSet> fundings)
    {
        var memory = new Mock<IChannelMemoryRepository>();
        memory.Setup(r => r.FindChannels(It.IsAny<Func<ChannelModel, bool>>()))
              .Returns((Func<ChannelModel, bool> predicate) => channels.Where(predicate).ToList());
        var fundingRepository = new Mock<IChannelFundingDbRepository>();
        fundingRepository.Setup(r => r.GetFundingSetAsync(It.IsAny<ChannelId>()))
                         .ReturnsAsync((ChannelId id) => fundings.GetValueOrDefault(id));
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.SetupGet(u => u.ChannelFundingDbRepository).Returns(fundingRepository.Object);
        var services = new ServiceCollection();
        services.AddScoped(_ => unitOfWork.Object);
        return new ChannelListPeerBackupBlobProvider(memory.Object, context.Cipher, context.Time,
                                                     services.BuildServiceProvider()
                                                             .GetRequiredService<IServiceScopeFactory>());
    }
}