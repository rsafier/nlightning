using Microsoft.Extensions.Options;
using NBitcoin;

namespace NLightning.Infrastructure.Bitcoin.Tests.Taproot;

using Bitcoin.Builders;
using Bitcoin.Crypto.Functions;
using Bitcoin.Onchain;
using Bitcoin.Services;
using Bitcoin.Signers;
using Domain.Bitcoin.Transactions.Enums;
using Domain.Bitcoin.Transactions.Factories;
using Domain.Bitcoin.Transactions.Outputs;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Commitments;
using Domain.Channels.Enums;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Money;
using Domain.Node.Options;
using Domain.Onchain.Enums;
using Domain.Onchain.Factories;
using Domain.Onchain.Models;
using Domain.Protocol.Models;
using Infrastructure.Crypto.Hashes;

/// <summary>
/// NL-877 T4 safety floor: the outputs of a simple taproot commitment on chain are mapped by
/// <see cref="CommitmentOutputMapper"/> to descriptors with their tapscript leaf and control block, and our own funds
/// are swept by BIP 341 script path through the production <see cref="SweepTransactionBuilder"/> and
/// <see cref="LocalLightningSigner"/>: our <c>to_local</c> after <c>to_self_delay</c> (delay leaf), our <c>to_remote</c>
/// on the peer's commitment after one block (its leaf), and a revoked <c>to_local</c> (revocation leaf). Every sweep is
/// executed by NBitcoin's interpreter against the commitment output it spends; HTLC outputs are mapped but carry no
/// spend path (NL-966).
/// </summary>
public class SimpleTaprootOnchainSweepTests
{
    private const ulong Number = 9;
    private const ulong FeeRatePerKw = 2_500;
    private const ushort AliceToSelfDelay = 144;
    private const ushort BobToSelfDelay = 120;

    private static readonly KeyDerivationService s_keyDerivation = new(new Secp256K1Math());
    private static readonly IOptions<NodeOptions> s_options = Microsoft.Extensions.Options.Options.Create(new NodeOptions());
    private static readonly byte[] s_destination = new Key(Enumerable.Repeat((byte)0x44, 32).ToArray())
                                                  .PubKey.WitHash.ScriptPubKey.ToBytes();

    [Fact]
    public void Given_OurTaprootCommitmentOnChain_When_Mapped_Then_ToLocalSweptByTheDelayLeafAfterToSelfDelay()
    {
        // Arrange: Bob's own commitment (Bob broadcast it)
        var kit = new TaprootSignerKit(bobLocalNumber: Number);
        var bobChannel = CreateChannel(kit, alice: false);
        var bobPoint = kit.Bob.GetPerCommitmentPoint(0u, Number);
        var commitment = Build(kit.Bob, bobChannel, CommitmentSide.Local, null);

        // Act
        var map = Mapper(kit.Bob).Map(bobChannel, Spec(alice: false), CommitmentCase.Local, Number, null, commitment);
        var toLocal = Assert.Single(map.Outputs, o => o.Kind == OutputDescriptorKind.DelayedToLocal);
        var input = SweepInputFactory.ToLocal(toLocal, commitment.TxId, bobPoint);
        var signed = Sweep(kit.Bob, input);

        // Assert: every output mapped on the rebuilt txid, HTLCs without a spend path (NL-966)
        Assert.Equal(commitment.TxId, map.ExpectedTxId);
        Assert.Empty(map.UnmappedVouts);
        Assert.All(map.Outputs, o => Assert.True(o.IsSimpleTaproot));
        Assert.Equal(4, map.Outputs.Count(o => o.Htlc is not null && o.TaprootControlBlock is null));
        Assert.Single(map.Outputs, o => o.Kind == OutputDescriptorKind.OurAnchor);
        Assert.Single(map.Outputs, o => o.Kind == OutputDescriptorKind.PeerAnchor);
        Assert.Equal(65, toLocal.TaprootControlBlock!.Length); // two leaves: the sibling's hash
        Assert.Equal(AliceToSelfDelay, toLocal.CsvDelay);

        // The script-path spend: <sig> <delay leaf> <control block>, nSequence = to_self_delay, valid by execution
        var tx = Transaction.Load(signed.RawTxBytes, Network.Main);
        Assert.Equal((uint)AliceToSelfDelay, (uint)Assert.Single(tx.Inputs).Sequence);
        Assert.Equal(3, tx.Inputs[0].WitScript.PushCount);
        Assert.Equal(64, tx.Inputs[0].WitScript[0].Length);
        Assert.Equal(toLocal.WitnessScript, tx.Inputs[0].WitScript[1]);
        AssertSpends(tx, commitment, toLocal.Vout);

        // Without the CSV the spend is refused by the interpreter (the delay leaf really is the path)
        var early = tx.Clone();
        early.Inputs[0].Sequence = new Sequence(1);
        Assert.NotNull(early.CreateValidator([Output(commitment, toLocal.Vout)]).ValidateInput(0).Error);
    }

