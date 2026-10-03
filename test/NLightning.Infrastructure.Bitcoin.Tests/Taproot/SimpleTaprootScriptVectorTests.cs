using System.Security.Cryptography;
using System.Text;
using NBitcoin;
using NBitcoin.Secp256k1;
using SHA256 = System.Security.Cryptography.SHA256;

namespace NLightning.Infrastructure.Bitcoin.Tests.Taproot;

using Bitcoin.Crypto.Functions;
using Bitcoin.Outputs;
using Bitcoin.Services;
using Bitcoin.Taproot;
using Domain.Crypto.ValueObjects;
using Domain.Money;

/// <summary>
/// Every <c>scripts.*</c> entry and derived key of bolt-simple-taproot.md's vectors, byte for byte: each leaf script,
/// leaf hash, tapscript root, internal key, output key (with its parity) and pkScript, built by
/// <see cref="SimpleTaprootScripts"/> and the taproot output classes.
/// </summary>
public class SimpleTaprootScriptVectorTests
{
    private static readonly KeyDerivationService s_keyDerivation = new(new Secp256K1Math());

    // The script vectors' HTLC: the first HTLC of the transaction vectors (preimage 32 x 0x00, expiry 500)
    private static readonly byte[] s_paymentHash = SHA256.HashData(new byte[32]);
    private const uint CltvExpiry = 500;

    private static PubKey LocalDelayedPubKey => SimpleTaprootVectors.PubKey("derived_local_delayed_pubkey");
    private static PubKey RevocationPubKey => SimpleTaprootVectors.PubKey("derived_revocation_pubkey");
    private static PubKey LocalHtlcPubKey => SimpleTaprootVectors.PubKey("derived_local_htlc_pubkey");
    private static PubKey RemoteHtlcPubKey => SimpleTaprootVectors.PubKey("derived_remote_htlc_pubkey");
    private static PubKey RemotePaymentPubKey => SimpleTaprootVectors.PubKey("derived_remote_payment_pubkey");

    #region Keys

    [Theory]
    [InlineData("local_funding_privkey", "local-funding")]
    [InlineData("remote_funding_privkey", "remote-funding")]
    [InlineData("local_payment_basepoint_secret", "local-payment-basepoint")]
    [InlineData("remote_payment_basepoint_secret", "remote-payment-basepoint")]
    [InlineData("local_delayed_payment_basepoint_secret", "local-delayed-payment-basepoint")]
    [InlineData("remote_revocation_basepoint_secret", "remote-revocation-basepoint")]
    [InlineData("local_htlc_basepoint_secret", "local-htlc-basepoint")]
    [InlineData("remote_htlc_basepoint_secret", "remote-htlc-basepoint")]
    [InlineData("local_per_commit_secret", "local-per-commit-secret")]
    public void Given_TheSeed_When_DerivingAVectorKey_Then_ItIsSha256OfSeedAndLabel(string key, string label)
    {
        // Arrange
        var seed = SimpleTaprootVectors.Hex(SimpleTaprootVectors.Params, "seed");

        // Act
        var derived = SHA256.HashData([.. seed, .. Encoding.ASCII.GetBytes(label)]);

        // Assert
        Assert.Equal(SimpleTaprootVectors.Key(key), derived);
    }

    [Theory]
    [InlineData("local_funding_privkey", "local_funding_pubkey")]
    [InlineData("remote_funding_privkey", "remote_funding_pubkey")]
    [InlineData("local_payment_basepoint_secret", "local_payment_basepoint")]
    [InlineData("remote_payment_basepoint_secret", "remote_payment_basepoint")]
    [InlineData("local_delayed_payment_basepoint_secret", "local_delayed_payment_basepoint")]
    [InlineData("remote_revocation_basepoint_secret", "remote_revocation_basepoint")]
    [InlineData("local_htlc_basepoint_secret", "local_htlc_basepoint")]
    [InlineData("remote_htlc_basepoint_secret", "remote_htlc_basepoint")]
    [InlineData("local_per_commit_secret", "local_per_commit_point")]
    public void Given_AVectorSecret_When_MultipliedByG_Then_ItIsTheVectorsPoint(string secret, string point)
    {
        // Act
        var pubKey = SimpleTaprootVectors.PrivKey(secret).PubKey;

        // Assert
        Assert.Equal(SimpleTaprootVectors.Key(point), pubKey.ToBytes());
    }

