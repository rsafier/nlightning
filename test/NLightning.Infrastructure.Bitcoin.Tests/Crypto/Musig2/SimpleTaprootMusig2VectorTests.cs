using System.Text.Json;
using NBitcoin;
using NBitcoin.Secp256k1.Musig;

namespace NLightning.Infrastructure.Bitcoin.Tests.Crypto.Musig2;

using Domain.Crypto.ValueObjects;
using Infrastructure.Bitcoin.Crypto.Musig2;
using Taproot;
using static Musig2VectorKit;

/// <summary>
/// The simple taproot channels vectors (<c>Taproot/Vectors/simple-taproot-vectors.json</c>, read through
/// <see cref="SimpleTaprootVectors"/>) at the MuSig2 level: the funding output key
/// from the two funding keys, each transaction's nonces, and the full signature of each signed commitment replayed over
/// its BIP 341 key-path sighash: the remote partial signature reproduced byte for byte from <c>remote_sec_nonce</c>, ours
/// added, and the aggregate equal to the witness of <c>expected_commitment_tx_hex</c>.
/// </summary>
public class SimpleTaprootMusig2VectorTests
{
    private readonly Musig2Service _service = new();

    public static TheoryData<int> Transactions
    {
        get
        {
            var data = new TheoryData<int>();
            for (var i = 0; i < SimpleTaprootVectors.Root.GetProperty("transactions").GetArrayLength(); i++)
                data.Add(i);

            return data;
        }
    }

    private static JsonElement Keys => SimpleTaprootVectors.Keys;
    private static JsonElement Funding => SimpleTaprootVectors.Scripts.GetProperty("funding");

    [Fact]
    public void Given_TheFundingKeys_When_AggregatedForATaprootKeyPath_Then_OutputKeyAndScriptEqualTheVector()
    {
        // Arrange
        CompactPubKey local = Hex(Keys.GetProperty("local_funding_pubkey"));
        CompactPubKey remote = Hex(Keys.GetProperty("remote_funding_pubkey"));

        // Act
        var aggregate = _service.AggregateTaprootKeyPath(local, remote);
        var swapped = _service.AggregateTaprootKeyPath(remote, local);

        // Assert
        Assert.Equal(Hex(Funding.GetProperty("combined_key")), aggregate.XOnlyOutputKey);
        Assert.Equal(Hex(Funding.GetProperty("pkscript")), aggregate.GetTaprootScriptPubKey());
        Assert.Equal(aggregate.PubKeys, swapped.PubKeys);
        Assert.Equal(aggregate.InternalKey, swapped.InternalKey);
        Assert.Equal(aggregate.OutputKey, swapped.OutputKey);
        Assert.Single(aggregate.Tweaks);
        Assert.True(aggregate.Tweaks[0].IsXOnly);

        // KeySort put the 02 key first
        Assert.Equal(remote, aggregate.PubKeys[0]);
        Assert.Equal(local, aggregate.PubKeys[1]);
    }

    [Fact]
    public void Given_TheFundingKeys_When_AggregatedByHand_Then_TheBip86TweakOfTheInternalKeyGivesTheOutputKey()
    {
        // Arrange: BIP 86 / BIP 341: Q = P + tagged_hash("TapTweak", x(P)) * G, x-only P
        CompactPubKey local = Hex(Keys.GetProperty("local_funding_pubkey"));
        CompactPubKey remote = Hex(Keys.GetProperty("remote_funding_pubkey"));
        var aggregate = _service.AggregateTaprootKeyPath(local, remote);

        // Act: NBitcoin's own BIP 86 output key of the internal key
        var internalKey = new TaprootInternalPubKey(((byte[])aggregate.InternalKey)[1..]);
        var outputKey = internalKey.GetTaprootFullPubKey();

        // Assert
        Assert.Equal(outputKey.ToBytes(), aggregate.XOnlyOutputKey);
        Assert.Equal(outputKey.OutputKeyParity, aggregate.OutputKeyHasOddY);
    }

