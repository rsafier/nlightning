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
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Commitments;
using Domain.Channels.Enums;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Money;
using Domain.Node.Options;
using Domain.Onchain.Fees;
using Domain.Onchain.Models;
using Domain.Protocol.Models;
using Infrastructure.Crypto.Hashes;

/// <summary>
/// NL-904 item 4 / NL-966 (1): Bob's own simple taproot HTLC-timeout and HTLC-success transactions pay no fee, so they
/// are combined with wallet fee inputs (P2WPKH and P2TR) and a change output before Bob signs: his BIP 340
/// <c>SIGHASH_DEFAULT</c> signature commits to every spent output, while Alice's stored
/// <c>SIGHASH_SINGLE|SIGHASH_ANYONECANPAY</c> (0x83) signature still covers input and output 0. Every input of every
/// combined transaction is executed by NBitcoin's interpreter against all the outputs it spends.
/// </summary>
public class SimpleTaprootHtlcFeeInputTests
{
    private const ulong Number = 9;
    private const ulong FeeRatePerKw = 2_500;
    private const uint HtlcFeeratePerKw = 3_000;

    private static readonly KeyDerivationService s_keyDerivation = new(new Secp256K1Math());
    private static readonly Key s_p2wpkhKey = new(Enumerable.Repeat((byte)0x31, 32).ToArray());
    private static readonly Key s_p2trKey = new(Enumerable.Repeat((byte)0x32, 32).ToArray());
    private static readonly byte[] s_changeScript = new Key(Enumerable.Repeat((byte)0x33, 32).ToArray())
                                                   .PubKey.WitHash.ScriptPubKey.ToBytes();

    private static readonly byte[][] s_preimages =
        Enumerable.Range(1, 4).Select(i => Enumerable.Repeat((byte)i, 32).ToArray()).ToArray();

    [Theory]
    [InlineData(HtlcTransactionType.Timeout, 1)]
    [InlineData(HtlcTransactionType.Timeout, 2)]
    [InlineData(HtlcTransactionType.Success, 1)]
    [InlineData(HtlcTransactionType.Success, 2)]
    public void Given_OurTaprootHtlcTransaction_When_CombinedWithFeeInputs_Then_EveryInputVerifies(
        HtlcTransactionType type, int feeInputCount)
    {
        // Arrange: Bob's commitment with Alice's 0x83 HTLC signatures, and the wallet's outputs (P2WPKH, then P2TR)
        var setup = new Setup();
        var index = setup.IndexOf(type);
        var wallet = WalletOutputs(feeInputCount);
        var feeInputs = wallet.Select(w => w.Input).ToList();

        // Act
        var (signed, combined) = setup.SignCombined(index, feeInputs, HtlcFeeratePerKw);

        // Assert: HTLC input 0 and output 0 (the peer's SINGLE|ANYONECANPAY pair), fee inputs after it, change after it
        var tx = Transaction.Load(signed.RawTxBytes, Network.Main);
        Assert.Equal(1 + feeInputCount, tx.Inputs.Count);
        Assert.Equal(new OutPoint(setup.Commitment, setup.Vout(index)), tx.Inputs[0].PrevOut);
        Assert.Equal(1u, (uint)tx.Inputs[0].Sequence);
        Assert.Equal((ulong)setup.Commitment.Outputs[(int)setup.Vout(index)].Value.Satoshi,
                     (ulong)tx.Outputs[0].Value.Satoshi);
        Assert.Equal(2, tx.Outputs.Count);
        Assert.Equal(s_changeScript, tx.Outputs[1].ScriptPubKey.ToBytes());
        Assert.Equal(type == HtlcTransactionType.Success ? 5 : 4, tx.Inputs[0].WitScript.PushCount);
        Assert.Equal(65, tx.Inputs[0].WitScript[0].Length); // the peer's signature carries 0x83
        Assert.Equal(64, tx.Inputs[0].WitScript[1].Length); // ours is SIGHASH_DEFAULT

        var spentOutputs = setup.SpentOutputs(index, wallet);
        var validator = tx.CreateValidator(spentOutputs);
        for (var i = 0; i < tx.Inputs.Count; i++)
            Assert.True(validator.ValidateInput(i).Error is null,
                        $"input {i}: {validator.ValidateInput(i).Error}");

        // The fee is what was decided, and the estimate is an upper bound of the signed weight
        var paid = spentOutputs.Sum(o => o.Value.Satoshi) - tx.Outputs.Sum(o => o.Value.Satoshi);
        Assert.Equal((long)combined.FeeSat, paid);
        Assert.True(tx.GetVirtualSize() * 4 <= combined.EstimatedWeight,
                    $"{tx.GetVirtualSize() * 4} > {combined.EstimatedWeight}");
        Assert.True(combined.FeeSat >= SweepWeights.FeeSat(HtlcFeeratePerKw, tx.GetVirtualSize() * 4));
    }

