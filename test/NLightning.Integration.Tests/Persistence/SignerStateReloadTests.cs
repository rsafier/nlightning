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
using Domain.Onchain.Enums;
using Domain.Onchain.Models;
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
using Infrastructure.Repositories.Database.Onchain;
using Infrastructure.Repositories.Memory;

/// <summary>
/// NL-067: after a restart the signer loads a channel's signing data from the database on first use
/// (<see cref="ChannelSigningInfoSource"/>), so a channel keeps operating without being registered by hand: the same
/// commitment signature as before the restart, a fully valid commitment broadcast, and the guards restored from the
/// persisted state (revocation guard, data loss, the commitment signed for broadcast).
/// </summary>
public sealed class SignerStateReloadTests : IDisposable
{
    private const uint FundingSats = 1_000_000;
    private const ulong LocalCommitmentNumber = 5;

    private readonly SqliteTestDatabase _database = new();
    private readonly FakeSecureKeyManager _keyManager = new();
    private readonly Key _remoteFundingKey = new();

    public void Dispose()
    {
        _database.Dispose();
    }

    [Fact]
    public async Task Given_PersistedChannel_When_SignerRestartsWithoutRegistration_Then_ItSignsTheSameCommitment()
    {
        // Arrange: before the restart the channel is registered by hand, as today
        var (channel, signerBefore) = await PersistChannelAsync();
        signerBefore.RegisterChannel(channel.ChannelId, channel.GetSigningInfo());
        var commitment = CreateUnsignedCommitment(channel);
        var signatureBefore = signerBefore.SignChannelTransaction(channel.ChannelId, commitment);

        // Act: a new signer over the same key manager and database, never registered
        await using var node = BuildRestartedNode();
        var signerAfter = node.GetRequiredService<ILightningSigner>();
        var signatureAfter = signerAfter.SignChannelTransaction(channel.ChannelId, commitment);

        // Assert: RFC 6979 makes the signature a function of the key and the transaction
        Assert.Equal(signatureBefore, signatureAfter);
        Assert.True(VerifyFundingSignature(channel, commitment, signatureAfter,
                                           new PubKey(channel.LocalKeySet.FundingCompactPubKey)));
        Assert.Equal(signerBefore.GetChannelBasepoints(channel.ChannelId),
                     signerAfter.GetChannelBasepoints(channel.ChannelId));
    }

    [Fact]
    public async Task Given_PersistedChannel_When_SignerRestarts_Then_TheCommitmentBroadcastIsFullyValid()
    {
        // Arrange
        var (channel, _) = await PersistChannelAsync();
        var commitment = CreateUnsignedCommitment(channel);
        var remoteSignature = SignAsRemote(channel, commitment);
        await using var node = BuildRestartedNode();
        var signer = node.GetRequiredService<ILightningSigner>();

        // Act
        var signed = signer.SignLocalCommitmentForBroadcast(channel.ChannelId, LocalCommitmentNumber, commitment,
                                                            remoteSignature);

        // Assert: the funding witness verifies against the funding output
        var tx = Transaction.Load(signed.RawTxBytes, Network.RegTest);
        var fundingOutput = new FundingOutputBuilder().Build(channel.FundingOutput!);
        Assert.True(tx.Inputs.AsIndexedInputs().Single().VerifyScript(fundingOutput.ToTxOut(), out var error),
                    error.ToString());
    }

    [Fact]
    public async Task Given_PersistedLocalCommitmentNumber_When_SignerRestarts_Then_TheRevocationGuardHolds()
    {
        // Arrange
        var (channel, signerBefore) = await PersistChannelAsync();
        signerBefore.RegisterChannel(channel.ChannelId, channel.GetSigningInfo());
        await using var node = BuildRestartedNode();
        var signer = node.GetRequiredService<ILightningSigner>();

        // Act
        var revoked = signer.RevealPerCommitmentSecret(channel.ChannelId, LocalCommitmentNumber - 1);

        // Assert: the secret of a revoked commitment is released, the current one's is not (NL-189)
        Assert.Equal(signerBefore.RevealPerCommitmentSecret(channel.ChannelId, LocalCommitmentNumber - 1), revoked);
        Assert.Throws<SignerException>(() => signer.RevealPerCommitmentSecret(channel.ChannelId,
                                                                             LocalCommitmentNumber));
        Assert.Equal(signerBefore.GetPerCommitmentPoint(channel.ChannelId, LocalCommitmentNumber + 1),
                     signer.GetPerCommitmentPoint(channel.ChannelId, LocalCommitmentNumber + 1));
    }

