using NBitcoin;
using NLightning.Tests.Utils.Channels;

namespace NLightning.Infrastructure.Bitcoin.Tests.Taproot;

using Bitcoin.Builders;
using Bitcoin.Crypto.Functions;
using Bitcoin.Services;
using Bitcoin.Signers;
using Domain.Bitcoin.Transactions.Enums;
using Domain.Bitcoin.Transactions.Factories;
using Domain.Bitcoin.Transactions.Models;
using Domain.Bitcoin.Transactions.Outputs;
using Domain.Channels.Commitments;
using Domain.Channels.Enums;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Money;
using Domain.Node.Options;
using Domain.Protocol.Models;
using Infrastructure.Crypto.Hashes;

/// <summary>
/// NL-904 item 5: one simple taproot state built from both sides through the production factory and builders. Alice
/// builds Bob's commitment as her REMOTE commitment and Bob builds it as his LOCAL one: the same transaction, Alice's
/// MuSig2 partial signature verifies on Bob's side, Bob broadcasts it with a valid key-path witness, and Alice's BIP 340
/// HTLC signatures (<c>SIGHASH_SINGLE|SIGHASH_ANYONECANPAY</c>) pass Bob's validation.
/// </summary>
public class SimpleTaprootMirroredCommitmentTests
{
    private const ulong Number = 9;
    private const ulong FeeRatePerKw = 2_500;

    private static readonly KeyDerivationService s_keyDerivation = new(new Secp256K1Math());

    [Fact]
    public void Given_OneState_When_AliceBuildsTheRemoteAndBobTheLocalCommitment_Then_TheyAreOneSignedTransaction()
    {
        // Arrange
        var kit = new TaprootSignerKit(bobLocalNumber: Number);
        var bobPoint = kit.Bob.GetPerCommitmentPoint(0u, Number);
        var aliceChannel = CreateChannel(kit, alice: true);
        var bobChannel = CreateChannel(kit, alice: false);
        var builder = new CommitmentTransactionBuilder(Microsoft.Extensions.Options.Options.Create(new NodeOptions()));

        // Act: Alice's view of Bob's commitment, and Bob's own
        var aliceModel = Factory(kit.Alice).CreateCommitmentTransactionModel(
            aliceChannel, Spec(alice: true), CommitmentSide.Remote, Number, CommitmentFormat.SimpleTaproot, bobPoint);
        var bobModel = Factory(kit.Bob).CreateCommitmentTransactionModel(
            bobChannel, Spec(alice: false), CommitmentSide.Local, Number, CommitmentFormat.SimpleTaproot);
        var aliceBuilt = builder.BuildWithOutputMap(aliceModel);
        var bobBuilt = builder.BuildWithOutputMap(bobModel);

        // Assert: one transaction, with all four HTLCs untrimmed
        Assert.Equal(bobBuilt.Transaction.TxId, aliceBuilt.Transaction.TxId);
        Assert.Equal(bobBuilt.Transaction.RawTxBytes, aliceBuilt.Transaction.RawTxBytes);
        Assert.Equal(4, bobBuilt.HtlcOutputsInTxOrder.Count);

        // MuSig2: Alice signs with Bob's verification nonce, Bob checks it and broadcasts
        var bobNonce = kit.Bob.GetLocalVerificationNonce(TaprootSignerKit.ChannelId, null, Number);
        var aliceSignature = kit.Alice.SignRemoteCommitmentPartial(TaprootSignerKit.ChannelId, null,
                                                                   aliceBuilt.Transaction, bobNonce);
        kit.Bob.ValidateLocalCommitmentPartialSignature(TaprootSignerKit.ChannelId, null, Number, aliceSignature,
                                                        bobBuilt.Transaction);
        var signed = kit.Bob.SignLocalCommitmentForBroadcast(TaprootSignerKit.ChannelId, null, Number,
                                                             bobBuilt.Transaction, aliceSignature);
        Assert.Null(TaprootSignerKit.Execute(signed, kit.FundingTxOut));

        // HTLC signatures: Alice signs the HTLC transactions of her remote view, Bob validates them on his local one
        var htlcBuilder = new HtlcTransactionBuilder(Microsoft.Extensions.Options.Options.Create(new NodeOptions()));
        var aliceContexts = Contexts(htlcBuilder, aliceModel, aliceBuilt, bobPoint);
        var bobContexts = Contexts(htlcBuilder, bobModel, bobBuilt, bobPoint);
        for (var i = 0; i < bobContexts.Count; i++)
            Assert.Equal(bobContexts[i].HtlcTransaction.Transaction.RawTxBytes,
                         aliceContexts[i].HtlcTransaction.Transaction.RawTxBytes);

        var htlcSignatures = kit.Alice.SignRemoteHtlcTransactions(TaprootSignerKit.ChannelId, aliceContexts);
        kit.Bob.ValidateLocalHtlcSignatures(TaprootSignerKit.ChannelId, bobContexts, htlcSignatures);

        // Bob's own HTLC transactions then spend his commitment's HTLC outputs
        var commitmentTx = Transaction.Load(signed.RawTxBytes, Network.Main);
        var htlcTxModels = HtlcTransactionModelFactory.CreateHtlcTransactionModels(bobModel, bobBuilt);
        for (var i = 0; i < bobContexts.Count; i++)
        {
            var localSignature = kit.Bob.SignLocalHtlcTransaction(TaprootSignerKit.ChannelId, bobContexts[i]);
            var preimage = htlcTxModels[i].Type == HtlcTransactionType.Success ? Preimage(htlcTxModels[i]) : null;
            var htlcTx = htlcBuilder.AddWitness(htlcTxModels[i], bobContexts[i].HtlcTransaction, htlcSignatures[i],
                                                localSignature, preimage);
            var spent = commitmentTx.Outputs[bobBuilt.HtlcOutputsInTxOrder[i].Vout];
            Assert.Null(Transaction.Load(htlcTx.RawTxBytes, Network.Main).CreateValidator([spent]).ValidateInput(0)
                                   .Error);
        }
    }

