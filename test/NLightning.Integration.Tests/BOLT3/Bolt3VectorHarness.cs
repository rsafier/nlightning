using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NBitcoin;
using NBitcoin.Crypto;
using NLightning.Tests.Utils.Vectors;

namespace NLightning.Integration.Tests.BOLT3;

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
using Domain.Protocol.Models;
using Infrastructure.Bitcoin.Builders;
using Infrastructure.Bitcoin.Signers;
using Infrastructure.Crypto.Hashes;
using Mocks;
using Vectors;

/// <summary>
/// Builds BOLT 3 Appendix C/F commitment transactions straight from a vector's <c>to_local_msat</c>,
/// <c>to_remote_msat</c>, feerate, dust limit and HTLC set through the spec-driven
/// <see cref="CommitmentTransactionModelFactory"/> (no balance adjustments), and assembles fully signed transactions
/// so they can be compared byte for byte with the spec.
/// </summary>
internal sealed class Bolt3VectorHarness
{
    private static readonly CompactPubKey s_emptyCompactPubKey = new([
        0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00
    ]);

    // Appendix C HTLCs 0-6: amount_msat, expiry, direction (from the local node's point of view)
    private static readonly (ulong AmountMsat, uint Expiry, HtlcDirection Direction, byte[] PaymentHash)[] s_htlcs =
    [
        (1_000_000, 500, HtlcDirection.Incoming, Bolt3AppendixCVectors.Htlc0PaymentHash),
        (2_000_000, 501, HtlcDirection.Incoming, Bolt3AppendixCVectors.Htlc1PaymentHash),
        (2_000_000, 502, HtlcDirection.Outgoing, Bolt3AppendixCVectors.Htlc2PaymentHash),
        (3_000_000, 503, HtlcDirection.Outgoing, Bolt3AppendixCVectors.Htlc3PaymentHash),
        (4_000_000, 504, HtlcDirection.Incoming, Bolt3AppendixCVectors.Htlc4PaymentHash),
        (5_000_000, 506, HtlcDirection.Outgoing, Bolt3AppendixCVectors.Htlc5PaymentHash),
        (5_000_001, 505, HtlcDirection.Outgoing, Bolt3AppendixCVectors.Htlc6PaymentHash)
    ];

    public static readonly byte[][] Preimages =
    [
        Bolt3AppendixCVectors.Htlc0Preimage, Bolt3AppendixCVectors.Htlc1Preimage,
        Bolt3AppendixCVectors.Htlc2Preimage, Bolt3AppendixCVectors.Htlc3Preimage,
        Bolt3AppendixCVectors.Htlc4Preimage, Bolt3AppendixCVectors.Htlc5Preimage,
        Bolt3AppendixCVectors.Htlc6Preimage
    ];

    public Bolt3TestLightningSigner Signer { get; }
    public CommitmentTransactionModelFactory Factory { get; }
    public CommitmentTransactionBuilder CommitmentBuilder { get; }
    public ChannelModel Channel { get; }
    public Bolt3CommitmentVector Vector { get; }
    public bool HasAnchors { get; }

    public Bolt3VectorHarness(Bolt3CommitmentVector vector, bool hasAnchors)
    {
        Vector = vector;
        HasAnchors = hasAnchors;

        var nodeOptions = new NodeOptions { DustLimitAmount = LightningMoney.Satoshis(vector.DustLimitSatoshis) };
        Signer = new Bolt3TestLightningSigner(nodeOptions, new Mock<ILogger<LocalLightningSigner>>().Object);
        Signer.RegisterChannel(ChannelId.Zero,
                               new ChannelSigningInfo(Bolt3AppendixBVectors.ExpectedTxId.ToBytes(),
                                                      Bolt3AppendixBVectors.InputIndex,
                                                      Bolt3AppendixBVectors.FundingSatoshis,
                                                      Bolt3AppendixCVectors.NodeAFundingPubkey.ToBytes(),
                                                      Bolt3AppendixCVectors.NodeBFundingPubkey.ToBytes(), 0));
        Factory = new CommitmentTransactionModelFactory(new Bolt3TestCommitmentKeyDerivationService(), Signer);
        CommitmentBuilder = new CommitmentTransactionBuilder(Options.Create(nodeOptions));
        Channel = CreateChannel(nodeOptions.DustLimitAmount, hasAnchors);
    }

    public static Htlc GetHtlc(int id)
    {
        var (amountMsat, expiry, direction, paymentHash) = s_htlcs[id];
        return new Htlc(LightningMoney.MilliSatoshis(amountMsat), null!, direction, expiry, (ulong)id, 0, paymentHash,
                        HtlcState.Offered);
    }