    [Fact]
    public void Given_ThePeersTaprootCommitmentOnChain_When_Mapped_Then_OurToRemoteSweptByItsLeafAfterOneBlock()
    {
        // Arrange: Bob's commitment as Alice sees it on chain
        var kit = new TaprootSignerKit(bobLocalNumber: Number);
        var aliceChannel = CreateChannel(kit, alice: true);
        var bobPoint = kit.Bob.GetPerCommitmentPoint(0u, Number);
        var commitment = Build(kit.Alice, aliceChannel, CommitmentSide.Remote, bobPoint);

        // Act: mapped from the rebuilt commitment, and found by script without it (data loss, B5-RMT-03)
        var map = Mapper(kit.Alice).Map(aliceChannel, Spec(alice: true), CommitmentCase.Remote, Number, bobPoint,
                                        commitment);
        var toRemote = Assert.Single(map.Outputs, o => o.Kind == OutputDescriptorKind.PaymentToRemote);
        var found = Assert.Single(Mapper(kit.Alice).FindSimpleTaprootPaymentToRemote(
                                      commitment, aliceChannel.LocalKeySet.PaymentCompactBasepoint));
        var signed = Sweep(kit.Alice, SweepInputFactory.ToRemote(toRemote, commitment.TxId,
                                                                  aliceChannel.LocalKeySet.PaymentCompactBasepoint));

        // Assert
        Assert.Equal(toRemote.Vout, found.Vout);
        Assert.Equal(toRemote.WitnessScript, found.WitnessScript);
        Assert.Equal(toRemote.TaprootControlBlock, found.TaprootControlBlock);
        Assert.Equal(33, toRemote.TaprootControlBlock!.Length); // one leaf: no inclusion proof
        Assert.Equal(TaprootNumsControlBlockKey(), toRemote.TaprootControlBlock[1..]);
        var tx = Transaction.Load(signed.RawTxBytes, Network.Main);
        Assert.Equal(1u, (uint)Assert.Single(tx.Inputs).Sequence);
        AssertSpends(tx, commitment, toRemote.Vout);
    }

    [Fact]
    public void Given_ARevokedTaprootCommitment_When_Mapped_Then_ItsToLocalPenalizedByTheRevocationLeaf()
    {
        // Arrange: Bob revoked commitment `Number` (his signer is past it) and broadcast it anyway
        var kit = new TaprootSignerKit(bobLocalNumber: Number + 1);
        var aliceChannel = CreateChannel(kit, alice: true);
        var secret = kit.Bob.RevealPerCommitmentSecret(TaprootSignerKit.ChannelId, Number);
        using var secretKey = new Key((byte[])secret);
        var point = new CompactPubKey(secretKey.PubKey.ToBytes());
        var commitment = Build(kit.Alice, aliceChannel, CommitmentSide.Remote, point);

        // Act
        var map = Mapper(kit.Alice).Map(aliceChannel, Spec(alice: true), CommitmentCase.Revoked, Number, point,
                                        commitment);
        var revoked = Assert.Single(map.Outputs, o => o.Kind == OutputDescriptorKind.RevokedToLocal);
        var revocationPubKey = new CompactPubKey(new Key(Enumerable.Repeat((byte)0x55, 32).ToArray()).PubKey.ToBytes());
        var input = SweepInputFactory.Penalty(revoked, commitment.TxId, secret, revocationPubKey);
        var signed = Sweep(kit.Alice, input);

        // Assert: no delay on the revocation leaf (nSequence is the RBF one), valid by execution
        var tx = Transaction.Load(signed.RawTxBytes, Network.Main);
        Assert.Equal(revoked.WitnessScript, tx.Inputs[0].WitScript[1]);
        AssertSpends(tx, commitment, revoked.Vout);

        // The revoked HTLC outputs are mapped but not penalized yet (NL-966): the factory refuses them
        var htlc = map.Outputs.First(o => o.Kind == OutputDescriptorKind.RevokedHtlc);
        Assert.Throws<ArgumentException>(
            () => SweepInputFactory.Penalty(htlc, commitment.TxId, secret, revocationPubKey));
    }