    [Fact]
    public async Task Given_PersistedDataLoss_When_SignerRestarts_Then_ItRefusesToSign()
    {
        // Arrange
        var (channel, _) = await PersistChannelAsync(markDataLoss: true);
        await using var node = BuildRestartedNode();
        var signer = node.GetRequiredService<ILightningSigner>();

        // Act & Assert (invariant I12 survives the restart)
        Assert.Throws<SignerException>(() => signer.SignChannelTransaction(channel.ChannelId,
                                                                          CreateUnsignedCommitment(channel)));
    }

    [Fact]
    public async Task Given_PersistedCommitmentBroadcast_When_SignerRestarts_Then_TheBroadcastMarkHolds()
    {
        // Arrange: our commitment 5 was signed for broadcast and its row saved (NL-271/NL-297)
        var (channel, _) = await PersistChannelAsync();
        await using (var context = _database.CreateContext())
        {
            new BroadcastTransactionDbRepository(context).Add(
                new BroadcastTransactionModel(new SignedTransaction(new TxId(Enumerable.Repeat((byte)0x77, 32).ToArray()),
                                                                    [0x02, 0x00]),
                                              BroadcastPurpose.LocalCommitment, channel.ChannelId, 900,
                                              commitmentNumber: LocalCommitmentNumber));
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var node = BuildRestartedNode();
        var signer = node.GetRequiredService<ILightningSigner>();

        // Act
        var marked = signer.TryGetBroadcastSignedCommitment(channel.ChannelId, out var number);

        // Assert: S1 is back after the restart without any registration, so no new commitment is signed
        Assert.True(marked);
        Assert.Equal(LocalCommitmentNumber, number);
        Assert.Throws<SignerException>(() => signer.SignChannelTransaction(channel.ChannelId,
                                                                          CreateUnsignedCommitment(channel)));
        Assert.Throws<SignerException>(() => signer.AdvanceLocalCommitment(channel.ChannelId,
                                                                          LocalCommitmentNumber + 1));
    }

    [Fact]
    public async Task Given_UnknownChannel_When_SignerRestarts_Then_ItStillRefuses()
    {
        // Arrange
        await PersistChannelAsync();
        await using var node = BuildRestartedNode();
        var signer = node.GetRequiredService<ILightningSigner>();
        var unknown = new ChannelId(Enumerable.Repeat((byte)0xEE, 32).ToArray());

        // Act & Assert
        Assert.Throws<SignerException>(() => signer.GetChannelBasepoints(unknown));
        Assert.Throws<SignerException>(() => signer.RevealPerCommitmentSecret(unknown, 0));
    }

    [Theory]
    [InlineData(ChannelState.Closed)]
    [InlineData(ChannelState.Stale)]
    public async Task Given_ClosedOrStaleChannel_When_SignerRestarts_Then_ItIsNotLoaded(ChannelState state)
    {
        // Arrange: the funding output is irrevocably spent (or never confirmed): nothing to sign any more
        var (channel, _) = await PersistChannelAsync(state: state);
        await using var node = BuildRestartedNode();
        var signer = node.GetRequiredService<ILightningSigner>();

        // Act & Assert: refused as before NL-067, no new remote commitment signature for a closed channel
        Assert.Throws<InvalidOperationException>(() => signer.SignChannelTransaction(
                                                     channel.ChannelId, CreateUnsignedCommitment(channel)));
        Assert.Throws<SignerException>(() => signer.GetChannelBasepoints(channel.ChannelId));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Given_PersistedAnnounceFlag_When_SignerLoadsTheChannel_Then_TheFlagIsLoaded(bool announce)
    {
        // Arrange
        var (channel, _) = await PersistChannelAsync(announceChannel: announce);

        // Act
        await using var context = _database.CreateContext();
        var signingInfo = await new ChannelSigningInfoDbRepository(context).GetAsync(channel.ChannelId);
        var all = await new ChannelSigningInfoDbRepository(context).GetAllAsync();

        // Assert: the signer refuses a channel announcement unless the channel is public (BOLT 7)
        Assert.NotNull(signingInfo);
        Assert.Equal(announce, signingInfo.Value.AnnounceChannel);
        Assert.Equal(announce, all[channel.ChannelId].AnnounceChannel);
        Assert.Equal(channel.GetSigningInfo().AnnounceChannel, signingInfo.Value.AnnounceChannel);
    }

    /// <summary>A channel we opened with the node's first channel key, saved as the node would.</summary>
    private async Task<(ChannelModel Channel, LocalLightningSigner Signer)> PersistChannelAsync(
        bool markDataLoss = false, ChannelState state = ChannelState.Open, bool announceChannel = false)
    {
        var signer = CreateSigner(null);
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
                                                     LightningMoney.Satoshis(546), 144, FeatureSupport.No) with
        {
            AnnounceChannel = announceChannel
        };
        var channel = new ChannelModel(channelParams, new ChannelId(Enumerable.Repeat((byte)0x5D, 32).ToArray()),
                                       new CommitmentNumber(local.PaymentCompactBasepoint,
                                                            remote.PaymentCompactBasepoint, new Sha256()),
                                       fundingOutput, true, null, null, LightningMoney.Satoshis(FundingSats), local,
                                       0, LocalCommitmentNumber, LightningMoney.Zero, remote, 0,
                                       new Key().PubKey.ToBytes(), LocalCommitmentNumber, state,
                                       ChannelVersion.V1, localCommitmentNumber: LocalCommitmentNumber,
                                       remoteCommitmentNumber: LocalCommitmentNumber)
        {
            ShortChannelId = new ShortChannelId(500, 3, 1)
        };
        if (markDataLoss)
            channel.MarkDataLossDetected();

        await using var context = _database.CreateContext();
        await new ChannelDbRepository(context, new Sha256()).AddAsync(channel);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        return (channel, signer);
    }

    /// <summary>
    /// The restarted node's signer as the daemon builds it: <c>AddBitcoinInfrastructure</c> +
    /// <c>AddRepositoriesInfrastructureServices</c> over the same database and key manager, nothing registered.
    /// </summary>
    private ServiceProvider BuildRestartedNode()
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
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private LocalLightningSigner CreateSigner(IChannelSigningInfoSource? source) =>
        new(new FundingOutputBuilder(), new KeyDerivationService(new Secp256K1Math()),
            NullLogger<LocalLightningSigner>.Instance, new NodeOptions { BitcoinNetwork = "regtest" }, _keyManager,
            new UtxoMemoryRepository(), source);

    private static SignedTransaction CreateUnsignedCommitment(ChannelModel channel)
    {
        var tx = Network.RegTest.CreateTransaction();
        tx.Version = 2;
        tx.Inputs.Add(new OutPoint(new uint256((byte[])channel.FundingOutput!.TransactionId!.Value),
                                   channel.FundingOutput.Index!.Value));
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

    private static bool VerifyFundingSignature(ChannelModel channel, SignedTransaction commitment,
                                               CompactSignature signature, PubKey pubKey)
    {
        var tx = Transaction.Load(commitment.RawTxBytes, Network.RegTest);
        var fundingOutput = new FundingOutputBuilder().Build(channel.FundingOutput!);
        var sigHash = tx.GetSignatureHash(fundingOutput.RedeemScript, 0, SigHash.All, fundingOutput.ToTxOut(),
                                          HashVersion.WitnessV0);
        return ECDSASignature.TryParseFromCompact(signature, out var ecdsa) && pubKey.Verify(sigHash, ecdsa);
    }
}