    public CommitmentSpec Spec =>
        new(Vector.ToLocalMsat, Vector.ToRemoteMsat, Vector.FeeRatePerKw, Vector.HtlcIds.Select(GetHtlc));

    public CommitmentTransactionModel CreateCommitmentModel() =>
        Factory.CreateCommitmentTransactionModel(Channel, Spec, CommitmentSide.Local, Channel.LocalCommitmentNumber);

    /// <summary>
    /// Signs the unsigned commitment with the local funding key, checks the vector's remote signature, and returns
    /// the transaction with its 2-of-2 witness (signatures in funding-script key order).
    /// </summary>
    public Transaction SignCommitment(SignedTransaction unsignedCommitment)
    {
        var remoteSignature = new ECDSASignature(Convert.FromHexString(Vector.RemoteSigHex));
        Signer.ValidateSignature(ChannelId.Zero, remoteSignature.ToCompact(), unsignedCommitment);
        var localCompact = Signer.SignChannelTransaction(ChannelId.Zero, unsignedCommitment);
        Assert.True(ECDSASignature.TryParseFromCompact(localCompact, out var localSignature));

        var localKey = Bolt3AppendixCVectors.NodeAFundingPubkey;
        var remoteKey = Bolt3AppendixCVectors.NodeBFundingPubkey;
        var fundingScript = PayToMultiSigTemplate.Instance.GenerateScriptPubKey(2, localKey, remoteKey);
        var localFirst = localKey.CompareTo(remoteKey) < 0;
        var localSig = new TransactionSignature(localSignature, SigHash.All).ToBytes();
        var remoteSig = new TransactionSignature(remoteSignature, SigHash.All).ToBytes();

        var tx = Transaction.Load(unsignedCommitment.RawTxBytes, Network.Main);
        tx.Inputs[0].WitScript = new WitScript(new[]
        {
            Array.Empty<byte>(), localFirst ? localSig : remoteSig, localFirst ? remoteSig : localSig,
            fundingScript.ToBytes()
        });
        return tx;
    }

    private ChannelModel CreateChannel(LightningMoney dustLimit, bool hasAnchors)
    {
        // Balances, feerate and HTLCs come from the CommitmentSpec; the channel only carries static data
        var channelConfig = new ChannelConfig(LightningMoney.Zero, LightningMoney.Zero, LightningMoney.Zero, dustLimit,
                                              0, LightningMoney.Zero, 0, hasAnchors, dustLimit,
                                              Bolt3AppendixCVectors.LocalDelay, FeatureSupport.No);
        var localKeySet = new ChannelKeySetModel(0, Bolt3AppendixCVectors.NodeAFundingPubkey.ToBytes(),
                                                 s_emptyCompactPubKey,
                                                 Bolt3AppendixCVectors.NodeAPaymentBasepoint.ToBytes(),
                                                 s_emptyCompactPubKey, s_emptyCompactPubKey, s_emptyCompactPubKey);
        var remoteKeySet = new ChannelKeySetModel(0, Bolt3AppendixCVectors.NodeBFundingPubkey.ToBytes(),
                                                  s_emptyCompactPubKey,
                                                  Bolt3AppendixCVectors.NodeBPaymentBasepoint.ToBytes(),
                                                  s_emptyCompactPubKey, s_emptyCompactPubKey, s_emptyCompactPubKey);
        var commitmentNumber = new CommitmentNumber(Bolt3AppendixCVectors.NodeAPaymentBasepoint.ToBytes(),
                                                    Bolt3AppendixCVectors.NodeBPaymentBasepoint.ToBytes(),
                                                    new Sha256());
        var fundingOutputInfo = new FundingOutputInfo(Bolt3AppendixBVectors.FundingSatoshis,
                                                      Bolt3AppendixCVectors.NodeAFundingPubkey.ToBytes(),
                                                      Bolt3AppendixCVectors.NodeBFundingPubkey.ToBytes())
        {
            TransactionId = Bolt3AppendixBVectors.ExpectedTxId.ToBytes(),
            Index = 0
        };

        return new ChannelModel(channelConfig, ChannelId.Zero, commitmentNumber, fundingOutputInfo, true, null, null,
                                LightningMoney.Zero, localKeySet, 0, 0, LightningMoney.Zero, remoteKeySet, 0,
                                Bolt3AppendixBVectors.RemotePubKey.ToBytes(), 0, ChannelState.V1Opening,
                                ChannelVersion.V1, localCommitmentNumber: Bolt3AppendixCVectors.CommitmentNumber);
    }
}