    [Fact]
    public void Given_ATaprootDescriptor_When_PersistedAndRead_Then_TheControlBlockRoundTrips()
    {
        // Arrange
        var kit = new TaprootSignerKit(bobLocalNumber: Number);
        var bobChannel = CreateChannel(kit, alice: false);
        var commitment = Build(kit.Bob, bobChannel, CommitmentSide.Local, null);
        var toLocal = Mapper(kit.Bob).Map(bobChannel, Spec(alice: false), CommitmentCase.Local, Number, null,
                                          commitment)
                                     .Outputs.Single(o => o.Kind == OutputDescriptorKind.DelayedToLocal);

        // Act
        var data = OutputDescriptorData.Decode(OutputDescriptorData.FromDescriptor(toLocal, null).Encode());

        // Assert
        Assert.Equal(toLocal.TaprootControlBlock, data.TaprootControlBlock);
        Assert.Equal(toLocal.WitnessScript, data.WitnessScript);
        Assert.Equal(toLocal.ScriptPubKey, data.ScriptPubKey);
    }

    [Fact]
    public void Given_ATaprootSweepContext_When_TheKeyIsNotInTheLeaf_Then_TheSignerRefuses()
    {
        // Arrange: the to_remote leaf of Bob's commitment signed with Alice's delayed key (a wrong key kind)
        var kit = new TaprootSignerKit(bobLocalNumber: Number);
        var aliceChannel = CreateChannel(kit, alice: true);
        var bobPoint = kit.Bob.GetPerCommitmentPoint(0u, Number);
        var commitment = Build(kit.Alice, aliceChannel, CommitmentSide.Remote, bobPoint);
        var toRemote = Mapper(kit.Alice).Map(aliceChannel, Spec(alice: true), CommitmentCase.Remote, Number, bobPoint,
                                             commitment)
                                        .Outputs.Single(o => o.Kind == OutputDescriptorKind.PaymentToRemote);
        var input = SweepInputFactory.ToRemote(toRemote, commitment.TxId,
                                               aliceChannel.LocalKeySet.PaymentCompactBasepoint) with
        {
            SpendKind = SweepSpendKind.DelayedOutput,
            PerCommitmentPoint = bobPoint
        };
        var unsigned = new SweepTransactionBuilder(s_options).Build([input], s_destination, 2_500);

        // Act / Assert
        Assert.Throws<Domain.Exceptions.SignerException>(
            () => kit.Alice.SignSweepInput(TaprootSignerKit.ChannelId, unsigned.GetSigningContext(0)));
    }

    #region Helpers

    private static SignedTransaction Sweep(LocalLightningSigner signer, SweepInput input)
    {
        var builder = new SweepTransactionBuilder(s_options);
        var unsigned = builder.Build([input], s_destination, 2_500);
        return builder.Sign(unsigned, signer, TaprootSignerKit.ChannelId);
    }

    private static void AssertSpends(Transaction tx, ChainTx commitment, uint vout)
    {
        var error = tx.CreateValidator([Output(commitment, vout)]).ValidateInput(0).Error;
        Assert.True(error is null, error?.ToString());
    }

    private static TxOut Output(ChainTx commitment, uint vout)
    {
        var output = commitment.Outputs[(int)vout];
        return new TxOut(Money.Satoshis((long)output.AmountSat), new Script(output.ScriptPubKey));
    }

    /// <summary>The control block's internal key: the x-only NUMS point of the spec vectors (NL-914).</summary>
    private static byte[] TaprootNumsControlBlockKey() =>
        Convert.FromHexString("dca094751109d0bd055d03565874e8276dd53e926b44e3bd1bb6bf4bc130a279");

    private static ChainTx Build(LocalLightningSigner signer, ChannelModel channel, CommitmentSide side,
                                 CompactPubKey? remotePoint)
    {
        var factory = Factory(signer);
        var model = side == CommitmentSide.Local
                        ? factory.CreateCommitmentTransactionModel(channel, Spec(channel.IsInitiator), side, Number)
                        : factory.CreateCommitmentTransactionModel(channel, Spec(channel.IsInitiator), side, Number,
                                                                   remotePoint!.Value);
        Assert.True(model.IsSimpleTaproot);
        var built = new CommitmentTransactionBuilder(s_options).BuildWithOutputMap(model);
        return ChainTxMapper.FromTransaction(Transaction.Load(built.Transaction.RawTxBytes, Network.Main));
    }