    [Fact]
    public void Given_TheBasepointsAndPerCommitmentPoint_When_DerivingTheCommitmentKeys_Then_TheyAreTheVectorsKeys()
    {
        // Arrange
        CompactPubKey point = SimpleTaprootVectors.Key("local_per_commit_point");

        // Act
        var localDelayed = s_keyDerivation.DerivePublicKey(SimpleTaprootVectors.Key("local_delayed_payment_basepoint"),
                                                           point);
        var revocation = s_keyDerivation.DeriveRevocationPubKey(SimpleTaprootVectors.Key("remote_revocation_basepoint"),
                                                                point);
        var localHtlc = s_keyDerivation.DerivePublicKey(SimpleTaprootVectors.Key("local_htlc_basepoint"), point);
        var remoteHtlc = s_keyDerivation.DerivePublicKey(SimpleTaprootVectors.Key("remote_htlc_basepoint"), point);

        // Assert
        Assert.Equal(SimpleTaprootVectors.Key("derived_local_delayed_pubkey"), (byte[])localDelayed);
        Assert.Equal(SimpleTaprootVectors.Key("derived_revocation_pubkey"), (byte[])revocation);
        Assert.Equal(SimpleTaprootVectors.Key("derived_local_htlc_pubkey"), (byte[])localHtlc);
        Assert.Equal(SimpleTaprootVectors.Key("derived_remote_htlc_pubkey"), (byte[])remoteHtlc);

        // option_static_remotekey: remotepubkey is the remote payment basepoint itself
        Assert.Equal(SimpleTaprootVectors.Key("remote_payment_basepoint"),
                     SimpleTaprootVectors.Key("derived_remote_payment_pubkey"));
    }

    [Fact]
    public void Given_TheBasepointSecrets_When_DerivingThePrivateKeys_Then_TheyMatchTheDerivedPublicKeys()
    {
        // Arrange
        CompactPubKey point = SimpleTaprootVectors.Key("local_per_commit_point");

        // Act
        var localDelayed = s_keyDerivation.DerivePrivateKey(
            SimpleTaprootVectors.Key("local_delayed_payment_basepoint_secret"), point);
        var localHtlc = s_keyDerivation.DerivePrivateKey(SimpleTaprootVectors.Key("local_htlc_basepoint_secret"),
                                                         point);
        var remoteHtlc = s_keyDerivation.DerivePrivateKey(SimpleTaprootVectors.Key("remote_htlc_basepoint_secret"),
                                                          point);
        var revocation = s_keyDerivation.DeriveRevocationPrivKey(
            SimpleTaprootVectors.Key("remote_revocation_basepoint_secret"),
            SimpleTaprootVectors.Key("local_per_commit_secret"));

        // Assert
        Assert.Equal(LocalDelayedPubKey, new Key(localDelayed).PubKey);
        Assert.Equal(LocalHtlcPubKey, new Key(localHtlc).PubKey);
        Assert.Equal(RemoteHtlcPubKey, new Key(remoteHtlc).PubKey);
        Assert.Equal(RevocationPubKey, new Key(revocation).PubKey);
    }

    [Fact]
    public void Given_TheNumsPointParam_When_Compared_Then_ItIsSimpleTaprootNums()
    {
        // Assert
        Assert.Equal(SimpleTaprootVectors.Hex(SimpleTaprootVectors.Params, "nums_point"),
                     SimpleTaprootScripts.NumsPoint.ToBytes());
    }

    #endregion

    #region Funding

    [Fact]
    public void Given_TheCombinedKey_When_BuildingTheFundingScript_Then_ItIsTheVectorsPkScript()
    {
        // Arrange
        var funding = SimpleTaprootVectors.Scripts.GetProperty("funding");
        var combinedKey = new TaprootPubKey(SimpleTaprootVectors.Hex(funding, "combined_key"));

        // Act
        var pkScript = combinedKey.ScriptPubKey;

        // Assert: "combined_key" is the BIP 86 output key, the funding transaction's output
        Assert.Equal(SimpleTaprootVectors.Hex(funding, "pkscript"), pkScript.ToBytes());
        var fundingTx = SimpleTaprootVectors.FundingTransaction;
        Assert.Single(fundingTx.Outputs);
        Assert.Equal(pkScript, fundingTx.Outputs[0].ScriptPubKey);
        Assert.Equal(Money.Satoshis(SimpleTaprootVectors.FundingSatoshis), fundingTx.Outputs[0].Value);
    }

