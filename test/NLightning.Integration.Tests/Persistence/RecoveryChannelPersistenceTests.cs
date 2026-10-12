namespace NLightning.Integration.Tests.Persistence;

using Application.Channels.Backup;
using Application.Channels.Backup.Models;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Enums;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Protocol.Models;
using Infrastructure.Repositories.Database.Channel;

/// <summary>
/// A recovery channel (restored from a static channel backup) survives a reload through SQLite with every property
/// that keeps it safe: still a recovery channel, data loss for the signer, the peer's "current point" unknown, the
/// original obscuring factor.
/// </summary>
public class RecoveryChannelPersistenceTests
{
    // Valid compressed secp256k1 points (BOLT 3 Appendix C keys and basepoints)
    private static readonly CompactPubKey s_key1 =
        Convert.FromHexString("023da092f6980e58d2c037173180e9a465476026ee50f96695963e8efe436f54eb");

    private static readonly CompactPubKey s_key2 =
        Convert.FromHexString("030e9f7b623d2ccc7c9bd44d66d5ce21ce504c0acf6385a132cec6d3c39fa711c1");

    private static readonly CompactPubKey s_key3 =
        Convert.FromHexString("034f355bdcb7cc0af728ef3cceb9615d90684bb5b2ca5f859ab0f0b704075871aa");

    private static readonly CompactPubKey s_key4 =
        Convert.FromHexString("032c0b7cf95324a07d05398b240174dc0c2be444d96b159aa6c7f7b1e668680991");

    private static readonly CompactPubKey s_key5 =
        Convert.FromHexString("02466d7fcae563e5cb09a0d1870bb580344804617879a14949cf22285f1bae3f27");

    private static readonly CompactPubKey s_key6 =
        Convert.FromHexString("0212a140cd0c6539d07cd08dfe09984dec3251ea808b892efeac3ede9402bf2b19");

    private static readonly CompactPubKey s_key7 =
        Convert.FromHexString("0394854aa6eab5b2a8122cc726e9dded053a2184d88256816826d6231c068d4a5b");

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Given_RecoveryChannel_When_Reloaded_Then_StillARecoveryChannelWithDataLoss(bool anchors)
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var db = await SqliteDbTestContext.CreateAsync(ct);
        var entry = CreateEntry(anchors);
        var basepoints = new ChannelBasepoints(s_key1, s_key2, s_key3, s_key4, s_key5);
        var channel = RecoveryChannels.Create(entry, basepoints, db.Sha256);
        channel.MarkErrorSent(new byte[] { 0x00, 0x11, 0x22 });

        // Act
        await using (var writeContext = db.CreateDbContext())
        {
            await new ChannelDbRepository(writeContext, db.Sha256).AddAsync(channel);
            await writeContext.SaveChangesAsync(ct);
        }

        await using var readContext = db.CreateDbContext();
        var reloaded = await new ChannelDbRepository(readContext, db.Sha256).GetByIdAsync(entry.ChannelId);
        var signingInfo = await new ChannelSigningInfoDbRepository(readContext).GetAsync(entry.ChannelId);

        // Assert
        Assert.NotNull(reloaded);
        Assert.True(RecoveryChannels.IsRecoveryChannel(reloaded));
        Assert.Equal(ChannelState.Failed, reloaded.State);
        Assert.Equal(anchors, reloaded.ChannelParams.OptionAnchorOutputs);
        Assert.Equal(channel.CommitmentNumber!.ObscuringFactor, reloaded.CommitmentNumber!.ObscuringFactor);
        Assert.Equal(RecoveryChannels.UnknownPerCommitmentIndex, reloaded.RemoteKeySet!.CurrentPerCommitmentIndex);
        Assert.NotEqual(PerCommitmentIndex.From(reloaded.RemoteCommitmentNumber),
                        reloaded.RemoteKeySet.CurrentPerCommitmentIndex);
        Assert.Equal(s_key3, reloaded.LocalKeySet.PaymentCompactBasepoint);
        Assert.Equal(entry.FundingTxId, reloaded.FundingOutput!.TransactionId);
        Assert.Equal(entry.ShortChannelId, reloaded.ShortChannelId);
        Assert.Equal(channel.ErrorSent!.Value.ToArray(), reloaded.ErrorSent!.Value.ToArray());
        Assert.NotNull(signingInfo);
        Assert.True(signingInfo.Value.DataLossDetected);
    }

    private static ChannelBackupEntry CreateEntry(bool anchors)
    {
        var party = new ChannelBackupParty(354, 10_000, 1, 483, 990_000_000, 144);
        return new ChannelBackupEntry
        {
            ChannelId = new ChannelId(Enumerable.Repeat((byte)0x5A, 32).ToArray()),
            RemoteNodeId = s_key6,
            Addresses = [new ChannelBackupAddress("IPv4", "127.0.0.1", 9735)],
            FundingTxId = new TxId(Enumerable.Repeat((byte)0xA5, 32).ToArray()),
            FundingOutputIndex = 1,
            CapacitySat = 1_000_000,
            FundingHeight = 432,
            ShortChannelId = new ShortChannelId(432, 1, 1),
            IsInitiator = true,
            OptionAnchorOutputs = anchors,
            UseScidAlias = FeatureSupport.No,
            MinimumDepth = 3,
            ChannelType = [],
            KeyIndex = 4,
            LocalFundingPubKey = s_key1,
            LocalPaymentBasepoint = s_key3,
            RemoteFundingPubKey = s_key7,
            RemoteRevocationBasepoint = s_key2,
            RemotePaymentBasepoint = s_key4,
            RemoteDelayedPaymentBasepoint = s_key5,
            RemoteHtlcBasepoint = s_key6,
            Local = party,
            Remote = party
        };
    }
}