    [Fact]
    public void Given_AnHtlcContextWithoutAnchors_When_SigningOrValidating_Then_ItThrowsASignerException()
    {
        // Arrange (NL-904 item 7): simple taproot keeps the anchors semantics
        var kit = new TaprootSignerKit(bobLocalNumber: Number);
        var bobPoint = kit.Bob.GetPerCommitmentPoint(0u, Number);
        var bobModel = Factory(kit.Bob).CreateCommitmentTransactionModel(
            CreateChannel(kit, alice: false), Spec(alice: false), CommitmentSide.Local, Number,
            CommitmentFormat.SimpleTaproot);
        var builder = new CommitmentTransactionBuilder(Microsoft.Extensions.Options.Options.Create(new NodeOptions()));
        var htlcBuilder = new HtlcTransactionBuilder(Microsoft.Extensions.Options.Options.Create(new NodeOptions()));
        var contexts = Contexts(htlcBuilder, bobModel, builder.BuildWithOutputMap(bobModel), bobPoint)
                      .Select(c => c with { HasAnchors = false })
                      .ToList();

        // Act / Assert
        Assert.Throws<Domain.Exceptions.SignerException>(
            () => kit.Bob.SignLocalHtlcTransaction(TaprootSignerKit.ChannelId, contexts[0]));
        Assert.Throws<Domain.Exceptions.SignerException>(
            () => kit.Alice.SignRemoteHtlcTransactions(TaprootSignerKit.ChannelId, contexts));
        Assert.Throws<Domain.Exceptions.SignerException>(
            () => kit.Bob.ValidateLocalHtlcSignatures(TaprootSignerKit.ChannelId, contexts,
                                                      contexts.Select(_ => (CompactSignature)new byte[64]).ToList()));
    }

    private static readonly byte[][] s_preimages =
        Enumerable.Range(1, 4).Select(i => Enumerable.Repeat((byte)i, 32).ToArray()).ToArray();

    private static byte[] Preimage(HtlcTransactionModel model) =>
        s_preimages.Single(p => ((byte[])model.SpentOutput.Htlc.PaymentHash).AsSpan()
                                                                            .SequenceEqual(
                                                                                 System.Security.Cryptography.SHA256
                                                                                    .HashData(p)));

    /// <summary>
    /// Alice's view: 600,000 sat local, 380,000 sat remote, two HTLCs each way (ids from each side's counter).
    /// </summary>
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

    private static IReadOnlyList<HtlcSigningContext> Contexts(HtlcTransactionBuilder htlcBuilder,
                                                              CommitmentTransactionModel model,
                                                              CommitmentTransactionBuildResult built,
                                                              CompactPubKey point) =>
        HtlcTransactionModelFactory.CreateHtlcTransactionModels(model, built)
                                   .Select(h => new HtlcSigningContext(htlcBuilder.Build(h), point, true))
                                   .ToList();

    private static CommitmentTransactionModelFactory Factory(LocalLightningSigner signer) =>
        new(new CommitmentKeyDerivationService(s_keyDerivation, signer), signer);

    private static ChannelModel CreateChannel(TaprootSignerKit kit, bool alice)
    {
        var dust = LightningMoney.Satoshis(354);
        var channelParams = TestChannelParams.Create(LightningMoney.Zero, LightningMoney.Zero, LightningMoney.Zero,
                                                     dust, 0, LightningMoney.Zero, 0, true, dust, 144,
                                                     FeatureSupport.No);
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

        // BOLT 3 obscuring factor: SHA256(opener's payment_basepoint || accepter's); Alice opened
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
                                0, remote.FundingPubKey, 0, ChannelState.V1Opening, ChannelVersion.V1,
                                localCommitmentNumber: Number);
    }
}