    [Fact]
    public void Given_TheMusig2AggregateOfTheFundingKeys_When_BuildingTheFundingOutput_Then_ItIsTheVectorsPkScript()
    {
        // Arrange: KeyAgg(KeySort(pubkey1, pubkey2)) through NBitcoin.Secp256k1 as a fixture (the MuSig2 module itself
        // is NL-877 lane T0's); the output takes the aggregate and applies the BIP 86 tweak
        var funding = SimpleTaprootVectors.Scripts.GetProperty("funding");
        var aggregate = ECPubKey.MusigAggregate(
        [
            ECPubKey.Create(SimpleTaprootVectors.Key("local_funding_pubkey")),
            ECPubKey.Create(SimpleTaprootVectors.Key("remote_funding_pubkey"))
        ], true);

        // Act
        var output = new TaprootFundingOutput(LightningMoney.Satoshis(SimpleTaprootVectors.FundingSatoshis),
                                              new TaprootInternalPubKey(aggregate.ToXOnlyPubKey().ToBytes()));

        // Assert
        Assert.Equal(SimpleTaprootVectors.Hex(funding, "pkscript"), output.ScriptPubKey.ToBytes());
        Assert.Equal(SimpleTaprootVectors.Hex(funding, "combined_key"), output.FundingKey.OutputKey.ToBytes());
        Assert.Throws<NotSupportedException>(() => output.ToCoin());
    }

    #endregion

    #region Commitment outputs

    [Fact]
    public void Given_TheToLocalKeys_When_BuildingTheToLocalOutput_Then_EveryValueMatchesTheVector()
    {
        // Act
        var output = new TaprootToLocalOutput(LightningMoney.Satoshis(1_000), LocalDelayedPubKey, RevocationPubKey,
                                              SimpleTaprootVectors.CsvDelay);

        // Assert
        AssertTree("to_local", output, SimpleTaprootScripts.NumsPoint,
                   ("settle", output.DelayLeaf), ("revocation", output.RevokeLeaf));
    }

    [Fact]
    public void Given_TheRemotePubKey_When_BuildingTheToRemoteOutput_Then_EveryValueMatchesTheVector()
    {
        // Act
        var output = new TaprootToRemoteOutput(LightningMoney.Satoshis(1_000), RemotePaymentPubKey);

        // Assert: the internal key is the same NUMS point as to_local's (not §To Remote Outputs' 0245b181...)
        AssertTree("to_remote", output, SimpleTaprootScripts.NumsPoint, ("settle", output.Leaf));
    }

    [Fact]
    public void Given_TheLocalDelayedPubKey_When_BuildingTheLocalAnchor_Then_EveryValueMatchesTheVector()
    {
        // Act
        var output = new TaprootAnchorOutput(LightningMoney.Satoshis(330), LocalDelayedPubKey);

        // Assert
        AssertTree("local_anchor", output, LocalDelayedPubKey, ("sweep", output.SweepLeaf));
    }

    [Fact]
    public void Given_TheRemotePubKey_When_BuildingTheRemoteAnchor_Then_EveryValueMatchesTheVector()
    {
        // Act
        var output = new TaprootAnchorOutput(LightningMoney.Satoshis(330), RemotePaymentPubKey);

        // Assert
        AssertTree("remote_anchor", output, RemotePaymentPubKey, ("sweep", output.SweepLeaf));
    }

    #endregion

    #region HTLC outputs

    [Theory]
    [InlineData("offered_htlc_local_commit")]
    [InlineData("offered_htlc_remote_commit")]
    public void Given_TheHtlcKeys_When_BuildingAnOfferedHtlcOutput_Then_EveryValueMatchesTheVector(string entry)
    {
        // Act
        var output = new TaprootOfferedHtlcOutput(LightningMoney.Satoshis(1_000), CltvExpiry, LocalHtlcPubKey,
                                                  s_paymentHash, RemoteHtlcPubKey, RevocationPubKey);

        // Assert
        AssertTree(entry, output, RevocationPubKey, ("timeout", output.TimeoutLeaf), ("success", output.SuccessLeaf));
    }

    [Theory]
    [InlineData("accepted_htlc_local_commit")]
    [InlineData("accepted_htlc_remote_commit")]
    public void Given_TheHtlcKeys_When_BuildingAnAcceptedHtlcOutput_Then_EveryValueMatchesTheVector(string entry)
    {
        // Act: the accepted vectors are built with the two HTLC keys swapped relative to their names (the generator
        // passed (sender, receiver) = (local, remote) to both HTLC kinds); the commitment vectors use the spec roles
        var output = new TaprootReceivedHtlcOutput(LightningMoney.Satoshis(1_000), CltvExpiry,
                                                   localHtlcPubKey: RemoteHtlcPubKey, s_paymentHash,
                                                   remoteHtlcPubKey: LocalHtlcPubKey, RevocationPubKey);

        // Assert
        AssertTree(entry, output, RevocationPubKey, ("timeout", output.TimeoutLeaf), ("success", output.SuccessLeaf));
    }

