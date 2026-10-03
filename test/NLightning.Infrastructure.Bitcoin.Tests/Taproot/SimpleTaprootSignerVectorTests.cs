using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;

namespace NLightning.Infrastructure.Bitcoin.Tests.Taproot;

using Bitcoin.Builders;
using Bitcoin.Crypto.Functions;
using Bitcoin.Services;
using Bitcoin.Signers;
using Bitcoin.Taproot;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.Transactions.Enums;
using Domain.Bitcoin.Transactions.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Exceptions;
using Domain.Node.Options;
using Domain.Protocol.Interfaces;

/// <summary>
/// The simple taproot HTLC signatures through <see cref="LocalLightningSigner"/>, the production path a channel uses:
/// the local node checks the vectors' remote signatures (BIP 340, <c>SIGHASH_SINGLE|SIGHASH_ANYONECANPAY</c>) and signs
/// its own HTLC transactions (BIP 340, <c>SIGHASH_DEFAULT</c>, fresh aux randomness); the remote node signs the local
/// node's HTLC transactions. The signed transactions are executed against the commitment outputs they spend.
/// </summary>
public class SimpleTaprootSignerVectorTests
{
    // The five-HTLC case: two offered and three received HTLC outputs
    private static SimpleTaprootVectors.TransactionCase FiveHtlcCase => SimpleTaprootVectors.Transactions[1];

    [Fact]
    public void Given_TheVectorsRemoteSignatures_When_TheLocalSignerValidatesThem_Then_TheyAreAccepted()
    {
        // Arrange
        var (contexts, _, _) = BuildContexts(FiveHtlcCase);
        var signer = new VectorSigner(asRemote: false);
        var signatures = FiveHtlcCase.HtlcDescs.Select(d => (CompactSignature)d.RemoteSignature).ToList();

        // Act
        var exception = Record.Exception(() => signer.ValidateLocalHtlcSignatures(ChannelId.Zero, contexts,
                                                                                   signatures));

        // Assert
        Assert.Null(exception);
    }

    [Fact]
    public void Given_ATamperedRemoteSignature_When_TheLocalSignerValidatesIt_Then_ItThrows()
    {
        // Arrange
        var (contexts, _, _) = BuildContexts(FiveHtlcCase);
        var signer = new VectorSigner(asRemote: false);
        var signatures = FiveHtlcCase.HtlcDescs.Select(d => d.RemoteSignature.ToArray()).ToList();
        signatures[2][10] ^= 0x01;

        // Act / Assert
        var exception = Assert.Throws<SignerException>(() => signer.ValidateLocalHtlcSignatures(
                                                           ChannelId.Zero, contexts,
                                                           signatures.Select(s => (CompactSignature)s).ToList()));
        Assert.Contains("HTLC signature 2", exception.Message);
    }

    [Fact]
    public void Given_SignaturesInTheWrongOrder_When_TheLocalSignerValidatesThem_Then_ItThrows()
    {
        // Arrange: each signature commits to its own HTLC transaction
        var (contexts, _, _) = BuildContexts(FiveHtlcCase);
        var signer = new VectorSigner(asRemote: false);
        var signatures = FiveHtlcCase.HtlcDescs.Select(d => (CompactSignature)d.RemoteSignature).Reverse().ToList();

        // Act / Assert
        Assert.Throws<SignerException>(() => signer.ValidateLocalHtlcSignatures(ChannelId.Zero, contexts,
                                                                                 signatures));
    }

    [Fact]
    public void Given_OneSignatureMissing_When_TheLocalSignerValidates_Then_ItThrows()
    {
        // Arrange
        var (contexts, _, _) = BuildContexts(FiveHtlcCase);
        var signer = new VectorSigner(asRemote: false);
        var signatures = FiveHtlcCase.HtlcDescs.Skip(1).Select(d => (CompactSignature)d.RemoteSignature).ToList();

        // Act / Assert
        Assert.Throws<SignerException>(() => signer.ValidateLocalHtlcSignatures(ChannelId.Zero, contexts,
                                                                                 signatures));
    }

    [Fact]
    public void Given_TheLocalSigner_When_SigningItsHtlcTransactions_Then_EachSignedTransactionIsValid()
    {
        // Arrange
        var (contexts, harness, htlcTxs) = BuildContexts(FiveHtlcCase);
        var (_, built, _) = harness.Build();
        var commitment = Transaction.Load(built.Transaction.RawTxBytes, Network.Main);
        var signer = new VectorSigner(asRemote: false);
        var localHtlcPubKey = SimpleTaprootVectorHarness.LocalHtlcKey.PubKey;

        for (var i = 0; i < contexts.Count; i++)
        {
            // Act
            var signature = signer.SignLocalHtlcTransaction(ChannelId.Zero, contexts[i]);
            var preimage = htlcTxs[i].Type == HtlcTransactionType.Success
                               ? harness.PreimageOf(htlcTxs[i].SpentOutput)
                               : null;
            var signed = harness.HtlcBuilder.AddWitness(htlcTxs[i], contexts[i].HtlcTransaction,
                                                        FiveHtlcCase.HtlcDescs[i].RemoteSignature, signature,
                                                        preimage);
            var tx = Transaction.Load(signed.RawTxBytes, Network.Main);
            var expected = Transaction.Parse(FiveHtlcCase.HtlcDescs[i].ResolutionTxHex, Network.Main);

            // Assert: a SIGHASH_DEFAULT signature (64 bytes) by our HTLC key, the vector's transaction, valid
            var sigHash = TaprootSignatures.ComputeHtlcSigHash(contexts[i].HtlcTransaction,
                                                               TaprootSignatures.HolderHtlcSigHash, Network.Main);
            Assert.Equal(64, ((byte[])signature).Length);
            Assert.True(TaprootSignatures.Verify(localHtlcPubKey, sigHash, (byte[])signature));
            Assert.Equal(expected.GetHash(), tx.GetHash());
            Assert.Null(tx.CreateValidator([commitment.Outputs[built.HtlcOutputsInTxOrder[i].Vout]])
                          .ValidateInput(0).Error);
        }
    }

