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
using Domain.Channels.Splicing;
using Domain.Channels.Splicing.Enums;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
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
using Infrastructure.Repositories.Memory;

/// <summary>
/// NL-495: after a splice lock and a restart, the reloaded <see cref="ChannelModel"/> carries the locked funding's key
/// index (<see cref="ChannelModel.LocalFundingKeyIndex"/>, from its <c>ChannelFundings</c> row) and
/// <see cref="ChannelModel.GetSigningInfo"/> reports the current funding's outpoint, keys and index, the same view as
/// the signer's database source, so a signer that learns the channel from the model signs the spliced funding with
/// the rotated key.
/// </summary>
public sealed class SplicedSigningInfoReloadTests : IDisposable
{
    private const uint FundingSats = 1_000_000;
    private const ulong SpliceSats = 1_200_000;
    private const ulong LocalCommitmentNumber = 5;

    private readonly SqliteTestDatabase _database = new();
    private readonly FakeSecureKeyManager _keyManager = new();
    private readonly Key _remoteSpliceKey = new();
    private readonly TxId _spliceTxId = new(Enumerable.Repeat((byte)0x4D, 32).ToArray());

    public void Dispose()
    {
        _database.Dispose();
    }

    [Fact]
    public async Task Given_ALockedSplice_When_TheChannelIsReloaded_Then_ItsSigningInfoIsTheSplicedFundings()
    {
        // Arrange
        var (channel, signer) = await PersistChannelAsync();
        var spliceKey = signer.GetFundingPubKey(channel.LocalKeySet.KeyIndex, 1);
        await LockSpliceAsync(channel, spliceKey);

        // Act
        var reloaded = await ReloadAsync(channel.ChannelId);
        var info = reloaded.GetSigningInfo();
        await using var context = _database.CreateContext();
        var stored = await new ChannelSigningInfoDbRepository(context).GetAsync(channel.ChannelId);

        // Assert
        Assert.Equal(1U, reloaded.LocalFundingKeyIndex);
        Assert.Equal(_spliceTxId, info.FundingTxId);
        Assert.Equal((ushort)0, info.FundingOutputIndex);
        Assert.Equal(LightningMoney.Satoshis(SpliceSats).MilliSatoshi, info.FundingSatoshis);
        Assert.Equal(spliceKey, info.LocalFundingPubKey);
        Assert.Equal(new CompactPubKey(_remoteSpliceKey.PubKey.ToBytes()), info.RemoteFundingPubKey);
        Assert.Equal(1U, info.LocalFundingKeyIndex);
        Assert.NotEqual(channel.LocalKeySet.FundingCompactPubKey, info.LocalFundingPubKey);

        // The model's view is the database source's (the signer's first registration after a restart)
        Assert.NotNull(stored);
        Assert.Equal(stored.Value.FundingTxId, info.FundingTxId);
        Assert.Equal(stored.Value.FundingOutputIndex, info.FundingOutputIndex);
        Assert.Equal(stored.Value.FundingSatoshis, info.FundingSatoshis);
        Assert.Equal(stored.Value.LocalFundingPubKey, info.LocalFundingPubKey);
        Assert.Equal(stored.Value.RemoteFundingPubKey, info.RemoteFundingPubKey);
        Assert.Equal(stored.Value.LocalFundingKeyIndex, info.LocalFundingKeyIndex);
    }

    [Fact]
    public async Task Given_ALockedSplice_When_ASignerLearnsTheChannelFromTheReloadedModel_Then_ItSignsWithTheRotatedKey()
    {
        // Arrange: a restarted node whose signer has no database source (the in-process harnesses) registers the
        // channel from the model, as ChannelManager does at startup
        var (channel, signer) = await PersistChannelAsync();
        var spliceKey = signer.GetFundingPubKey(channel.LocalKeySet.KeyIndex, 1);
        await LockSpliceAsync(channel, spliceKey);
        var reloaded = await ReloadAsync(channel.ChannelId);
        var restartedSigner = CreateSigner(null);
        var commitment = CreateUnsignedCommitment(reloaded);

        // Act
        restartedSigner.RegisterChannel(reloaded.ChannelId, reloaded.GetSigningInfo());
        var signature = restartedSigner.SignChannelTransaction(reloaded.ChannelId, commitment);

        // Assert: valid for the spliced funding's 2-of-2 under the rotated key, not the key set's
        Assert.True(VerifyFundingSignature(reloaded, commitment, signature, new PubKey(spliceKey)));
        Assert.False(VerifyFundingSignature(reloaded, commitment, signature,
                                            new PubKey(channel.LocalKeySet.FundingCompactPubKey)));

        // The signer that loads the channel from the database signs the same (RFC 6979), and a registration from the
        // model afterwards is a refresh of the same channel, not a mismatch
        await using var node = BuildRestartedNode();
        var loadingSigner = node.GetRequiredService<ILightningSigner>();
        Assert.Equal(signature, loadingSigner.SignChannelTransaction(reloaded.ChannelId, commitment));
        loadingSigner.RegisterChannel(reloaded.ChannelId, reloaded.GetSigningInfo());
        Assert.Equal(signature, loadingSigner.SignChannelTransaction(reloaded.ChannelId, commitment));
    }

