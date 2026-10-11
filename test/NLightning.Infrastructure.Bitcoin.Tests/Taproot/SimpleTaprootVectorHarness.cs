using System.Diagnostics.CodeAnalysis;
using NBitcoin;
using NLightning.Tests.Utils.Channels;

namespace NLightning.Infrastructure.Bitcoin.Tests.Taproot;

using Bitcoin.Builders;
using Bitcoin.Crypto.Functions;
using Bitcoin.Services;
using Domain.Bitcoin.Interfaces;
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
using Domain.Protocol.Interfaces;
using Domain.Protocol.Models;
using Infrastructure.Crypto.Hashes;

/// <summary>
/// Builds the simple taproot vectors' commitment transactions through the production
/// <see cref="CommitmentTransactionModelFactory"/> (format <see cref="CommitmentFormat.SimpleTaproot"/>),
/// <see cref="CommitmentTransactionBuilder"/>, <see cref="HtlcTransactionModelFactory"/> and
/// <see cref="HtlcTransactionBuilder"/>, from the vectors' balances, feerate, dust limit and HTLCs. The local node is
/// the funder, the commitment is its local commitment number 42 and its keys are the vectors' derived keys.
/// </summary>
[ExcludeFromCodeCoverage]
internal sealed class SimpleTaprootVectorHarness
{
    private static readonly KeyDerivationService s_keyDerivation = new(new Secp256K1Math());

    public SimpleTaprootVectors.TransactionCase Case { get; }
    public CommitmentTransactionModelFactory Factory { get; }
    public CommitmentTransactionBuilder CommitmentBuilder { get; }
    public HtlcTransactionBuilder HtlcBuilder { get; }
    public ChannelModel Channel { get; }

    public static CompactPubKey PerCommitmentPoint => SimpleTaprootVectors.Key("local_per_commit_point");

    /// <summary>The funding output the commitment spends (the vectors' funding transaction, output 0).</summary>
    public static TxOut FundingTxOut => SimpleTaprootVectors.FundingTransaction.Outputs[0];

    public SimpleTaprootVectorHarness(SimpleTaprootVectors.TransactionCase vectorCase)
    {
        Case = vectorCase;
        var nodeOptions = new NodeOptions();
        Factory = new CommitmentTransactionModelFactory(new VectorKeyDerivationService(),
                                                        new Mock<ILightningSigner>().Object);
        CommitmentBuilder = new CommitmentTransactionBuilder(Microsoft.Extensions.Options.Options.Create(nodeOptions));
        HtlcBuilder = new HtlcTransactionBuilder(Microsoft.Extensions.Options.Options.Create(nodeOptions));
        Channel = CreateChannel(LightningMoney.Satoshis(vectorCase.DustLimitSatoshis));
    }

    /// <summary>The local commitment model of the vector, in the simple taproot format.</summary>
    public CommitmentTransactionModel CreateCommitmentModel()
    {
        var htlcs = Case.Htlcs.Select(h => new Htlc(LightningMoney.MilliSatoshis(h.AmountMsat), null,
                                                    h.Incoming ? HtlcDirection.Incoming : HtlcDirection.Outgoing,
                                                    h.Expiry, (ulong)h.Id, 0, h.PaymentHash, HtlcState.Offered));
        var spec = new CommitmentTxSpec(Case.LocalBalanceMsat, Case.RemoteBalanceMsat, Case.FeePerKw, htlcs);
        return Factory.CreateCommitmentTransactionModel(Channel, spec, CommitmentSide.Local,
                                                        SimpleTaprootVectors.CommitHeight,
                                                        CommitmentFormat.SimpleTaproot);
    }

    /// <summary>The commitment, its output map and the HTLC transaction models in commitment output order.</summary>
    public (CommitmentTransactionModel Model, CommitmentTransactionBuildResult Built,
        IReadOnlyList<HtlcTransactionModel> HtlcTxs) Build()
    {
        var model = CreateCommitmentModel();
        var built = CommitmentBuilder.BuildWithOutputMap(model);
        return (model, built, HtlcTransactionModelFactory.CreateHtlcTransactionModels(model, built));
    }

    /// <summary>The preimage of the vector HTLC an HTLC output belongs to.</summary>
    public byte[] PreimageOf(HtlcOutputInfo output) => Case.Htlcs.Single(h => (ulong)h.Id == output.Htlc.Id).Preimage;

    /// <summary>The local node's HTLC key for this commitment (<c>local_htlc_basepoint_secret</c> tweaked).</summary>
    public static Key LocalHtlcKey => DeriveKey("local_htlc_basepoint_secret");

    /// <summary>The remote node's HTLC key for this commitment.</summary>
    public static Key RemoteHtlcKey => DeriveKey("remote_htlc_basepoint_secret");