    [Fact]
    public void Given_ACombinedTaprootHtlcTransaction_When_RbfReplacedAtAHigherFee_Then_ItIsSignedAgainAndVerifies()
    {
        // Arrange: the first attempt at 3,000 sat/kw
        var setup = new Setup();
        var index = setup.IndexOf(HtlcTransactionType.Timeout);
        var wallet = WalletOutputs(2);
        var feeInputs = wallet.Select(w => w.Input).ToList();
        var (first, firstCombined) = setup.SignCombined(index, feeInputs, HtlcFeeratePerKw);

        // Act: the replacement over the same inputs pays more (a smaller change), so every signature is made again
        var (second, secondCombined) = setup.SignCombined(index, feeInputs, HtlcFeeratePerKw * 3);

        // Assert
        Assert.True(secondCombined.FeeSat > firstCombined.FeeSat);
        Assert.NotEqual(first.TxId, second.TxId);
        var tx = Transaction.Load(second.RawTxBytes, Network.Main);
        var validator = tx.CreateValidator(setup.SpentOutputs(index, wallet));
        for (var i = 0; i < tx.Inputs.Count; i++)
            Assert.Null(validator.ValidateInput(i).Error);

        // The first attempt's signature does not carry over: our SIGHASH_DEFAULT one committed to its change output
        var stale = tx.Clone();
        stale.Inputs[0].WitScript = Transaction.Load(first.RawTxBytes, Network.Main).Inputs[0].WitScript;
        Assert.NotNull(stale.CreateValidator(setup.SpentOutputs(index, wallet)).ValidateInput(0).Error);
    }

    [Fact]
    public void Given_ACombinedTaprootHtlcTransaction_When_TheFeeInputOutputsAreMissing_Then_TheSignerRefuses()
    {
        // Arrange: the combined transaction without the outputs its fee inputs spend
        var setup = new Setup();
        var index = setup.IndexOf(HtlcTransactionType.Timeout);
        var feeInputs = WalletOutputs(1).Select(w => w.Input).ToList();
        var combined = setup.HtlcBuilder.AddFeeInputs(setup.Models[index], setup.Contexts[index].HtlcTransaction,
                                                      feeInputs, s_changeScript, HtlcFeeratePerKw);
        var stripped = combined.BuildResult with { FeeInputSpentOutputs = null };
        var wrong = combined.BuildResult with
        {
            FeeInputSpentOutputs =
            [
                combined.BuildResult.FeeInputSpentOutputs![0] with { Index = 7 }
            ]
        };

        // Act / Assert: no signature over an incomplete or mismatched set of spent outputs
        Assert.ThrowsAny<Exception>(() => setup.Kit.Bob.SignLocalHtlcTransaction(
                                        TaprootSignerKit.ChannelId,
                                        new HtlcSigningContext(stripped, setup.BobPoint, true)));
        Assert.ThrowsAny<Exception>(() => setup.Kit.Bob.SignLocalHtlcTransaction(
                                        TaprootSignerKit.ChannelId, new HtlcSigningContext(wrong, setup.BobPoint, true)));

        // The peer's (counterparty) signature never covers fee inputs
        Assert.ThrowsAny<Exception>(() => setup.Kit.Alice.SignRemoteHtlcTransactions(
                                        TaprootSignerKit.ChannelId,
                                        [new HtlcSigningContext(combined.BuildResult, setup.BobPoint, true)]));
    }

