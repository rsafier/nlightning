using NBitcoin;

namespace NLightning.Infrastructure.Bitcoin.Tests.Taproot;

using Bitcoin.Builders;
using Bitcoin.Outputs;
using Bitcoin.Taproot;
using Domain.Money;

/// <summary>
/// Every spend path of the simple taproot outputs of the five-HTLC vector commitment, executed by NBitcoin's script
/// interpreter against the commitment output it spends: to_local (delay leaf at and one below <c>to_self_delay</c>,
/// revocation leaf), to_remote (CSV 1), both anchors (owner key path, anyone after 16 blocks), the offered HTLC's
/// success leaf and the accepted HTLC's timeout leaf (the remote node's paths; ours are the second-level transactions
/// of <see cref="SimpleTaprootCommitmentVectorTests"/>), the revocation key path of every HTLC output and the
/// second-level output (delay leaf and revocation key path).
/// </summary>
public class SimpleTaprootSpendPathTests
{
    private static readonly byte[] s_zeroAuxRandomness = new byte[32];
    private static readonly Script s_destination = new Key(Enumerable.Repeat((byte)0x55, 32).ToArray()).PubKey
                                                                                                       .WitHash
                                                                                                       .ScriptPubKey;

    private readonly SimpleTaprootVectors.TransactionCase _case = SimpleTaprootVectors.Transactions[1];
    private readonly Transaction _commitment;
    private readonly List<BaseOutput> _outputs;

    public SimpleTaprootSpendPathTests()
    {
        var harness = new SimpleTaprootVectorHarness(_case);
        var (model, built, _) = harness.Build();
        _commitment = Transaction.Load(built.Transaction.RawTxBytes, Network.Main);
        _outputs = CommitmentTransactionBuilder.CreateSimpleTaprootOutputs(model).Select(o => o.Output).ToList();
    }

    [Theory]
    [InlineData(144u, true)]
    [InlineData(143u, false)]
    public void Given_ToLocal_When_SpentThroughTheDelayLeaf_Then_ValidOnlyAfterToSelfDelay(uint sequence, bool valid)
    {
        // Arrange
        var output = Single<TaprootToLocalOutput>();
        var (tx, spent) = Spend(output, sequence);

        // Act
        var sig = SignScriptPath(tx, spent, output.DelayLeaf, SimpleTaprootVectorHarness.LocalDelayedKey);
        tx.Inputs[0].WitScript = Witness(sig, output.DelayLeaf.Script.ToBytes(),
                                               output.GetControlBlock(output.DelayLeaf));

        // Assert
        AssertValidity(tx, spent, valid);
    }

    [Fact]
    public void Given_ToLocal_When_SpentThroughTheRevocationLeaf_Then_ValidAtOnce()
    {
        // Arrange
        var output = Single<TaprootToLocalOutput>();
        var (tx, spent) = Spend(output, 0);

        // Act
        var sig = SignScriptPath(tx, spent, output.RevokeLeaf, SimpleTaprootVectorHarness.RevocationKey);
        tx.Inputs[0].WitScript = Witness(sig, output.RevokeLeaf.Script.ToBytes(),
                                               output.GetControlBlock(output.RevokeLeaf));

        // Assert
        AssertValidity(tx, spent, true);
    }

    [Fact]
    public void Given_ToLocal_When_TheDelayedKeySignsTheRevocationLeaf_Then_Invalid()
    {
        // Arrange
        var output = Single<TaprootToLocalOutput>();
        var (tx, spent) = Spend(output, 0);

        // Act
        var sig = SignScriptPath(tx, spent, output.RevokeLeaf, SimpleTaprootVectorHarness.LocalDelayedKey);
        tx.Inputs[0].WitScript = Witness(sig, output.RevokeLeaf.Script.ToBytes(),
                                               output.GetControlBlock(output.RevokeLeaf));

        // Assert
        AssertValidity(tx, spent, false);
    }

