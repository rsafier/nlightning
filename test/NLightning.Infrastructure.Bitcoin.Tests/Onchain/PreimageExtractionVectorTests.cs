using System.Security.Cryptography;
using NBitcoin;
using NLightning.Integration.Tests.BOLT3;
using NLightning.Integration.Tests.BOLT3.Vectors;

namespace NLightning.Infrastructure.Bitcoin.Tests.Onchain;

using Domain.Crypto.ValueObjects;
using Domain.Onchain.Enums;
using Domain.Onchain.Parsers;
using Infrastructure.Bitcoin.Onchain;

/// <summary>
/// BOLT 5 plan O3-T5 vectors: every BOLT 3 Appendix C HTLC-success transaction yields its HTLC's preimage from the
/// input spending the commitment output, and no HTLC-timeout transaction yields one.
/// </summary>
public class PreimageExtractionVectorTests
{
    public static TheoryData<string, int> AppendixCHtlcTransactions
    {
        get
        {
            var data = new TheoryData<string, int>();
            foreach (var vector in Bolt3SpecVectors.AppendixC)
                foreach (var htlcTx in vector.HtlcTxs)
                    data.Add(vector.Name, htlcTx.OutputIndex);
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(AppendixCHtlcTransactions))]
    public void Given_AppendixCHtlcTransaction_When_ExtractingPreimage_Then_OnlySuccessRevealsIt(string name,
        int outputIndex)
    {
        // Arrange: the HTLC tx spends output #outputIndex of the vector commitment
        var vector = Bolt3SpecVectors.GetAppendixC(name);
        var htlcTx = vector.HtlcTxs.Single(h => h.OutputIndex == outputIndex);
        var commitment = Transaction.Parse(vector.CommitTxHex, Network.Main);
        var spender = ChainTxMapper.FromTransaction(Transaction.Parse(htlcTx.TxHex, Network.Main));
        var expectedPreimage = Bolt3VectorHarness.Preimages[htlcTx.HtlcId!.Value];
        Hash paymentHash = SHA256.HashData(expectedPreimage);

        // Act
        var found = HtlcWitnessParser.TryExtractPreimage(spender, commitment.GetHash().ToBytes(), (uint)outputIndex,
                                                         paymentHash, out var preimage);
        var path = HtlcWitnessParser.Parse(spender.Inputs[0].Witness).Path;

        // Assert
        if (htlcTx.IsSuccess!.Value)
        {
            Assert.Equal(HtlcSpendPath.HtlcSuccessTransaction, path);
            Assert.True(found);
            Assert.Equal(expectedPreimage, (byte[])preimage);
        }
        else
        {
            Assert.Equal(HtlcSpendPath.HtlcTimeoutTransaction, path);
            Assert.False(found);
        }
    }

    [Fact]
    public void Given_HtlcSuccessOfAnotherHtlc_When_ExtractingWithThisHash_Then_None()
    {
        // Arrange: HTLC 0's success tx checked against HTLC 1's payment hash
        var vector = Bolt3SpecVectors.GetAppendixC("commitment tx with all five HTLCs untrimmed (minimum feerate)");
        var htlcTx = vector.HtlcTxs.First(h => h.IsSuccess == true && h.HtlcId == 0);
        var spender = ChainTxMapper.FromTransaction(Transaction.Parse(htlcTx.TxHex, Network.Main));
        Hash otherHash = SHA256.HashData(Bolt3VectorHarness.Preimages[1]);

        // Act
        var found = HtlcWitnessParser.TryExtractPreimage(spender.Inputs[0].Witness, otherHash, out _);

        // Assert
        Assert.False(found);
    }
}