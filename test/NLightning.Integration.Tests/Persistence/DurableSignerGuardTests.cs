using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;
using NBitcoin.Crypto;
using NLightning.Tests.Utils.Channels;

namespace NLightning.Integration.Tests.Persistence;

using Docker.Mock;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.Transactions.Outputs;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Enums;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Exceptions;
using Domain.Money;
using Domain.Node.Options;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Models;
using Infrastructure.Bitcoin;
using Infrastructure.Bitcoin.Builders;
using Infrastructure.Bitcoin.Crypto.Functions;
using Infrastructure.Bitcoin.Services;
using Infrastructure.Bitcoin.Signers;
using Infrastructure.Crypto.Hashes;
using Infrastructure.Repositories;
using Infrastructure.Repositories.Database.Channel;

/// <summary>
/// NL-1345: the local signer's durable guard. Two signers built as the daemon builds them (two service providers, as two
/// processes) over one SQLite database: what the first released or signed is refused to the second even when the
/// channel rows it loads lag (a restore, a standby starting from the same database), the guard is raised before a
/// secret or signature leaves the signer, and a database from before the guard is seeded from its channel rows.
/// </summary>
public sealed class DurableSignerGuardTests : IDisposable
{
    private const uint FundingSats = 1_000_000;
    private const ulong RowsCommitmentNumber = 5;

    private readonly SqliteTestDatabase _database = new();
    private readonly FakeSecureKeyManager _keyManager = new();
    private readonly Key _remoteFundingKey = new();

    public void Dispose()
    {
        _database.Dispose();
    }

    [Fact]
    public async Task Given_ProcessAReleasedAndSigned_When_ProcessBStartsFromStaleRows_Then_BNeverSignsBelow()
    {
        // Arrange: process A advances to 6, releases secret 5 and signs the peer's commitment 7; the channel rows
        // stay at 5 (B reads a database whose channel rows lag)
        var channel = await PersistChannelAsync();
        await using (var processA = BuildNode())
        {
            var signerA = processA.GetRequiredService<ILightningSigner>();
            signerA.AdvanceLocalCommitment(channel.ChannelId, RowsCommitmentNumber + 1);
            _ = signerA.RevealPerCommitmentSecret(channel.ChannelId, RowsCommitmentNumber);
            _ = signerA.SignChannelTransaction(channel.ChannelId, CreateCommitment(channel, 7));
        }

        await using var processB = BuildNode();
        var signerB = processB.GetRequiredService<ILightningSigner>();
        var revoked = CreateCommitment(channel, RowsCommitmentNumber);

        // Act & Assert: the revoked commitment 5 is never signed for broadcast (its secret is out)
        var broadcast = Assert.Throws<SignerException>(() => signerB.SignLocalCommitmentForBroadcast(
                                                           channel.ChannelId, RowsCommitmentNumber, revoked,
                                                           SignAsRemote(channel, revoked)));
        Assert.Contains("revoked", broadcast.Message);

        // The peer's commitment is never signed below 7; 7 again (a retransmission) and 8 are
        Assert.Throws<SignerException>(() => signerB.SignChannelTransaction(channel.ChannelId,
                                                                           CreateCommitment(channel, 6)));
        Assert.Null(Record.Exception(() => signerB.SignChannelTransaction(channel.ChannelId,
                                                                          CreateCommitment(channel, 7))));
        Assert.Null(Record.Exception(() => signerB.SignChannelTransaction(channel.ChannelId,
                                                                          CreateCommitment(channel, 8))));

        // The local commitment never goes back below 6
        Assert.Throws<SignerException>(() => signerB.AdvanceLocalCommitment(channel.ChannelId,
                                                                           RowsCommitmentNumber));
    }

    [Fact]
    public async Task Given_TwoRunningProcesses_When_OneSignsForBroadcast_Then_TheOtherRefusesItsNextSignature()
    {
        // Arrange: both signers are up (a standby) and have loaded the channel
        var channel = await PersistChannelAsync();
        await using var processA = BuildNode();
        await using var processB = BuildNode();
        var signerA = processA.GetRequiredService<ILightningSigner>();
        var signerB = processB.GetRequiredService<ILightningSigner>();
        _ = signerA.SignChannelTransaction(channel.ChannelId, CreateCommitment(channel, 6));
        var current = CreateCommitment(channel, RowsCommitmentNumber);

        // Act: B signs our current commitment for broadcast (S1)
        _ = signerB.SignLocalCommitmentForBroadcast(channel.ChannelId, RowsCommitmentNumber, current,
                                                    SignAsRemote(channel, current));

        // Assert: A reads the durable mark before its next guarded operation and refuses
        Assert.Throws<SignerException>(() => signerA.SignChannelTransaction(channel.ChannelId,
                                                                           CreateCommitment(channel, 7)));
        Assert.Throws<SignerException>(() => signerA.AdvanceLocalCommitment(channel.ChannelId,
                                                                           RowsCommitmentNumber + 1));
        Assert.True(signerA.TryGetBroadcastSignedCommitment(channel.ChannelId, out var marked));
        Assert.Equal(RowsCommitmentNumber, marked);
    }