    [Theory]
    [InlineData(1u, true)]
    [InlineData(0u, false)]
    public void Given_ToRemote_When_SpentByTheRemoteNode_Then_ValidOnlyAfterOneBlock(uint sequence, bool valid)
    {
        // Arrange
        var output = Single<TaprootToRemoteOutput>();
        Assert.Equal(SimpleTaprootVectorHarness.RemotePaymentKey.PubKey, output.RemotePubKey);
        var (tx, spent) = Spend(output, sequence);

        // Act
        var sig = SignScriptPath(tx, spent, output.Leaf, SimpleTaprootVectorHarness.RemotePaymentKey);
        tx.Inputs[0].WitScript = Witness(sig, output.Leaf.Script.ToBytes(), output.GetControlBlock(output.Leaf));

        // Assert
        AssertValidity(tx, spent, valid);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Given_AnAnchor_When_ItsOwnerSpendsItThroughTheKeyPath_Then_Valid(bool local)
    {
        // Arrange: the local anchor's internal key is local_delayedpubkey, the remote one's the remotepubkey
        var key = local ? SimpleTaprootVectorHarness.LocalDelayedKey : SimpleTaprootVectorHarness.RemotePaymentKey;
        var output = _outputs.OfType<TaprootAnchorOutput>().Single(a => a.InternalPubKey == key.PubKey);
        var (tx, spent) = Spend(output, 0);

        // Act
        tx.Inputs[0].WitScript = Witness(SignKeyPath(tx, spent, key, output.Tree.MerkleRoot));

        // Assert
        AssertValidity(tx, spent, true);
    }

    [Theory]
    [InlineData(16u, true)]
    [InlineData(15u, false)]
    public void Given_AnAnchor_When_AnyoneSpendsItThroughTheScriptPath_Then_ValidOnlyAfter16Blocks(uint sequence,
        bool valid)
    {
        // Arrange
        var output = _outputs.OfType<TaprootAnchorOutput>().First();
        var (tx, spent) = Spend(output, sequence);

        // Act: OP_16 OP_CHECKSEQUENCEVERIFY needs no signature
        tx.Inputs[0].WitScript = Witness(output.SweepLeaf.Script.ToBytes(),
                                               output.GetControlBlock(output.SweepLeaf));

        // Assert
        AssertValidity(tx, spent, valid);
    }

    [Theory]
    [InlineData(1u, true, true)]
    [InlineData(0u, true, false)]
    [InlineData(1u, false, false)]
    public void Given_AnOfferedHtlc_When_TheRemoteNodeClaimsItWithThePreimage_Then_ValidOnlyWithItAfterOneBlock(
        uint sequence, bool rightPreimage, bool valid)
    {
        // Arrange
        var output = _outputs.OfType<TaprootOfferedHtlcOutput>().First();
        var preimage = _case.Htlcs.Single(h => h.PaymentHash.AsSpan().SequenceEqual(output.PaymentHash.Span))
                            .Preimage;
        var (tx, spent) = Spend(output, sequence);

        // Act
        var sig = SignScriptPath(tx, spent, output.SuccessLeaf, SimpleTaprootVectorHarness.RemoteHtlcKey);
        tx.Inputs[0].WitScript = Witness(sig, rightPreimage ? preimage : new byte[32],
                                               output.SuccessLeaf.Script.ToBytes(),
                                               output.GetControlBlock(output.SuccessLeaf));

        // Assert
        AssertValidity(tx, spent, valid);
    }

    [Theory]
    [InlineData(0, 1u, true)]
    [InlineData(-1, 1u, false)]
    [InlineData(0, 0u, false)]
    public void Given_AnAcceptedHtlc_When_TheRemoteNodeTimesItOut_Then_ValidOnlyAtCltvExpiryAfterOneBlock(
        int lockTimeOffset, uint sequence, bool valid)
    {
        // Arrange
        var output = _outputs.OfType<TaprootReceivedHtlcOutput>().First();
        var (tx, spent) = Spend(output, sequence, (uint)((long)output.CltvExpiry + lockTimeOffset));

        // Act
        var sig = SignScriptPath(tx, spent, output.TimeoutLeaf, SimpleTaprootVectorHarness.RemoteHtlcKey);
        tx.Inputs[0].WitScript = Witness(sig, output.TimeoutLeaf.Script.ToBytes(),
                                               output.GetControlBlock(output.TimeoutLeaf));

        // Assert
        AssertValidity(tx, spent, valid);
    }

    [Fact]
    public void Given_EveryHtlcOutput_When_SpentWithTheRevocationKey_Then_ValidThroughTheKeyPath()
    {
        // Arrange
        var htlcOutputs = _outputs.OfType<TaprootHtlcOutput>().ToList();
        Assert.Equal(_case.HtlcDescs.Count, htlcOutputs.Count);

        foreach (var output in htlcOutputs)
        {
            var (tx, spent) = Spend(output, 0);

            // Act
            tx.Inputs[0].WitScript = Witness(SignKeyPath(tx, spent, SimpleTaprootVectorHarness.RevocationKey,
                                                               output.Tree.MerkleRoot));

            // Assert
            AssertValidity(tx, spent, true);
        }
    }

    [Fact]
    public void Given_AnHtlcOutput_When_SpentThroughTheKeyPathByAnotherKey_Then_Invalid()
    {
        // Arrange
        var output = _outputs.OfType<TaprootHtlcOutput>().First();
        var (tx, spent) = Spend(output, 0);

        // Act
        tx.Inputs[0].WitScript = Witness(SignKeyPath(tx, spent, SimpleTaprootVectorHarness.LocalHtlcKey,
                                                           output.Tree.MerkleRoot));

        // Assert
        AssertValidity(tx, spent, false);
    }

    [Theory]
    [InlineData(144u, true)]
    [InlineData(143u, false)]
    public void Given_ASecondLevelOutput_When_SpentThroughTheDelayLeaf_Then_ValidOnlyAfterToSelfDelay(uint sequence,
        bool valid)
    {
        foreach (var desc in _case.HtlcDescs)
        {
            // Arrange
            var (resolution, output) = SecondLevel(desc);
            var (tx, spent) = Spend(resolution, output, sequence);

            // Act
            var sig = SignScriptPath(tx, spent, output.DelayLeaf, SimpleTaprootVectorHarness.LocalDelayedKey);
            tx.Inputs[0].WitScript = Witness(sig, output.DelayLeaf.Script.ToBytes(),
                                                   output.GetControlBlock(output.DelayLeaf));

            // Assert
            AssertValidity(tx, spent, valid);
        }
    }

    [Fact]
    public void Given_ASecondLevelOutput_When_SpentWithTheRevocationKey_Then_ValidThroughTheKeyPath()
    {
        foreach (var desc in _case.HtlcDescs)
        {
            // Arrange
            var (resolution, output) = SecondLevel(desc);
            var (tx, spent) = Spend(resolution, output, 0);

            // Act
            tx.Inputs[0].WitScript = Witness(SignKeyPath(tx, spent, SimpleTaprootVectorHarness.RevocationKey,
                                                               output.Tree.MerkleRoot));

            // Assert
            AssertValidity(tx, spent, true);
        }
    }

    private T Single<T>() where T : BaseOutput => _outputs.OfType<T>().Single();

    /// <summary>The vector's second-level transaction and our model of its output, checked to be the same script.</summary>
    private static (Transaction Resolution, TaprootHtlcResolutionOutput Output) SecondLevel(
        SimpleTaprootVectors.HtlcDesc desc)
    {
        var resolution = Transaction.Parse(desc.ResolutionTxHex, Network.Main);
        var output = new TaprootHtlcResolutionOutput(LightningMoney.Satoshis(resolution.Outputs[0].Value.Satoshi),
                                                     SimpleTaprootVectorHarness.LocalDelayedKey.PubKey,
                                                     SimpleTaprootVectorHarness.RevocationKey.PubKey,
                                                     SimpleTaprootVectors.CsvDelay);
        Assert.Equal(resolution.Outputs[0].ScriptPubKey, output.Tree.ScriptPubKey);
        return (resolution, output);
    }

    private (Transaction Tx, TxOut Spent) Spend(BaseTaprootOutput output, uint sequence, uint lockTime = 0) =>
        Spend(_commitment, output, sequence, lockTime);

    private static (Transaction Tx, TxOut Spent) Spend(Transaction previous, BaseTaprootOutput output,
                                                       uint sequence, uint lockTime = 0)
    {
        var vout = previous.Outputs.FindIndex(o => o.ScriptPubKey == output.Tree.ScriptPubKey);
        Assert.True(vout >= 0, "The output is not in the transaction");
        var spent = previous.Outputs[vout];

        var tx = Network.Main.CreateTransaction();
        tx.Version = 2;
        tx.LockTime = new LockTime(lockTime);
        tx.Inputs.Add(new OutPoint(previous, vout), null, null, new Sequence(sequence));
        tx.Outputs.Add(spent.Value - Money.Satoshis(200), s_destination);
        return (tx, spent);
    }

    private static byte[] SignScriptPath(Transaction tx, TxOut spent, TapScript leaf, Key key)
    {
        var sigHash = tx.GetSignatureHashTaproot([spent],
                                                 new TaprootExecutionData(0, leaf.LeafHash)
                                                 {
                                                     SigHash = TaprootSigHash.Default
                                                 });
        return TaprootSignatures.Sign(key, sigHash, s_zeroAuxRandomness);
    }

    private static byte[] SignKeyPath(Transaction tx, TxOut spent, Key internalKey, uint256 merkleRoot)
    {
        var sigHash = tx.GetSignatureHashTaproot([spent],
                                                 new TaprootExecutionData(0) { SigHash = TaprootSigHash.Default });
        return internalKey.SignTaprootKeySpend(sigHash, merkleRoot, TaprootSigHash.Default).ToBytes();
    }

    private static WitScript Witness(params byte[][] items) => new(items);

    private static void AssertValidity(Transaction tx, TxOut spent, bool valid)
    {
        var error = tx.CreateValidator([spent]).ValidateInput(0).Error;
        if (valid)
            Assert.Null(error);
        else
            Assert.NotNull(error);
    }
}