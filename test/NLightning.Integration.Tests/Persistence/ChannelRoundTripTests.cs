namespace NLightning.Integration.Tests.Persistence;

using Domain.Bitcoin.Enums;
using Domain.Bitcoin.Transactions.Outputs;
using Domain.Bitcoin.ValueObjects;
using Domain.Bitcoin.Wallet.Models;
using Domain.Channels.Commitments;
using Domain.Channels.Enums;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Money;
using Domain.Protocol.Models;
using Infrastructure.Crypto.Hashes;
using Infrastructure.Repositories.Database.Bitcoin;
using Infrastructure.Repositories.Database.Channel;

/// <summary>
/// Saves a channel with every <see cref="ChannelModel"/> field set to a distinct value into SQLite (real migrations)
/// and checks that each one reloads unchanged (BOLT2 plan N1-T5).
/// </summary>
public class ChannelRoundTripTests
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

    private static readonly CompactPubKey s_key8 =
        Convert.FromHexString("03a6cb04c3ea0ec2c5b6ac9e8c0f5e7ba1ff0d0bba8e9d8e8b5f3c5f9e2c2ad0c6");

    private static readonly WalletAddressModel s_changeAddress =
        new(AddressType.P2Wpkh, 7, true, "bcrt1qw508d6qejxtdg4y5r3zarvary0c5xw7kygt080");

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Given_ChannelWithEveryFieldSet_When_Reloaded_Then_EveryFieldIsEqual(bool isInitiator)
    {
        // Arrange
        await using var db = await SqliteDbTestContext.CreateAsync(TestContext.Current.CancellationToken);
        await AddChangeAddressAsync(db);
        var channel = CreateFullChannel(isInitiator);

        // Act
        var reloaded = await SaveAndReloadAsync(db, channel);

        // Assert
        AssertChannelsEqual(channel, reloaded);
    }

    [Fact]
    public async Task Given_AnnouncedChannel_When_AnnouncementStateResetAndUpdated_Then_ItReloadsCleared()
    {
        // Arrange (a reorg moved the short channel id: the old signatures are useless, BOLT 7 plan §8.6)
        await using var db = await SqliteDbTestContext.CreateAsync(TestContext.Current.CancellationToken);
        await AddChangeAddressAsync(db);
        var channel = CreateFullChannel(true);
        await SaveAndReloadAsync(db, channel);
        channel.ResetAnnouncementSignatures();

        // Act
        var reloaded = await UpdateAndReloadAsync(db, channel);

        // Assert
        Assert.True(reloaded.AnnounceChannel);
        Assert.Null(reloaded.RemoteAnnouncementSignatures);
        Assert.Null(reloaded.LocalAnnouncementSignaturesSentAt);
    }

    [Fact]
    public async Task Given_PrivateChannel_When_Reloaded_Then_ItIsNotAnnounced()
    {
        // Arrange
        await using var db = await SqliteDbTestContext.CreateAsync(TestContext.Current.CancellationToken);

        // Act
        var reloaded = await SaveAndReloadAsync(db, SqliteDbTestContext.CreateChannel(false));

        // Assert
        Assert.False(reloaded.AnnounceChannel);
        Assert.Null(reloaded.RemoteAnnouncementSignatures);
        Assert.Null(reloaded.LocalAnnouncementSignaturesSentAt);
    }

    [Fact]
    public async Task Given_BalanceWithOneMsatRemainder_When_Reloaded_Then_RemainderSurvives()
    {
        // Arrange (NL-191: balances used to be stored as whole satoshis)
        await using var db = await SqliteDbTestContext.CreateAsync(TestContext.Current.CancellationToken);
        await AddChangeAddressAsync(db);
        var channel = CreateFullChannel(true, LightningMoney.MilliSatoshis(599_999_999),
                                        LightningMoney.MilliSatoshis(400_000_001));

        // Act
        var reloaded = await SaveAndReloadAsync(db, channel);

        // Assert
        Assert.Equal(599_999_999UL, reloaded.LocalBalance.MilliSatoshi);
        Assert.Equal(400_000_001UL, reloaded.RemoteBalance.MilliSatoshi);
    }

    [Fact]
    public async Task Given_ConfirmedChannel_When_Reloaded_Then_ShortChannelIdIsKept()
    {
        // Arrange (NL-225)
        await using var db = await SqliteDbTestContext.CreateAsync(TestContext.Current.CancellationToken);
        var channel = SqliteDbTestContext.CreateChannel(true);
        channel.ShortChannelId = new ShortChannelId(812_345, 1_234, 2);

        // Act
        var reloaded = await SaveAndReloadAsync(db, channel);

        // Assert
        Assert.Equal(new ShortChannelId(812_345, 1_234, 2), reloaded.ShortChannelId);
    }

    [Fact]
    public async Task Given_OnchainResolvingChannel_When_ReloadedAndMovedOn_Then_StatesAreKept()
    {
        // Arrange (BOLT 5 plan O1-T2: state 37 sits between Failed and Closed)
        await using var db = await SqliteDbTestContext.CreateAsync(TestContext.Current.CancellationToken);
        var channel = SqliteDbTestContext.CreateChannel(true, state: ChannelState.Failed);
        var failed = await SaveAndReloadAsync(db, channel);
        Assert.Equal(ChannelState.Failed, failed.State);

        // Act: its commitment confirms, then every output is resolved
        failed.UpdateState(ChannelState.OnchainResolving);
        var resolving = await UpdateAndReloadAsync(db, failed);
        var resolvingState = resolving.State;
        resolving.UpdateState(ChannelState.Closed);
        var closed = await UpdateAndReloadAsync(db, resolving);

        // Assert
        Assert.Equal(ChannelState.OnchainResolving, resolvingState);
        Assert.Equal(ChannelState.Closed, closed.State);
    }

    [Fact]
    public async Task Given_UnconfirmedChannel_When_Reloaded_Then_ShortChannelIdStaysUnset()
    {
        // Arrange
        await using var db = await SqliteDbTestContext.CreateAsync(TestContext.Current.CancellationToken);
        var channel = SqliteDbTestContext.CreateChannel(true, state: ChannelState.V1FundingSigned);

        // Act
        var reloaded = await SaveAndReloadAsync(db, channel);

        // Assert
        Assert.Null((byte[]?)reloaded.ShortChannelId);
    }

    [Fact]
    public async Task Given_PersistedChannel_When_ConfirmedThroughUpdate_Then_ShortChannelIdAndBalancesAreStored()
    {
        // Arrange: funding confirmation sets the scid on a channel that was saved without one
        await using var db = await SqliteDbTestContext.CreateAsync(TestContext.Current.CancellationToken);
        var channel = SqliteDbTestContext.CreateChannel(true, state: ChannelState.V1FundingSigned);
        await SaveAndReloadAsync(db, channel);
        channel.ShortChannelId = new ShortChannelId(700_000, 5, 1);

        // Act
        await using (var updateContext = db.CreateDbContext())
        {
            var repository = new ChannelDbRepository(updateContext, db.Sha256);
            await repository.UpdateAsync(channel);
            await updateContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var readContext = db.CreateDbContext();
        var reloaded = await new ChannelDbRepository(readContext, db.Sha256)
                          .GetByIdAsync(channel.ChannelId);

        // Assert
        Assert.NotNull(reloaded);
        Assert.Equal(new ShortChannelId(700_000, 5, 1), reloaded.ShortChannelId);
        Assert.Equal(channel.LocalBalance, reloaded.LocalBalance);
    }

    private static async Task<ChannelModel> SaveAndReloadAsync(SqliteDbTestContext db, ChannelModel channel)
    {
        await using (var writeContext = db.CreateDbContext())
        {
            var repository = new ChannelDbRepository(writeContext, db.Sha256);
            await repository.AddAsync(channel);
            await writeContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var readContext = db.CreateDbContext();
        var readRepository = new ChannelDbRepository(readContext, db.Sha256);
        return await readRepository.GetByIdAsync(channel.ChannelId)
            ?? throw new InvalidOperationException("Channel was not reloaded");
    }

    private static async Task<ChannelModel> UpdateAndReloadAsync(SqliteDbTestContext db, ChannelModel channel)
    {
        await using (var writeContext = db.CreateDbContext())
        {
            await new ChannelDbRepository(writeContext, db.Sha256).UpdateAsync(channel);
            await writeContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var readContext = db.CreateDbContext();
        return await new ChannelDbRepository(readContext, db.Sha256).GetByIdAsync(channel.ChannelId)
            ?? throw new InvalidOperationException("Channel was not reloaded");
    }

    private static async Task AddChangeAddressAsync(SqliteDbTestContext db)
    {
        await using var addressContext = db.CreateDbContext();
        new WalletAddressesDbRepository(addressContext).AddRange([s_changeAddress]);
        await addressContext.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static ChannelModel CreateFullChannel(bool isInitiator,
                                                  LightningMoney? localBalance = null,
                                                  LightningMoney? remoteBalance = null)
    {
        // Every per-side value differs between the sides and from every other field
        var local = new ChannelParty(LightningMoney.Satoshis(546), LightningMoney.Satoshis(10_001),
                                     LightningMoney.MilliSatoshis(1_001), 31, LightningMoney.MilliSatoshis(800_000_001),
                                     720, new BitcoinScript([0x00, 0x14, .. Enumerable.Repeat((byte)0x11, 20)]));
        var remote = new ChannelParty(LightningMoney.Satoshis(354), LightningMoney.Satoshis(10_002),
                                      LightningMoney.MilliSatoshis(1_002), 32,
                                      LightningMoney.MilliSatoshis(800_000_002), 144,
                                      new BitcoinScript([0x00, 0x14, .. Enumerable.Repeat((byte)0x22, 20)]));
        var channelParams = new ChannelParams(local, remote, LightningMoney.Satoshis(2_535), 6, true,
                                              FeatureSupport.Compulsory)
        {
            HasInferredParams = true,
            AnnounceChannel = true
        };

        var localKeySet = new ChannelKeySetModel(9, s_key1, s_key2, s_key3, s_key4, s_key5, s_key6, 281474976710650);
        var remoteKeySet = new ChannelKeySetModel(0, s_key7, s_key8, s_key6, s_key5, s_key4, s_key3, 281474976710651);

        // BOLT 3: the obscuring factor is SHA256(opener payment_basepoint || accepter payment_basepoint). The
        // commitment numbers are stored on their own (NL-188), so give each side a distinct one.
        const ulong localRevocationNumber = 4;
        var sha256 = new Sha256();
        var commitmentNumber = isInitiator
                                   ? new CommitmentNumber(s_key3, s_key6, sha256)
                                   : new CommitmentNumber(s_key6, s_key3, sha256);

        var fundingTxId = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
        var fundingOutput = new FundingOutputInfo(LightningMoney.Satoshis(1_000_000), s_key1, s_key7, fundingTxId, 3);

        var channelIdBytes = Enumerable.Range(100, 32).Select(i => (byte)i).ToArray();
        channelIdBytes[31] = isInitiator ? (byte)1 : (byte)2;
        var channelId = new ChannelId(channelIdBytes);

        var sentSignature = new CompactSignature(Enumerable.Repeat((byte)0x31, 64).ToArray());
        var receivedSignature = new CompactSignature(Enumerable.Repeat((byte)0x32, 64).ToArray());

        var channel = new ChannelModel(channelParams, channelId, commitmentNumber, fundingOutput, isInitiator, sentSignature,
                                receivedSignature, localBalance ?? LightningMoney.MilliSatoshis(600_000_123),
                                localKeySet, 11, localRevocationNumber,
                                remoteBalance ?? LightningMoney.MilliSatoshis(399_999_877), remoteKeySet, 13, s_key2,
                                3, ChannelState.Open, ChannelVersion.V1,
                                localCommitmentNumber: localRevocationNumber + 1, remoteCommitmentNumber: 3)
        {
            ShortChannelId = new ShortChannelId(812_345, 678, 3),
            FundingCreatedAtBlockHeight = 812_340,
            ChangeAddress = s_changeAddress,
            LocalAliases = [new ShortChannelId(16_000_001, 1, 1), new ShortChannelId(16_000_002, 2, 2)],
            RemoteAlias = new ShortChannelId(16_000_003, 3, 3)
        };

        // The commitment state (N5): HTLCs both ways, one locked in, and an unacked commitment_signed of ours. The
        // params carry the inferred-limits flag, as the reload does
        var driver = new CommitmentDanceDriver(channelId, CommitmentParams.FromChannel(channel),
                                               channel.LocalBalance.MilliSatoshi, channel.RemoteBalance.MilliSatoshi,
                                               usCommitmentNumber: channel.LocalCommitmentNumber,
                                               peerCommitmentNumber: channel.RemoteCommitmentNumber);
        Assert.NotNull(driver.TryUsAdd(4_000_000));
        Assert.NotNull(driver.TryUsCommit());
        Assert.NotNull(driver.TryDeliverRevokeToUs());
        Assert.NotNull(driver.TryPeerCommit());
        Assert.NotNull(driver.TryPeerAdd());
        Assert.NotNull(driver.TryUsAdd(3_000_000));
        Assert.NotNull(driver.TryUsCommit());
        channel.UpdateCommitments(driver.Us, new ChannelStateExtras
        {
            SentCommitDiff = CommitmentDanceDriver.DiffFor(driver.Us.RemoteNextCommit!.Commit.Number),
            LastSent = LastSentCommitmentMessage.CommitmentSigned
        });
        channel.MarkErrorSent(new byte[] { 0x00, 0x11, 0x42 });
        channel.MarkDataLossDetected();
        // Close state (N10, migration AddShutdownState)
        channel.SetLocalShutdownScript(Convert.FromHexString("0014" + new string('a', 40)));
        channel.SetRemoteShutdownScript(Convert.FromHexString("0020" + new string('b', 64)));
        channel.SetClosingTransaction(new SignedTransaction(new TxId(Enumerable.Repeat((byte)0x5c, 32).ToArray()),
                                                            [0x02, 0x00, 0x00, 0x00, 0x01]));
        // Announcement state (BOLT 7 plan G1, migration AddGossipGraph)
        channel.SetRemoteAnnouncementSignatures(
            new ChannelAnnouncementSignatures(new CompactSignature(Enumerable.Repeat((byte)0x41, 64).ToArray()),
                                              new CompactSignature(Enumerable.Repeat((byte)0x42, 64).ToArray())));
        channel.MarkAnnouncementSignaturesSent(
            new DateTimeOffset(2026, 9, 26, 12, 34, 56, TimeSpan.FromHours(2)).AddTicks(7_891));

        return channel;
    }

    private static void AssertChannelsEqual(ChannelModel expected, ChannelModel actual)
    {
        // Parameters, both sides (NL-194) and shared
        Assert.Equal(expected.ChannelParams, actual.ChannelParams);
        Assert.Equal(expected.LocalUpfrontShutdownScript, actual.LocalUpfrontShutdownScript);
        Assert.Equal(expected.RemoteUpfrontShutdownScript, actual.RemoteUpfrontShutdownScript);

        // Identity and funding
        Assert.Equal(expected.ChannelId, actual.ChannelId);
        Assert.Equal(expected.ShortChannelId, actual.ShortChannelId);
        Assert.Equal(expected.FundingCreatedAtBlockHeight, actual.FundingCreatedAtBlockHeight);
        Assert.NotNull(actual.FundingOutput);
        Assert.Equal(expected.FundingOutput!.Amount, actual.FundingOutput.Amount);
        Assert.Equal(expected.FundingOutput.LocalFundingPubKey, actual.FundingOutput.LocalFundingPubKey);
        Assert.Equal(expected.FundingOutput.RemoteFundingPubKey, actual.FundingOutput.RemoteFundingPubKey);
        Assert.Equal(expected.FundingOutput.TransactionId, actual.FundingOutput.TransactionId);
        Assert.Equal(expected.FundingOutput.Index, actual.FundingOutput.Index);
        Assert.Equal(expected.IsInitiator, actual.IsInitiator);
        Assert.Equal(expected.RemoteNodeId, actual.RemoteNodeId);
        Assert.Equal(expected.State, actual.State);
        Assert.Equal(expected.Version, actual.Version);
        Assert.NotNull(actual.ChangeAddress);
        Assert.Equal(expected.ChangeAddress!.AddressType, actual.ChangeAddress.AddressType);
        Assert.Equal(expected.ChangeAddress.Index, actual.ChangeAddress.Index);
        Assert.Equal(expected.ChangeAddress.IsChange, actual.ChangeAddress.IsChange);
        Assert.Equal(expected.ChangeAddress.Address, actual.ChangeAddress.Address);

        // Commitment number, obscuring factor and signatures
        Assert.NotNull(actual.CommitmentNumber);
        Assert.Equal(expected.LocalCommitmentNumber, actual.LocalCommitmentNumber);
        Assert.Equal(expected.RemoteCommitmentNumber, actual.RemoteCommitmentNumber);
        Assert.Equal(expected.CommitmentNumber!.ObscuringFactor, actual.CommitmentNumber.ObscuringFactor);
        Assert.Equal(expected.LastSentSignature, actual.LastSentSignature);
        Assert.Equal(expected.LastReceivedSignature, actual.LastReceivedSignature);

        // Local side
        Assert.Equal(expected.LocalBalance, actual.LocalBalance);
        AssertKeySetsEqual(expected.LocalKeySet, actual.LocalKeySet);
        Assert.Equal(expected.LocalNextHtlcId, actual.LocalNextHtlcId);
        Assert.Equal(expected.LocalRevocationNumber, actual.LocalRevocationNumber);
        Assert.Equal(expected.LocalAliases!.OrderBy(a => a.ToString()), actual.LocalAliases!.OrderBy(a => a.ToString()));

        // Remote side
        Assert.Equal(expected.RemoteAlias, actual.RemoteAlias);
        Assert.Equal(expected.RemoteBalance, actual.RemoteBalance);
        Assert.NotNull(actual.RemoteKeySet);
        AssertKeySetsEqual(expected.RemoteKeySet!, actual.RemoteKeySet);
        Assert.Equal(expected.RemoteNextHtlcId, actual.RemoteNextHtlcId);
        Assert.Equal(expected.RemoteRevocationNumber, actual.RemoteRevocationNumber);

        // Commitment state (N5-T1/T2) and failure flags
        Assert.NotNull(actual.Commitments);
        CommitmentsAssert.Equal(expected.Commitments!, actual.Commitments);
        Assert.Equal(expected.SentCommitDiff?.ToArray(), actual.SentCommitDiff?.ToArray());
        Assert.Equal(expected.LastSentCommitmentMessage, actual.LastSentCommitmentMessage);
        Assert.Equal(expected.ErrorSent?.ToArray(), actual.ErrorSent?.ToArray());
        Assert.Equal(expected.DataLossDetected, actual.DataLossDetected);

        // Close state (N10)
        Assert.Equal(expected.LocalShutdownScript, actual.LocalShutdownScript);
        Assert.Equal(expected.RemoteShutdownScript, actual.RemoteShutdownScript);
        Assert.NotNull(actual.ClosingTransaction);
        Assert.Equal(expected.ClosingTransaction!.TxId, actual.ClosingTransaction.TxId);
        Assert.Equal(expected.ClosingTransaction.RawTxBytes, actual.ClosingTransaction.RawTxBytes);

        // Announcement state (BOLT 7 plan G1)
        Assert.Equal(expected.AnnounceChannel, actual.AnnounceChannel);
        Assert.Equal(expected.RemoteAnnouncementSignatures, actual.RemoteAnnouncementSignatures);
        Assert.Equal(expected.LocalAnnouncementSignaturesSentAt, actual.LocalAnnouncementSignaturesSentAt);
    }

    private static void AssertKeySetsEqual(ChannelKeySetModel expected, ChannelKeySetModel actual)
    {
        Assert.Equal(expected.KeyIndex, actual.KeyIndex);
        Assert.Equal(expected.FundingCompactPubKey, actual.FundingCompactPubKey);
        Assert.Equal(expected.RevocationCompactBasepoint, actual.RevocationCompactBasepoint);
        Assert.Equal(expected.PaymentCompactBasepoint, actual.PaymentCompactBasepoint);
        Assert.Equal(expected.DelayedPaymentCompactBasepoint, actual.DelayedPaymentCompactBasepoint);
        Assert.Equal(expected.HtlcCompactBasepoint, actual.HtlcCompactBasepoint);
        Assert.Equal(expected.CurrentPerCommitmentCompactPoint, actual.CurrentPerCommitmentCompactPoint);
        Assert.Equal(expected.CurrentPerCommitmentIndex, actual.CurrentPerCommitmentIndex);
    }
}