    [Fact]
    public async Task Given_DataLossInProcessA_When_ProcessBStartsFromRowsWithoutIt_Then_BRefusesToSign()
    {
        // Arrange
        var channel = await PersistChannelAsync();
        await using (var processA = BuildNode())
            processA.GetRequiredService<ILightningSigner>().MarkDataLoss(channel.ChannelId);

        await using var processB = BuildNode();
        var signerB = processB.GetRequiredService<ILightningSigner>();

        // Act & Assert (I12 survives rows that never recorded it)
        Assert.Throws<SignerException>(() => signerB.SignChannelTransaction(channel.ChannelId,
                                                                           CreateCommitment(channel, 6)));
    }

    [Fact]
    public async Task Given_ACrashAfterTheReleaseBeforeTheChannelSave_When_TheNodeRestarts_Then_TheReleaseIsKept()
    {
        // Arrange: the secret of 5 left the signer, then the process died before anything else was saved
        var channel = await PersistChannelAsync();
        Secret released;
        await using (var crashed = BuildNode())
        {
            var signer = crashed.GetRequiredService<ILightningSigner>();
            signer.AdvanceLocalCommitment(channel.ChannelId, RowsCommitmentNumber + 1);
            released = signer.RevealPerCommitmentSecret(channel.ChannelId, RowsCommitmentNumber);
        }

        // Act
        await using var restarted = BuildNode();
        var signerAfter = restarted.GetRequiredService<ILightningSigner>();
        var revoked = CreateCommitment(channel, RowsCommitmentNumber);

        // Assert: the restarted node never broadcasts the revoked commitment, and releases the same secret again
        Assert.Throws<SignerException>(() => signerAfter.SignLocalCommitmentForBroadcast(
                                           channel.ChannelId, RowsCommitmentNumber, revoked,
                                           SignAsRemote(channel, revoked)));
        Assert.Equal(released, signerAfter.RevealPerCommitmentSecret(channel.ChannelId, RowsCommitmentNumber));
        var guard = await ReadGuardAsync(channel.ChannelId);
        Assert.Equal(RowsCommitmentNumber, guard!.Value.RevokedCommitmentNumber);
        Assert.Equal(RowsCommitmentNumber + 1, guard.Value.LocalCommitmentNumber);
    }

    [Fact]
    public async Task Given_TheGuardCannotBeWritten_When_ReleasingASecret_Then_NothingLeavesTheSigner()
    {
        // Arrange: the store's write fails (the crash before the durable write)
        var channel = await PersistChannelAsync();
        await using var node = BuildNode(failRaises: true);
        var signer = node.GetRequiredService<ILightningSigner>();
        signer.AdvanceLocalCommitment(channel.ChannelId, RowsCommitmentNumber + 1);
        var current = CreateCommitment(channel, RowsCommitmentNumber + 1);

        // Act & Assert: no secret, no commitment signature for the peer, no broadcast signature
        Assert.Throws<SignerException>(() => signer.RevealPerCommitmentSecret(channel.ChannelId,
                                                                             RowsCommitmentNumber));
        Assert.Throws<SignerException>(() => signer.SignChannelTransaction(channel.ChannelId,
                                                                          CreateCommitment(channel, 6)));
        Assert.Throws<SignerException>(() => signer.SignLocalCommitmentForBroadcast(
                                           channel.ChannelId, RowsCommitmentNumber + 1, current,
                                           SignAsRemote(channel, current)));

        // The secret of 5 never left, so a node over the same database may still broadcast commitment 5
        await using var other = BuildNode();
        var revoked = CreateCommitment(channel, RowsCommitmentNumber);
        Assert.Null(Record.Exception(() => other.GetRequiredService<ILightningSigner>()
                                                .SignLocalCommitmentForBroadcast(
                                                     channel.ChannelId, RowsCommitmentNumber, revoked,
                                                     SignAsRemote(channel, revoked))));
    }

    [Fact]
    public async Task Given_TheGuardCannotBeRead_When_Signing_Then_TheSignerRefuses()
    {
        // Arrange
        var channel = await PersistChannelAsync();
        await using var node = BuildNode(failLoads: true);
        var signer = node.GetRequiredService<ILightningSigner>();

        // Act & Assert: fail closed
        Assert.Throws<SignerException>(() => signer.SignChannelTransaction(channel.ChannelId,
                                                                          CreateCommitment(channel, 6)));
        Assert.Throws<SignerException>(() => signer.RevealPerCommitmentSecret(channel.ChannelId,
                                                                             RowsCommitmentNumber - 1));
    }