    [Fact]
    public async Task Given_ANeverSplicedChannel_When_Reloaded_Then_KeyIndexZeroAndTheKeySetsKeys()
    {
        // Arrange
        var (channel, _) = await PersistChannelAsync();

        // Act
        var reloaded = await ReloadAsync(channel.ChannelId);
        var info = reloaded.GetSigningInfo();

        // Assert
        Assert.Equal(0U, reloaded.LocalFundingKeyIndex);
        Assert.Equal(channel.LocalKeySet.FundingCompactPubKey, info.LocalFundingPubKey);
        Assert.Equal(channel.RemoteKeySet!.FundingCompactPubKey, info.RemoteFundingPubKey);
        Assert.Equal(0U, info.LocalFundingKeyIndex);
    }

    /// <summary>The splice at key index 1 staged pending, then locked, as the splice's saves do.</summary>
    private async Task LockSpliceAsync(ChannelModel channel, CompactPubKey spliceKey)
    {
        var splice = new ChannelFunding(_spliceTxId, 0, SpliceSats, spliceKey, _remoteSpliceKey.PubKey.ToBytes(), 1,
                                        200_000_000, 0, ChannelFundingKind.Splice, ChannelFundingStatus.Pending, 2_500,
                                        0, ShortChannelId: new ShortChannelId(900, 1, 0));
        await using (var context = _database.CreateContext())
        {
            await new ChannelFundingDbRepository(context).UpsertAsync(channel.ChannelId, splice);
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using (var context = _database.CreateContext())
        {
            var repository = new ChannelFundingDbRepository(context);
            var set = (await repository.GetFundingSetAsync(channel.ChannelId))!;
            var (next, retired) = set.Lock(_spliceTxId);
            await repository.ApplyLockAsync(channel.ChannelId, next.Current, retired);
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
    }

    private async Task<ChannelModel> ReloadAsync(ChannelId channelId)
    {
        await using var context = _database.CreateContext();
        return (await new ChannelDbRepository(context, new Sha256()).GetByIdAsync(channelId))!;
    }

    /// <summary>A channel we opened with the node's first channel key, saved as the node would.</summary>
    private async Task<(ChannelModel Channel, LocalLightningSigner Signer)> PersistChannelAsync()
    {
        var signer = CreateSigner(null);
        var keyIndex = signer.CreateNewChannel(out var basepoints, out var firstPoint);
        var remote = new ChannelKeySetModel(0, new Key().PubKey.ToBytes(), new Key().PubKey.ToBytes(),
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
        var channel = new ChannelModel(channelParams, new ChannelId(Enumerable.Repeat((byte)0x5E, 32).ToArray()),
                                       new CommitmentNumber(local.PaymentCompactBasepoint,
                                                            remote.PaymentCompactBasepoint, new Sha256()),
                                       fundingOutput, true, null, null, LightningMoney.Satoshis(FundingSats), local,
                                       0, LocalCommitmentNumber, LightningMoney.Zero, remote, 0,
                                       new Key().PubKey.ToBytes(), LocalCommitmentNumber, ChannelState.Open,
                                       ChannelVersion.V1, localCommitmentNumber: LocalCommitmentNumber,
                                       remoteCommitmentNumber: LocalCommitmentNumber)
        {
            ShortChannelId = new ShortChannelId(500, 3, 1)
        };

        await using var context = _database.CreateContext();
        await new ChannelDbRepository(context, new Sha256()).AddAsync(channel);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        return (channel, signer);
    }

    /// <summary>The restarted node's signer as the daemon builds it (it loads channels from the database).</summary>
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
        tx.Outputs.Add(Money.Satoshis((long)SpliceSats - 1_000), new Key().PubKey.WitHash.ScriptPubKey);
        return new SignedTransaction(tx.GetHash().ToBytes(), tx.ToBytes());
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