    [Fact]
    public void Given_ATaprootHtlcTransaction_When_TheBaseWeightIsEstimated_Then_ItBoundsTheSignedTransaction()
    {
        // Arrange
        var setup = new Setup();
        foreach (var type in new[] { HtlcTransactionType.Timeout, HtlcTransactionType.Success })
        {
            var index = setup.IndexOf(type);
            var wallet = WalletOutputs(1);

            // Act
            var baseWeight = setup.HtlcBuilder.EstimateAnchorBaseWeight(setup.Models[index],
                                                                        setup.Contexts[index].HtlcTransaction,
                                                                        s_changeScript.Length);
            var (signed, _) = setup.SignCombined(index, wallet.Select(w => w.Input).ToList(), HtlcFeeratePerKw);

            // Assert: the base weight plus the fee input's own weight bounds the signed transaction
            var tx = Transaction.Load(signed.RawTxBytes, Network.Main);
            Assert.True(tx.GetVirtualSize() * 4 <= baseWeight + wallet[0].Input.InputWeight);
            Assert.True(tx.GetVirtualSize() * 4 >= baseWeight + wallet[0].Input.InputWeight - 16);
        }
    }

    #region Helpers

    /// <summary>
    /// The wallet's outputs: P2WPKH first, then P2TR (key path, BIP 86), each in its own funding transaction.
    /// </summary>
    private static List<(AnchorFeeInput Input, Transaction Funding, Key Key, bool Taproot)> WalletOutputs(int count)
    {
        var outputs = new List<(AnchorFeeInput, Transaction, Key, bool)>();
        for (var i = 0; i < count; i++)
        {
            var taproot = i % 2 == 1;
            var key = taproot ? s_p2trKey : s_p2wpkhKey;
            var script = taproot
                             ? key.PubKey.GetTaprootFullPubKey().ScriptPubKey
                             : key.PubKey.WitHash.ScriptPubKey;
            var funding = Transaction.Create(Network.Main);
            funding.Inputs.Add(new OutPoint(new uint256((ulong)(100 + i)), 0));
            funding.Outputs.Add(new TxOut(Money.Satoshis(20_000), script));
            var input = new AnchorFeeInput(funding.GetHash().ToBytes(), 0, 20_000, script.ToBytes(),
                                           taproot ? 230 : AnchorFeeInput.P2WpkhInputWeight);
            outputs.Add((input, funding, key, taproot));
        }

        return outputs;
    }

    private sealed class Setup
    {
        public TaprootSignerKit Kit { get; } = new(bobLocalNumber: Number);
        public CompactPubKey BobPoint { get; }
        public HtlcTransactionBuilder HtlcBuilder { get; } =
            new(Microsoft.Extensions.Options.Options.Create(new NodeOptions()));
        public Transaction Commitment { get; }
        public IReadOnlyList<HtlcTransactionModel> Models { get; }
        public IReadOnlyList<HtlcSigningContext> Contexts { get; }
        public IReadOnlyList<CompactSignature> RemoteSignatures { get; }

        private readonly CommitmentTransactionBuildResult _bobBuilt;

        public Setup()
        {
            BobPoint = Kit.Bob.GetPerCommitmentPoint(0u, Number);
            var builder = new CommitmentTransactionBuilder(
                Microsoft.Extensions.Options.Options.Create(new NodeOptions()));
            var aliceModel = Factory(Kit.Alice).CreateCommitmentTransactionModel(
                CreateChannel(Kit, alice: true), Spec(alice: true), CommitmentSide.Remote, Number,
                CommitmentFormat.SimpleTaproot, BobPoint);
            var bobModel = Factory(Kit.Bob).CreateCommitmentTransactionModel(
                CreateChannel(Kit, alice: false), Spec(alice: false), CommitmentSide.Local, Number,
                CommitmentFormat.SimpleTaproot);
            var aliceBuilt = builder.BuildWithOutputMap(aliceModel);
            _bobBuilt = builder.BuildWithOutputMap(bobModel);
            Commitment = Transaction.Load(_bobBuilt.Transaction.RawTxBytes, Network.Main);

            Models = HtlcTransactionModelFactory.CreateHtlcTransactionModels(bobModel, _bobBuilt);
            Contexts = Models.Select(h => new HtlcSigningContext(HtlcBuilder.Build(h), BobPoint, true)).ToList();
            var aliceContexts = HtlcTransactionModelFactory.CreateHtlcTransactionModels(aliceModel, aliceBuilt)
                                                           .Select(h => new HtlcSigningContext(
                                                                       HtlcBuilder.Build(h), BobPoint, true))
                                                           .ToList();
            RemoteSignatures = Kit.Alice.SignRemoteHtlcTransactions(TaprootSignerKit.ChannelId, aliceContexts);
            Kit.Bob.ValidateLocalHtlcSignatures(TaprootSignerKit.ChannelId, Contexts, RemoteSignatures);
        }