    private static CommitmentOutputMapper Mapper(LocalLightningSigner signer) =>
        new(Factory(signer), new CommitmentTransactionBuilder(s_options));

    private static readonly byte[][] s_preimages =
        Enumerable.Range(1, 4).Select(i => Enumerable.Repeat((byte)i, 32).ToArray()).ToArray();

    /// <summary>Alice's view: 600,000 sat local, 380,000 sat remote, two HTLCs each way.</summary>
    private static CommitmentTxSpec Spec(bool alice)
    {
        var htlcs = new List<Htlc>
        {
            Htlc(0, 5_000_000, aliceOffers: true, 500, alice),
            Htlc(1, 7_000_000, aliceOffers: true, 501, alice),
            Htlc(0, 4_000_000, aliceOffers: false, 502, alice),
            Htlc(1, 4_000_000, aliceOffers: false, 503, alice)
        };
        return alice
                   ? new CommitmentTxSpec(600_000_000, 380_000_000, FeeRatePerKw, htlcs)
                   : new CommitmentTxSpec(380_000_000, 600_000_000, FeeRatePerKw, htlcs);
    }

    private static Htlc Htlc(ulong id, ulong amountMsat, bool aliceOffers, uint expiry, bool aliceView)
    {
        var index = (aliceOffers ? 0 : 2) + (int)id;
        Hash paymentHash = System.Security.Cryptography.SHA256.HashData(s_preimages[index]);
        var direction = aliceOffers == aliceView ? HtlcDirection.Outgoing : HtlcDirection.Incoming;
        return new Htlc(LightningMoney.MilliSatoshis(amountMsat), null, direction, expiry, id, 0, paymentHash,
                        HtlcState.Offered);
    }

    private static CommitmentTransactionModelFactory Factory(LocalLightningSigner signer) =>
        new(new CommitmentKeyDerivationService(s_keyDerivation, signer), signer);

    /// <summary>A simple taproot channel of the kit; each side announces its own to_self_delay.</summary>
    private static ChannelModel CreateChannel(TaprootSignerKit kit, bool alice)
    {
        var dust = LightningMoney.Satoshis(354);
        var aliceParty = new ChannelParty(dust, LightningMoney.Zero, LightningMoney.Zero, 30,
                                          LightningMoney.Satoshis(TaprootSignerKit.CapacitySat), AliceToSelfDelay);
        var bobParty = new ChannelParty(dust, LightningMoney.Zero, LightningMoney.Zero, 30,
                                        LightningMoney.Satoshis(TaprootSignerKit.CapacitySat), BobToSelfDelay);
        var channelParams = new ChannelParams(alice ? aliceParty : bobParty, alice ? bobParty : aliceParty,
                                              LightningMoney.Satoshis(FeeRatePerKw), 3, true, FeatureSupport.No)
        {
            OptionSimpleTaproot = true
        };
        var (local, remote) = alice
                                  ? (kit.AliceBasepoints, kit.BobBasepoints)
                                  : (kit.BobBasepoints, kit.AliceBasepoints);
        var unusedPoint = kit.AliceBasepoints.FundingPubKey;
        var localKeySet = new ChannelKeySetModel(0, local.FundingPubKey, local.RevocationBasepoint,
                                                 local.PaymentBasepoint, local.DelayedPaymentBasepoint,
                                                 local.HtlcBasepoint, unusedPoint);
        var remoteKeySet = new ChannelKeySetModel(0, remote.FundingPubKey, remote.RevocationBasepoint,
                                                  remote.PaymentBasepoint, remote.DelayedPaymentBasepoint,
                                                  remote.HtlcBasepoint, unusedPoint);
        var commitmentNumber = new CommitmentNumber(kit.AliceBasepoints.PaymentBasepoint,
                                                    kit.BobBasepoints.PaymentBasepoint, new Sha256());
        var fundingOutput = new FundingOutputInfo(LightningMoney.Satoshis(TaprootSignerKit.CapacitySat),
                                                  local.FundingPubKey, remote.FundingPubKey)
        {
            TransactionId = kit.FundingTxId,
            Index = 0
        };

        return new ChannelModel(channelParams, TaprootSignerKit.ChannelId, commitmentNumber, fundingOutput, alice,
                                null, null, LightningMoney.Zero, localKeySet, 0, 0, LightningMoney.Zero, remoteKeySet,
                                0, remote.FundingPubKey, 0, ChannelState.OnchainResolving, ChannelVersion.V1,
                                localCommitmentNumber: Number);
    }

    #endregion
}