    /// <summary>The holder's delayed payment key for this commitment.</summary>
    public static Key LocalDelayedKey => DeriveKey("local_delayed_payment_basepoint_secret");

    /// <summary>The revocation key of this commitment (known to the remote node once it is revoked).</summary>
    public static Key RevocationKey =>
        new(s_keyDerivation.DeriveRevocationPrivKey(SimpleTaprootVectors.Key("remote_revocation_basepoint_secret"),
                                                    SimpleTaprootVectors.Key("local_per_commit_secret")));

    /// <summary>The remote node's payment key (<c>option_static_remotekey</c>: its payment basepoint secret).</summary>
    public static Key RemotePaymentKey => SimpleTaprootVectors.PrivKey("remote_payment_basepoint_secret");

    private static Key DeriveKey(string basepointSecret) =>
        new(s_keyDerivation.DerivePrivateKey(SimpleTaprootVectors.Key(basepointSecret), PerCommitmentPoint));

    private static ChannelModel CreateChannel(LightningMoney dustLimit)
    {
        // Balances, feerate and HTLCs come from the CommitmentTxSpec; the channel only carries static data. The format
        // is passed to the factory (no channel type stores option_simple_taproot yet, NL-877 T3)
        var channelParams = TestChannelParams.Create(LightningMoney.Zero, LightningMoney.Zero, LightningMoney.Zero,
                                                     dustLimit, 0, LightningMoney.Zero, 0, true, dustLimit,
                                                     SimpleTaprootVectors.CsvDelay, FeatureSupport.No);
        // Basepoints the factory does not read for the local commitment (the keys come from the stub below)
        CompactPubKey empty = SimpleTaprootVectors.Key("local_per_commit_point");
        var localKeySet = new ChannelKeySetModel(0, SimpleTaprootVectors.Key("local_funding_pubkey"), empty,
                                                 SimpleTaprootVectors.Key("local_payment_basepoint"), empty, empty,
                                                 empty);
        var remoteKeySet = new ChannelKeySetModel(0, SimpleTaprootVectors.Key("remote_funding_pubkey"), empty,
                                                  SimpleTaprootVectors.Key("remote_payment_basepoint"), empty, empty,
                                                  empty);

        // BOLT 3 obscuring factor: SHA256(opener's payment_basepoint || accepter's), the local node is the opener
        var commitmentNumber = new CommitmentNumber(SimpleTaprootVectors.Key("local_payment_basepoint"),
                                                    SimpleTaprootVectors.Key("remote_payment_basepoint"),
                                                    new Sha256());
        var fundingOutput = new FundingOutputInfo(LightningMoney.Satoshis(SimpleTaprootVectors.FundingSatoshis),
                                                  SimpleTaprootVectors.Key("local_funding_pubkey"),
                                                  SimpleTaprootVectors.Key("remote_funding_pubkey"))
        {
            TransactionId = SimpleTaprootVectors.FundingTransaction.GetHash().ToBytes(),
            Index = 0
        };

        return new ChannelModel(channelParams, ChannelId.Zero, commitmentNumber, fundingOutput, true, null, null,
                                LightningMoney.Zero, localKeySet, 0, 0, LightningMoney.Zero, remoteKeySet, 0,
                                SimpleTaprootVectors.Key("remote_funding_pubkey"), 0, ChannelState.V1Opening,
                                ChannelVersion.V1, localCommitmentNumber: SimpleTaprootVectors.CommitHeight);
    }

    /// <summary>The vectors' commitment keys for the local commitment (the per-commitment secret is given).</summary>
    private sealed class VectorKeyDerivationService : ICommitmentKeyDerivationService
    {
        public CommitmentKeys DeriveLocalCommitmentKeys(uint localChannelKeyIndex, ChannelBasepoints localBasepoints,
                                                        ChannelBasepoints remoteBasepoints, ulong commitmentNumber) =>
            new(SimpleTaprootVectors.Key("local_payment_basepoint"),
                SimpleTaprootVectors.Key("derived_local_delayed_pubkey"),
                SimpleTaprootVectors.Key("derived_revocation_pubkey"),
                SimpleTaprootVectors.Key("derived_local_htlc_pubkey"),
                SimpleTaprootVectors.Key("derived_remote_htlc_pubkey"),
                PerCommitmentPoint);

        public CommitmentKeys DeriveRemoteCommitmentKeys(ChannelBasepoints localBasepoints,
                                                         ChannelBasepoints remoteBasepoints,
                                                         CompactPubKey remotePerCommitmentPoint) =>
            throw new NotSupportedException("The vectors are the local node's commitments");
    }
}