    [Theory]
    [InlineData("second_level_htlc_success")]
    [InlineData("second_level_htlc_timeout")]
    public void Given_TheDelayedAndRevocationKeys_When_BuildingASecondLevelOutput_Then_EveryValueMatchesTheVector(
        string entry)
    {
        // Act
        var output = new TaprootHtlcResolutionOutput(LightningMoney.Satoshis(1_000), LocalDelayedPubKey,
                                                     RevocationPubKey, SimpleTaprootVectors.CsvDelay);

        // Assert
        AssertTree(entry, output, RevocationPubKey, ("success", output.DelayLeaf));
    }

    [Fact]
    public void Given_ATwoLeafTree_When_GettingAControlBlock_Then_ItProvesTheLeafWithTheSiblingsHash()
    {
        // Arrange
        var output = new TaprootOfferedHtlcOutput(LightningMoney.Satoshis(1_000), CltvExpiry, LocalHtlcPubKey,
                                                  s_paymentHash, RemoteHtlcPubKey, RevocationPubKey);

        // Act
        var controlBlock = output.Tree.GetControlBlock(output.SuccessLeaf);

        // Assert: (parity | 0xc0) || internal key || the timeout leaf's hash
        Assert.Equal(65, controlBlock.ToBytes().Length);
        Assert.Equal((byte)(0xc0 | (output.Tree.OutputKeyParityIsOdd ? 1 : 0)), controlBlock.ToBytes()[0]);
        Assert.Equal(output.Tree.InternalKey.ToBytes(), controlBlock.ToBytes()[1..33]);
        Assert.Equal(output.TimeoutLeaf.LeafHash.ToBytes(), controlBlock.ToBytes()[33..]);
        Assert.True(controlBlock.VerifyTaprootCommitment(output.Tree.OutputKey, output.SuccessLeaf));
        Assert.Throws<ArgumentException>(() => output.Tree.GetControlBlock(
                                             new TapScript(SimpleTaprootScripts.CreateAnchorScript(),
                                                           SimpleTaprootScripts.LeafVersion)));
    }

    #endregion

    private static void AssertTree(string entryName, BaseTaprootOutput output, PubKey expectedInternalKey,
                                   params (string Name, TapScript Leaf)[] leaves)
    {
        var entry = SimpleTaprootVectors.Scripts.GetProperty(entryName);
        var scripts = entry.GetProperty("scripts");
        var leafHashes = entry.GetProperty("leaf_hashes");

        Assert.Equal(scripts.EnumerateObject().Count(), leaves.Length);
        foreach (var (name, leaf) in leaves)
        {
            Assert.Equal(SimpleTaprootVectors.HexString(scripts, name), Hex(leaf.Script.ToBytes()));
            Assert.Equal(SimpleTaprootVectors.HexString(leafHashes, name), Hex(leaf.LeafHash.ToBytes()));
            Assert.Equal(SimpleTaprootScripts.LeafVersion, leaf.Version);
        }

        var tree = output.Tree;
        Assert.Equal(SimpleTaprootVectors.HexString(entry, "tapscript_root"), Hex(tree.MerkleRoot.ToBytes()));

        // The vectors give the internal and output keys as 33-byte keys: the x coordinate is committed, the prefix is the
        // y parity (the internal key's as given, the output key's as the control blocks carry it)
        var internalKey = SimpleTaprootVectors.Hex(entry, "internal_key");
        Assert.Equal(internalKey, expectedInternalKey.ToBytes());
        Assert.Equal(internalKey[1..], tree.InternalKey.ToBytes());

        var outputKey = SimpleTaprootVectors.Hex(entry, "output_key");
        Assert.Equal(outputKey[0] == 0x03, tree.OutputKeyParityIsOdd);
        Assert.Equal(outputKey[1..], tree.OutputKey.OutputKey.ToBytes());

        Assert.Equal(SimpleTaprootVectors.HexString(entry, "pkscript"), Hex(output.ScriptPubKey.ToBytes()));
        Assert.Equal(output.ScriptPubKey, output.ToTxOut().ScriptPubKey);
    }

    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();
}