    [Theory]
    [MemberData(nameof(Transactions))]
    public void Given_ATransaction_When_ItsSecretNoncesAreRead_Then_TheyMatchThePublicNoncesAndFundingKeys(int index)
    {
        // Arrange
        var transaction = SimpleTaprootVectors.Root.GetProperty("transactions")[index];
        var localSecNonce = Hex(transaction.GetProperty("local_sec_nonce"));
        var remoteSecNonce = Hex(transaction.GetProperty("remote_sec_nonce"));
        var localNonce = Hex(transaction.GetProperty("local_nonce"));
        var remoteNonce = Hex(transaction.GetProperty("remote_nonce"));

        // Act
        var localPublic = Bip327SignVectorTests.PublicNonceOf(localSecNonce);
        var remotePublic = Bip327SignVectorTests.PublicNonceOf(remoteSecNonce);

        // Assert
        Assert.Equal(localNonce, localPublic);
        Assert.Equal(remoteNonce, remotePublic);
        Assert.Equal(Hex(Keys.GetProperty("local_funding_pubkey")),
                     (byte[])new MusigSecretNonce(localSecNonce).PublicKey);
        Assert.Equal(Hex(Keys.GetProperty("remote_funding_pubkey")),
                     (byte[])new MusigSecretNonce(remoteSecNonce).PublicKey);
    }

    [Theory]
    [MemberData(nameof(Transactions))]
    public void Given_ATransaction_When_ItsNoncesAreAggregated_Then_TheResultIsOrderFreeAndMatchesNBitcoin(int index)
    {
        // Arrange
        var transaction = SimpleTaprootVectors.Root.GetProperty("transactions")[index];
        MusigPublicNonce localNonce = Hex(transaction.GetProperty("local_nonce"));
        MusigPublicNonce remoteNonce = Hex(transaction.GetProperty("remote_nonce"));

        // Act
        var aggregate = _service.AggregateNonces([localNonce, remoteNonce]);
        var reversed = _service.AggregateNonces([remoteNonce, localNonce]);
        var byNBitcoin = MusigPubNonce.Aggregate([new MusigPubNonce(localNonce), new MusigPubNonce(remoteNonce)]);

        // Assert
        Assert.Equal(aggregate, reversed);
        Assert.Equal(byNBitcoin.ToBytes(), (byte[])aggregate);
    }

    [Theory]
    [MemberData(nameof(Transactions))]
    public void Given_ASignedCommitment_When_ReplayedOverItsSighash_Then_PartialSignaturesAndWitnessAreReproduced(
        int index)
    {
        // Arrange
        var transaction = SimpleTaprootVectors.Root.GetProperty("transactions")[index];
        var commitment = Transaction.Parse(transaction.GetProperty("expected_commitment_tx_hex").GetString()!,
                                           Network.RegTest);
        var fundingSat = SimpleTaprootVectors.Params.GetProperty("funding_amount_satoshis").GetInt64();
        var spentOutput = new TxOut(Money.Satoshis(fundingSat),
                                    Script.FromBytesUnsafe(Hex(Funding.GetProperty("pkscript"))));
        var sighash = commitment.GetSignatureHashTaproot([spentOutput], new TaprootExecutionData(0)).ToBytes();
        var witness = commitment.Inputs[0].WitScript;

        CompactPubKey local = Hex(Keys.GetProperty("local_funding_pubkey"));
        CompactPubKey remote = Hex(Keys.GetProperty("remote_funding_pubkey"));
        PrivKey localPrivKey = Hex(Keys.GetProperty("local_funding_privkey"));
        PrivKey remotePrivKey = Hex(Keys.GetProperty("remote_funding_privkey"));
        MusigPublicNonce localNonce = Hex(transaction.GetProperty("local_nonce"));
        MusigPublicNonce remoteNonce = Hex(transaction.GetProperty("remote_nonce"));
        var expectedRemotePartialSig = Hex(transaction.GetProperty("remote_partial_sig"));

        var aggregate = _service.AggregateTaprootKeyPath(local, remote);
        var session = aggregate.CreateSession(_service.AggregateNonces([localNonce, remoteNonce]), sighash);

        // Act
        var remotePartialSig = _service.Sign(new MusigSecretNonce(Hex(transaction.GetProperty("remote_sec_nonce"))),
                                             remotePrivKey, session);
        var localPartialSig = _service.Sign(new MusigSecretNonce(Hex(transaction.GetProperty("local_sec_nonce"))),
                                            localPrivKey, session);
        var signature = _service.AggregatePartialSignatures([localPartialSig, remotePartialSig], session);

        // Assert: the key-path witness is the one 64-byte BIP-340 signature (SIGHASH_DEFAULT)
        Assert.Equal(1, witness.PushCount);
        Assert.Equal(expectedRemotePartialSig, (byte[])remotePartialSig);
        Assert.True(_service.VerifyPartialSignature(expectedRemotePartialSig, remoteNonce, remote, session));
        Assert.True(_service.VerifyPartialSignature(localPartialSig, localNonce, local, session));
        Assert.Equal(witness[0], signature);
        Assert.True(_service.VerifySignature(signature, aggregate.XOnlyOutputKey, sighash));
    }
}