        public int IndexOf(HtlcTransactionType type) => Models.ToList().FindIndex(m => m.Type == type);

        public uint Vout(int index) => _bobBuilt.HtlcOutputsInTxOrder[index].Vout;

        /// <summary>Combines HTLC transaction <paramref name="index"/> with the fee inputs, then signs every input:
        /// ours over the combined transaction, the wallet's after the HTLC witness.</summary>
        public (SignedTransaction Signed, AnchorHtlcTransaction Combined) SignCombined(
            int index, IReadOnlyList<AnchorFeeInput> feeInputs, uint feeratePerKw)
        {
            var model = Models[index];
            var combined = HtlcBuilder.AddFeeInputs(model, Contexts[index].HtlcTransaction, feeInputs,
                                                    s_changeScript, feeratePerKw);
            var localSignature = Kit.Bob.SignLocalHtlcTransaction(
                TaprootSignerKit.ChannelId, new HtlcSigningContext(combined.BuildResult, BobPoint, true));
            var preimage = model.Type == HtlcTransactionType.Success ? Preimage(model) : null;
            var withHtlc = HtlcBuilder.AddWitness(model, combined.BuildResult, RemoteSignatures[index],
                                                  localSignature, preimage);

            var tx = Transaction.Load(withHtlc.RawTxBytes, Network.Main);
            var spent = new List<TxOut> { Commitment.Outputs[(int)Vout(index)] };
            spent.AddRange(feeInputs.Select(f => new TxOut(Money.Satoshis(f.AmountSat), new Script(f.ScriptPubKey))));
            for (var i = 1; i < tx.Inputs.Count; i++)
            {
                var input = feeInputs[i - 1];
                if (input.ScriptPubKey[0] == 0x51)
                {
                    var keyPair = s_p2trKey.CreateTaprootKeyPair();
                    var hash = tx.GetSignatureHashTaproot(spent.ToArray(), new TaprootExecutionData(i));
                    tx.Inputs[i].WitScript = new WitScript(Op.GetPushOp(keyPair.SignTaprootKeySpend(hash, TaprootSigHash.Default).ToBytes()));
                }
                else
                {
                    var hash = tx.GetSignatureHash(s_p2wpkhKey.PubKey.Hash.ScriptPubKey, i, SigHash.All, spent[i],
                                                   HashVersion.WitnessV0);
                    tx.Inputs[i].WitScript = PayToWitPubKeyHashTemplate.Instance.GenerateWitScript(
                        new TransactionSignature(s_p2wpkhKey.Sign(hash), SigHash.All), s_p2wpkhKey.PubKey);
                }
            }

            Assert.Equal(withHtlc.TxId, new TxId(tx.GetHash().ToBytes()));
            return (new SignedTransaction(tx.GetHash().ToBytes(), tx.ToBytes()), combined);
        }

        public TxOut[] SpentOutputs(int index,
                                    IReadOnlyList<(AnchorFeeInput Input, Transaction Funding, Key Key, bool Taproot)>
                                        wallet) =>
            new[] { Commitment.Outputs[(int)Vout(index)] }
               .Concat(wallet.Select(w => w.Funding.Outputs[0]))
               .ToArray();
    }

    private static byte[] Preimage(HtlcTransactionModel model) =>
        s_preimages.Single(p => ((byte[])model.SpentOutput.Htlc.PaymentHash).AsSpan()
                                                                            .SequenceEqual(
                                                                                 System.Security.Cryptography.SHA256
                                                                                    .HashData(p)));

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

    #endregion
}