    [Fact]
    public void Given_TheRemoteSigner_When_SigningTheLocalHtlcTransactions_Then_TheLocalSignerAcceptsThem()
    {
        // Arrange
        var (contexts, harness, htlcTxs) = BuildContexts(FiveHtlcCase);
        var (_, built, _) = harness.Build();
        var commitment = Transaction.Load(built.Transaction.RawTxBytes, Network.Main);
        var remoteSigner = new VectorSigner(asRemote: true);
        var localSigner = new VectorSigner(asRemote: false);

        // Act
        var remoteSignatures = remoteSigner.SignRemoteHtlcTransactions(ChannelId.Zero, contexts);

        // Assert: fresh aux randomness, so not the vectors' bytes, but valid SINGLE|ANYONECANPAY signatures
        Assert.Equal(contexts.Count, remoteSignatures.Count);
        localSigner.ValidateLocalHtlcSignatures(ChannelId.Zero, contexts, remoteSignatures);
        for (var i = 0; i < contexts.Count; i++)
        {
            Assert.NotEqual(FiveHtlcCase.HtlcDescs[i].RemoteSignature, (byte[])remoteSignatures[i]);
            var localSignature = localSigner.SignLocalHtlcTransaction(ChannelId.Zero, contexts[i]);
            var preimage = htlcTxs[i].Type == HtlcTransactionType.Success
                               ? harness.PreimageOf(htlcTxs[i].SpentOutput)
                               : null;
            var signed = harness.HtlcBuilder.AddWitness(htlcTxs[i], contexts[i].HtlcTransaction,
                                                        remoteSignatures[i], localSignature, preimage);
            var tx = Transaction.Load(signed.RawTxBytes, Network.Main);
            Assert.Null(tx.CreateValidator([commitment.Outputs[built.HtlcOutputsInTxOrder[i].Vout]])
                          .ValidateInput(0).Error);
        }
    }

    [Fact]
    public void Given_TheVectorsRemoteSignature_When_CheckedAsTheHoldersSigHash_Then_TheSignerRejectsIt()
    {
        // Arrange: the holder's own signature (SIGHASH_DEFAULT) is not a valid counterparty signature
        var (contexts, _, _) = BuildContexts(FiveHtlcCase);
        var signer = new VectorSigner(asRemote: false);
        var holderSignatures = contexts.Select(c => signer.SignLocalHtlcTransaction(ChannelId.Zero, c)).ToList();

        // Act / Assert
        Assert.Throws<SignerException>(() => signer.ValidateLocalHtlcSignatures(ChannelId.Zero, contexts,
                                                                                 holderSignatures));
    }

    private static (IReadOnlyList<HtlcSigningContext> Contexts, SimpleTaprootVectorHarness Harness,
        IReadOnlyList<HtlcTransactionModel> HtlcTxs) BuildContexts(SimpleTaprootVectors.TransactionCase vectorCase)
    {
        var harness = new SimpleTaprootVectorHarness(vectorCase);
        var (_, _, htlcTxs) = harness.Build();
        var contexts = htlcTxs.Select(h => new HtlcSigningContext(harness.HtlcBuilder.Build(h),
                                                                  SimpleTaprootVectorHarness.PerCommitmentPoint,
                                                                  true))
                              .ToList();
        return (contexts, harness, htlcTxs);
    }

    /// <summary>
    /// A signer whose HTLC basepoint secret is the vectors' local (or remote) one, registered for
    /// <see cref="ChannelId.Zero"/> with the other node's HTLC basepoint.
    /// </summary>
    private sealed class VectorSigner : LocalLightningSigner
    {
        private readonly bool _asRemote;

        public VectorSigner(bool asRemote)
            : base(new FundingOutputBuilder(), new KeyDerivationService(new Secp256K1Math()),
                   NullLogger<LocalLightningSigner>.Instance, new NodeOptions(),
                   new Mock<ISecureKeyManager>().Object, new Mock<IUtxoMemoryRepository>().Object)
        {
            _asRemote = asRemote;
            var local = SimpleTaprootVectors.Key("local_funding_pubkey");
            var remote = SimpleTaprootVectors.Key("remote_funding_pubkey");
            RegisterChannel(ChannelId.Zero,
                            new ChannelSigningInfo(SimpleTaprootVectors.FundingTransaction.GetHash().ToBytes(), 0,
                                                   SimpleTaprootVectors.FundingSatoshis, asRemote ? remote : local,
                                                   asRemote ? local : remote, 0,
                                                   SimpleTaprootVectors.Key(asRemote
                                                                                ? "local_htlc_basepoint"
                                                                                : "remote_htlc_basepoint"),
                                                   SimpleTaprootVectors.CommitHeight));
        }

        protected override Key GetHtlcBasepointSecret(uint channelKeyIndex) =>
            SimpleTaprootVectors.PrivKey(_asRemote ? "remote_htlc_basepoint_secret" : "local_htlc_basepoint_secret");
    }
}