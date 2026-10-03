using NBitcoin;

namespace NLightning.Infrastructure.Bitcoin.Tests.Taproot;

using Bitcoin.Builders;
using Bitcoin.Comparers;
using Bitcoin.Outputs;
using Domain.Bitcoin.Transactions.Enums;
using Domain.Money;

/// <summary>
/// The taproot paths of <see cref="HtlcTransactionBuilder"/> outside the vectors' happy path: what a
/// build result carries, the preimage rules of the witness, the fee-input paths kept for T4, and the cltv tie-break of
/// two taproot offered HTLC outputs with the same script.
/// </summary>
public class SimpleTaprootHtlcBuilderTests
{
    [Fact]
    public void Given_ATaprootHtlcTransaction_When_Built_Then_ItCarriesTheSpentOutputAndAValidControlBlock()
    {
        // Arrange
        var harness = new SimpleTaprootVectorHarness(SimpleTaprootVectors.Transactions[1]);
        var (model, built, htlcTxs) = harness.Build();
        var commitment = Transaction.Load(built.Transaction.RawTxBytes, Network.Main);
        var outputs = CommitmentTransactionBuilder.CreateSimpleTaprootOutputs(model).Select(o => o.Output)
                                                  .OfType<TaprootHtlcOutput>().ToList();

        for (var i = 0; i < htlcTxs.Count; i++)
        {
            // Act
            var result = harness.HtlcBuilder.Build(htlcTxs[i]);

            // Assert: the spent leaf with its control block commits to the commitment output's key
            Assert.True(result.IsTaproot);
            var spent = commitment.Outputs[built.HtlcOutputsInTxOrder[i].Vout];
            Assert.Equal(spent.ScriptPubKey.ToBytes(), (byte[])result.SpentScriptPubKey!.Value);
            Assert.Equal(spent.Value.Satoshi, result.SpentAmount.Satoshi);
            var controlBlock = ControlBlock.FromSlice(result.ControlBlock!);
            var leaf = new TapScript(new Script((byte[])result.SpentWitnessScript), TapLeafVersion.C0);
            var outputKey = outputs.First(o => o.Tree.ScriptPubKey == spent.ScriptPubKey).Tree.OutputKey;
            Assert.True(controlBlock.VerifyTaprootCommitment(outputKey, leaf));
        }
    }

    [Fact]
    public void Given_AWrongPreimageUse_When_AddingTheWitness_Then_ItThrows()
    {
        // Arrange
        var vectorCase = SimpleTaprootVectors.Transactions[1];
        var harness = new SimpleTaprootVectorHarness(vectorCase);
        var (_, _, htlcTxs) = harness.Build();
        var success = htlcTxs.First(h => h.Type == HtlcTransactionType.Success);
        var timeout = htlcTxs.First(h => h.Type == HtlcTransactionType.Timeout);
        var signature = new byte[64];

        // Act / Assert
        Assert.Throws<ArgumentException>(() => harness.HtlcBuilder.AddWitness(
                                             success, harness.HtlcBuilder.Build(success), signature, signature));
        Assert.Throws<ArgumentException>(() => harness.HtlcBuilder.AddWitness(
                                             timeout, harness.HtlcBuilder.Build(timeout), signature, signature,
                                             new byte[32]));
    }

    [Fact]
    public void Given_ATaprootHtlcTransaction_When_AskedForFeeInputs_Then_NotSupportedYet()
    {
        // Arrange: the taproot HTLC transaction's weights with fee inputs are lane T4's
        var harness = new SimpleTaprootVectorHarness(SimpleTaprootVectors.Transactions[1]);
        var (_, _, htlcTxs) = harness.Build();
        var built = harness.HtlcBuilder.Build(htlcTxs[0]);

        // Act / Assert
        Assert.Throws<NotSupportedException>(() => harness.HtlcBuilder.EstimateAnchorBaseWeight(htlcTxs[0], built,
                                                                                                22));
        Assert.Throws<NotSupportedException>(() => harness.HtlcBuilder.AddFeeInputs(htlcTxs[0], built, [],
                                                                                   new byte[22], 1000));
    }

    [Fact]
    public void Given_TwoTaprootOfferedHtlcsWithTheSameScript_When_Sorted_Then_TheLowerCltvExpiryComesFirst()
    {
        // Arrange: an offered HTLC's tapscripts do not commit to cltv_expiry, so the BOLT 3 tie-break decides
        var local = SimpleTaprootVectors.PubKey("derived_local_htlc_pubkey");
        var remote = SimpleTaprootVectors.PubKey("derived_remote_htlc_pubkey");
        var revocation = SimpleTaprootVectors.PubKey("derived_revocation_pubkey");
        var hash = new byte[32];
        var later = new TaprootOfferedHtlcOutput(LightningMoney.Satoshis(5000), 600, local, hash, remote, revocation);
        var earlier = new TaprootOfferedHtlcOutput(LightningMoney.Satoshis(5000), 500, local, hash, remote, revocation);

        // Act
        var comparison = TransactionOutputComparer.Instance.Compare(later, earlier);

        // Assert
        Assert.Equal(later.ScriptPubKey, earlier.ScriptPubKey);
        Assert.True(comparison > 0);
        Assert.True(TransactionOutputComparer.Instance.Compare(earlier, later) < 0);
    }
}