    [Fact]
    public async Task Given_ADatabaseFromBeforeTheGuard_When_TheSignerLoadsAChannel_Then_ItSeedsTheGuardFromTheRows()
    {
        // Arrange: channel rows and no guard row (an existing node upgrading in place)
        var channel = await PersistChannelAsync();
        Assert.Null(await ReadGuardAsync(channel.ChannelId));

        // Act
        await using var node = BuildNode();
        Assert.Equal(32, ((byte[])node.GetRequiredService<ILightningSigner>()
                                      .RevealPerCommitmentSecret(channel.ChannelId, RowsCommitmentNumber - 1)).Length);

        // Assert
        var guard = await ReadGuardAsync(channel.ChannelId);
        Assert.NotNull(guard);
        Assert.Equal(RowsCommitmentNumber, guard.Value.LocalCommitmentNumber);
        Assert.Equal(RowsCommitmentNumber - 1, guard.Value.RevokedCommitmentNumber);
        Assert.Null(guard.Value.BroadcastSignedCommitmentNumber);
        Assert.False(guard.Value.DataLossDetected);
    }

    [Fact]
    public async Task Given_APersistedGuard_When_AStaleWriterRaisesIt_Then_ItNeverMovesBack()
    {
        // Arrange
        var channelId = new ChannelId(Enumerable.Repeat((byte)0x6A, 32).ToArray());
        await RaiseAsync(channelId, new ChannelSignerGuard(9, 8, 10, 7, true));

        // Act: lower numbers, a higher broadcast mark, no data loss
        await RaiseAsync(channelId, new ChannelSignerGuard(3, 2, 4, 9));
        await RaiseAsync(channelId, new ChannelSignerGuard(12, BroadcastSignedCommitmentNumber: 6));

        // Assert: field by field the safe direction
        Assert.Equal(new ChannelSignerGuard(12, 8, 10, 6, true), await ReadGuardAsync(channelId));
    }

    [Fact]
    public async Task Given_AStoredChannel_When_ItsSigningDataIsLoaded_Then_ItCarriesTheObscuringFactor()
    {
        // Arrange
        var channel = await PersistChannelAsync();

        // Act
        await using var context = _database.CreateContext();
        var signingInfo = await new ChannelSigningInfoDbRepository(context).GetAsync(channel.ChannelId);

        // Assert: the factor the commitment numbers are obscured with (BOLT 3), as the channel model computes it
        Assert.Equal(channel.CommitmentNumber!.ObscuringFactor, signingInfo!.Value.CommitmentObscuringFactor);
        Assert.Equal(channel.CommitmentNumber.ObscuringFactor, channel.GetSigningInfo().CommitmentObscuringFactor);
    }

    private async Task RaiseAsync(ChannelId channelId, ChannelSignerGuard guard)
    {
        await using var context = _database.CreateContext();
        await new ChannelSignerGuardDbRepository(context).RaiseAsync(channelId, guard);
    }

    private async Task<ChannelSignerGuard?> ReadGuardAsync(ChannelId channelId)
    {
        await using var context = _database.CreateContext();
        return await new ChannelSignerGuardDbRepository(context).GetAsync(channelId);
    }

    /// <summary>A channel we opened with the node's first channel key, its rows at commitment 5 on both sides.</summary>
    private async Task<ChannelModel> PersistChannelAsync()
    {
        var signer = new LocalLightningSigner(new FundingOutputBuilder(),
                                              new KeyDerivationService(new Secp256K1Math()),
                                              NullLogger<LocalLightningSigner>.Instance,
                                              new NodeOptions { BitcoinNetwork = "regtest" }, _keyManager,
                                              new Infrastructure.Repositories.Memory.UtxoMemoryRepository());
        var keyIndex = signer.CreateNewChannel(out var basepoints, out var firstPoint);
        var remote = new ChannelKeySetModel(0, _remoteFundingKey.PubKey.ToBytes(), new Key().PubKey.ToBytes(),
                                            new Key().PubKey.ToBytes(), new Key().PubKey.ToBytes(),
                                            new Key().PubKey.ToBytes(), new Key().PubKey.ToBytes());
        var local = new ChannelKeySetModel(keyIndex, basepoints.FundingPubKey, basepoints.RevocationBasepoint,
                                           basepoints.PaymentBasepoint, basepoints.DelayedPaymentBasepoint,
                                           basepoints.HtlcBasepoint, firstPoint);
        var fundingTxId = new TxId(Enumerable.Repeat((byte)0x3C, 32).ToArray());
        var fundingOutput = new FundingOutputInfo(LightningMoney.Satoshis(FundingSats), local.FundingCompactPubKey,
                                                  remote.FundingCompactPubKey, fundingTxId, 1);
        var channelParams = TestChannelParams.Create(LightningMoney.Satoshis(10_000), LightningMoney.Satoshis(2_500),
                                                     LightningMoney.MilliSatoshis(1_000),
                                                     LightningMoney.Satoshis(546), 483,
                                                     LightningMoney.Satoshis(500_000), 3, false,
                                                     LightningMoney.Satoshis(546), 144, FeatureSupport.No);
        var channel = new ChannelModel(channelParams, new ChannelId(Enumerable.Repeat((byte)0x5D, 32).ToArray()),
                                       new CommitmentNumber(local.PaymentCompactBasepoint,
                                                            remote.PaymentCompactBasepoint, new Sha256()),
                                       fundingOutput, true, null, null, LightningMoney.Satoshis(FundingSats), local,
                                       0, RowsCommitmentNumber, LightningMoney.Zero, remote, 0,
                                       new Key().PubKey.ToBytes(), RowsCommitmentNumber, ChannelState.Open,
                                       ChannelVersion.V1, localCommitmentNumber: RowsCommitmentNumber,
                                       remoteCommitmentNumber: RowsCommitmentNumber)
        {
            ShortChannelId = new ShortChannelId(500, 3, 1)
        };

        await using var context = _database.CreateContext();
        await new ChannelDbRepository(context, new Sha256()).AddAsync(channel);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        return channel;
    }

