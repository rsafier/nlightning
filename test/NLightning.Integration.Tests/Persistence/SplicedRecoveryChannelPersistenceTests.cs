using NBitcoin;

namespace NLightning.Integration.Tests.Persistence;

using Application.Channels.Backup;
using Application.Channels.Backup.Models;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Splicing;
using Domain.Channels.Splicing.Enums;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Infrastructure.Repositories.Database.Channel;

/// <summary>
/// A recovery channel restored at a splice (lane SP2-E, NL-478) reloads through SQLite with the splice's funding keys
/// and our rotated key index (the channel model, the signer's data and the funding set), and a stored recovery channel
/// moved to a splice by the lock's save reloads at the new funding.
/// </summary>
public class SplicedRecoveryChannelPersistenceTests
{
    private static readonly CompactPubKey[] s_keys =
        Enumerable.Range(1, 12).Select(i => new CompactPubKey(new Key(Enumerable.Repeat((byte)i, 32).ToArray())
                                                                  .PubKey.ToBytes())).ToArray();

    private static readonly TxId s_fundingTxId = new(Enumerable.Repeat((byte)0xA1, 32).ToArray());
    private static readonly TxId s_spliceTxId = new(Enumerable.Repeat((byte)0xA2, 32).ToArray());

    [Theory]
    [InlineData(0U)]
    [InlineData(2U)]
    public async Task Given_UnknownRecoveryFundingKeys_When_Reloaded_Then_FundingAndSignerKeepTheUnknownFlag(uint keyIndex)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await SqliteDbTestContext.CreateAsync(ct);
        var entry = CreateEntry() with { FundingKeysUnknown = true, LocalFundingKeyIndex = keyIndex };
        var channel = RecoveryChannels.Create(entry, Basepoints(), entry.LocalFundingPubKey, db.Sha256);
        await using (var writeContext = db.CreateDbContext())
        {
            await new ChannelDbRepository(writeContext, db.Sha256).AddAsync(channel);
            await new ChannelFundingDbRepository(writeContext).UpsertAsync(entry.ChannelId, RecoveryChannels.CreateFundings(entry).Current);
            await writeContext.SaveChangesAsync(ct);
        }
        await using var readContext = db.CreateDbContext();
        var reloaded = await new ChannelDbRepository(readContext, db.Sha256).GetByIdAsync(entry.ChannelId);
        var signingInfo = await new ChannelSigningInfoDbRepository(readContext).GetAsync(entry.ChannelId);
        var funding = await new ChannelFundingDbRepository(readContext).GetFundingSetAsync(entry.ChannelId);
        Assert.True(reloaded!.FundingKeysUnknown);
        Assert.True(signingInfo!.Value.FundingKeysUnknown);
        Assert.True(signingInfo.Value.DataLossDetected);
        Assert.True(funding!.Current.FundingKeysUnknown);
    }

    [Fact]
    public async Task Given_ARecoveryChannelAtASplice_When_Reloaded_Then_ItKeepsTheSplicesKeysAndOurKeyIndex()
    {
        // Arrange: a post-splice backup (funding key index 2) with another splice pending
        var ct = TestContext.Current.CancellationToken;
        await using var db = await SqliteDbTestContext.CreateAsync(ct);
        var entry = CreateEntry() with
        {
            FundingTxId = s_spliceTxId,
            FundingOutputIndex = 1,
            CapacitySat = 1_200_000,
            LocalFundingKeyIndex = 2,
            LocalFundingPubKey = s_keys[9],
            RemoteFundingPubKey = s_keys[10],
            ShortChannelId = new ShortChannelId(600, 2, 1),
            PendingFundings =
            [
                new ChannelBackupFunding(new TxId(Enumerable.Repeat((byte)0xA3, 32).ToArray()), 0, 1_150_000, 3,
                                         s_keys[11], s_keys[10])
            ]
        };
        var channel = RecoveryChannels.Create(entry, Basepoints(), entry.LocalFundingPubKey, db.Sha256);
        var (current, pending) = RecoveryChannels.CreateFundings(entry);

        // Act: the restore's save (channel, then its funding rows on the same unit of work)
        await using (var writeContext = db.CreateDbContext())
        {
            await new ChannelDbRepository(writeContext, db.Sha256).AddAsync(channel);
            var fundings = new ChannelFundingDbRepository(writeContext);
            await fundings.UpsertAsync(entry.ChannelId, current);
            foreach (var funding in pending)
                await fundings.UpsertAsync(entry.ChannelId, funding);
            await writeContext.SaveChangesAsync(ct);
        }

        await using var readContext = db.CreateDbContext();
        var reloaded = await new ChannelDbRepository(readContext, db.Sha256).GetByIdAsync(entry.ChannelId);
        var signingInfo = await new ChannelSigningInfoDbRepository(readContext).GetAsync(entry.ChannelId);
        var set = await new ChannelFundingDbRepository(readContext).GetFundingSetAsync(entry.ChannelId);

        // Assert
        Assert.NotNull(reloaded);
        Assert.True(RecoveryChannels.IsRecoveryChannel(reloaded));
        Assert.Equal(s_spliceTxId, reloaded.FundingOutput!.TransactionId);
        Assert.Equal((ushort)1, reloaded.FundingOutput.Index);
        Assert.Equal(s_keys[9], reloaded.LocalFundingPubKey);
        Assert.Equal(s_keys[10], reloaded.RemoteFundingPubKey);
        Assert.Equal(s_keys[0], reloaded.LocalKeySet.FundingCompactPubKey);
        Assert.NotNull(signingInfo);
        Assert.Equal(2u, signingInfo.Value.LocalFundingKeyIndex);
        Assert.Equal(s_keys[9], signingInfo.Value.LocalFundingPubKey);
        Assert.True(signingInfo.Value.DataLossDetected);
        Assert.NotNull(set);
        Assert.Equal(ChannelFundingKind.Splice, set.Current.Kind);
        Assert.Equal(2u, set.Current.LocalFundingKeyIndex);
        var stillPending = Assert.Single(set.Pending);
        Assert.Equal(3u, stillPending.LocalFundingKeyIndex);
        Assert.Equal(entry.PendingFundings[0].FundingTxId, stillPending.FundingTxId);
    }

    [Fact]
    public async Task Given_ARecoveryChannelAtItsOriginalFunding_When_ItsSpliceIsLocked_Then_ItReloadsAtTheSplice()
    {
        // Arrange: stored at the original funding (key index 0)
        var ct = TestContext.Current.CancellationToken;
        await using var db = await SqliteDbTestContext.CreateAsync(ct);
        var entry = CreateEntry();
        var channel = RecoveryChannels.Create(entry, Basepoints(), db.Sha256);
        await using (var writeContext = db.CreateDbContext())
        {
            await new ChannelDbRepository(writeContext, db.Sha256).AddAsync(channel);
            await writeContext.SaveChangesAsync(ct);
        }

        // Act: what the restore's move saves (the splice pending, then locked, the old funding replaced)
        var locked = new ChannelFunding(s_spliceTxId, 1, 1_200_000, s_keys[9], s_keys[10], 1, 0, 0,
                                        ChannelFundingKind.Splice, ChannelFundingStatus.Current,
                                        ShortChannelId: new ShortChannelId(600, 2, 1));
        await using (var moveContext = db.CreateDbContext())
        {
            var fundings = new ChannelFundingDbRepository(moveContext);
            await fundings.UpsertAsync(entry.ChannelId, locked with { Status = ChannelFundingStatus.Pending });
            var set = await fundings.GetFundingSetAsync(entry.ChannelId);
            await fundings.ApplyLockAsync(entry.ChannelId, locked,
                                          [set!.Current with { Status = ChannelFundingStatus.Replaced }]);
            await moveContext.SaveChangesAsync(ct);
        }

        await using var readContext = db.CreateDbContext();
        var reloaded = await new ChannelDbRepository(readContext, db.Sha256).GetByIdAsync(entry.ChannelId);
        var signingInfo = await new ChannelSigningInfoDbRepository(readContext).GetAsync(entry.ChannelId);

        // Assert
        Assert.NotNull(reloaded);
        Assert.True(RecoveryChannels.IsRecoveryChannel(reloaded));
        Assert.Equal(s_spliceTxId, reloaded.FundingOutput!.TransactionId);
        Assert.Equal(s_keys[9], reloaded.LocalFundingPubKey);
        Assert.Equal(new ShortChannelId(600, 2, 1), reloaded.ShortChannelId);
        Assert.Equal(1u, signingInfo!.Value.LocalFundingKeyIndex);
    }

    private static ChannelBasepoints Basepoints() => new(s_keys[0], s_keys[1], s_keys[2], s_keys[3], s_keys[4]);

    private static ChannelBackupEntry CreateEntry()
    {
        var party = new ChannelBackupParty(354, 10_000, 1, 483, 990_000_000, 144);
        return new ChannelBackupEntry
        {
            ChannelId = new ChannelId(Enumerable.Repeat((byte)0x6B, 32).ToArray()),
            RemoteNodeId = s_keys[5],
            Addresses = [new ChannelBackupAddress("IPv4", "127.0.0.1", 9735)],
            FundingTxId = s_fundingTxId,
            FundingOutputIndex = 0,
            CapacitySat = 1_000_000,
            FundingHeight = 500,
            ShortChannelId = new ShortChannelId(500, 1, 0),
            IsInitiator = true,
            OptionAnchorOutputs = true,
            UseScidAlias = FeatureSupport.No,
            MinimumDepth = 3,
            ChannelType = [],
            KeyIndex = 4,
            LocalFundingPubKey = s_keys[0],
            LocalPaymentBasepoint = s_keys[2],
            RemoteFundingPubKey = s_keys[6],
            RemoteRevocationBasepoint = s_keys[7],
            RemotePaymentBasepoint = s_keys[8],
            RemoteDelayedPaymentBasepoint = s_keys[5],
            RemoteHtlcBasepoint = s_keys[6],
            Local = party,
            Remote = party
        };
    }
}