    /// <summary>
    /// One node's signer as the daemon builds it (<c>AddBitcoinInfrastructure</c> +
    /// <c>AddRepositoriesInfrastructureServices</c>) over the shared database and key manager; optionally with a guard
    /// store whose reads or writes fail.
    /// </summary>
    private ServiceProvider BuildNode(bool failLoads = false, bool failRaises = false)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.Configure<NodeOptions>(o => o.BitcoinNetwork = "regtest");
        services.AddSingleton<ISecureKeyManager>(_keyManager);
        services.AddBitcoinInfrastructure();
        services.AddRepositoriesInfrastructureServices();
        services.AddScoped<IUnitOfWork>(sp => new UnitOfWork(_database.CreateContext(),
                                                             NullLogger<UnitOfWork>.Instance, new Sha256(),
                                                             sp.GetRequiredService<IUtxoMemoryRepository>()));
        if (failLoads || failRaises)
            services.AddSingleton<IChannelSignerGuardStore>(sp => new FailingGuardStore(
                                                                new ChannelSignerGuardStore(
                                                                    sp.GetRequiredService<IServiceScopeFactory>()),
                                                                failLoads, failRaises));
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    /// <summary>
    /// A commitment transaction of <paramref name="channel"/> numbered <paramref name="number"/>: the BOLT 3 locktime
    /// and sequence of that obscured number, spending the funding output.
    /// </summary>
    private static SignedTransaction CreateCommitment(ChannelModel channel, ulong number)
    {
        var obscured = number ^ channel.CommitmentNumber!.ObscuringFactor;
        var tx = Network.RegTest.CreateTransaction();
        tx.Version = 2;
        tx.LockTime = new LockTime((0x20u << 24) | (uint)(obscured & 0xFFFFFF));
        tx.Inputs.Add(new OutPoint(new uint256((byte[])channel.FundingOutput!.TransactionId!.Value),
                                   channel.FundingOutput.Index!.Value),
                      sequence: new Sequence((0x80u << 24) | (uint)((obscured >> 24) & 0xFFFFFF)));
        tx.Outputs.Add(Money.Satoshis(FundingSats - 1_000), new Key().PubKey.WitHash.ScriptPubKey);
        return new SignedTransaction(tx.GetHash().ToBytes(), tx.ToBytes());
    }

    private CompactSignature SignAsRemote(ChannelModel channel, SignedTransaction commitment)
    {
        var tx = Transaction.Load(commitment.RawTxBytes, Network.RegTest);
        var fundingOutput = new FundingOutputBuilder().Build(channel.FundingOutput!);
        var sigHash = tx.GetSignatureHash(fundingOutput.RedeemScript, 0, SigHash.All, fundingOutput.ToTxOut(),
                                          HashVersion.WitnessV0);
        return _remoteFundingKey.Sign(sigHash, new SigningOptions(SigHash.All, false)).Signature.MakeCanonical()
                                .ToCompact();
    }

    private sealed class FailingGuardStore(IChannelSignerGuardStore inner, bool failLoads, bool failRaises)
        : IChannelSignerGuardStore
    {
        public ChannelSignerGuard? Load(ChannelId channelId) =>
            failLoads ? throw new InvalidOperationException("The database is unavailable") : inner.Load(channelId);

        public void Raise(ChannelId channelId, ChannelSignerGuard guard)
        {
            if (failRaises)
                throw new InvalidOperationException("The process died before the write");
            inner.Raise(channelId, guard);
        }
    }
}