using System.Security.Cryptography;

namespace NLightning.Domain.Tests.Onchain;

using Domain.Crypto.ValueObjects;
using Domain.Onchain.Enums;
using Domain.Onchain.Models;
using Domain.Onchain.Parsers;

/// <summary>
/// BOLT 5 plan O3-T5: the spend path and preimage of each BOLT 3 HTLC witness shape (the Appendix C vectors run in
/// Infrastructure.Bitcoin.Tests/Onchain/PreimageExtractionVectorTests).
/// </summary>
public class HtlcWitnessParserTests
{
    private static readonly byte[] s_signature = [0x30, .. Enumerable.Repeat((byte)0x44, 70), 0x01];
    private static readonly byte[] s_script = Enumerable.Repeat((byte)0x76, 133).ToArray();
    private static readonly byte[] s_preimage = Enumerable.Repeat((byte)0x07, 32).ToArray();
    private static readonly Hash s_paymentHash = SHA256.HashData(s_preimage);
    private static readonly byte[] s_pubKey = [0x02, .. Enumerable.Repeat((byte)0x11, 32)];

    [Fact]
    public void Given_HtlcSuccessWitness_When_Extracting_Then_Preimage()
    {
        // Arrange
        byte[][] witness = [[], s_signature, s_signature, s_preimage, s_script];

        // Act
        var parsed = HtlcWitnessParser.Parse(witness);
        var found = HtlcWitnessParser.TryExtractPreimage(witness, s_paymentHash, out var preimage);

        // Assert
        Assert.Equal(HtlcSpendPath.HtlcSuccessTransaction, parsed.Path);
        Assert.True(found);
        Assert.Equal(s_preimage, (byte[])preimage);
    }

    [Fact]
    public void Given_DirectPreimageClaim_When_Extracting_Then_Preimage()
    {
        // Arrange: <remotehtlcsig> <payment_preimage> <script> on an offered output
        byte[][] witness = [s_signature, s_preimage, s_script];

        // Act
        var found = HtlcWitnessParser.TryExtractPreimage(witness, s_paymentHash, out var preimage);

        // Assert
        Assert.Equal(HtlcSpendPath.PreimageClaim, HtlcWitnessParser.Parse(witness).Path);
        Assert.True(found);
        Assert.Equal(s_preimage, (byte[])preimage);
    }

    [Theory]
    [MemberData(nameof(NonPreimageWitnesses))]
    public void Given_WitnessWithoutPreimage_When_Extracting_Then_NoneAndPathKnown(string name, byte[][] witness,
                                                                                    HtlcSpendPath expectedPath)
    {
        // Act
        var found = HtlcWitnessParser.TryExtractPreimage(witness, s_paymentHash, out _);

        // Assert
        Assert.False(found, name);
        Assert.Equal(expectedPath, HtlcWitnessParser.Parse(witness).Path);
    }

    public static TheoryData<string, byte[][], HtlcSpendPath> NonPreimageWitnesses => new()
    {
        { "htlc-timeout tx", [[], s_signature, s_signature, [], s_script], HtlcSpendPath.HtlcTimeoutTransaction },
        { "timeout claim", [s_signature, [], s_script], HtlcSpendPath.TimeoutClaim },
        { "revocation", [s_signature, s_pubKey, s_script], HtlcSpendPath.Revocation },
        { "to_local penalty", [s_signature, [0x01], s_script], HtlcSpendPath.Unknown },
        { "p2wpkh", [s_signature, s_pubKey], HtlcSpendPath.Unknown },
        { "empty", [], HtlcSpendPath.Unknown },
        { "funding 2-of-2", [[], s_signature, s_signature, s_script], HtlcSpendPath.Unknown },
        { "33-byte item in preimage slot", [[], s_signature, s_signature, s_pubKey, s_script], HtlcSpendPath.Unknown },
        { "no signature", [s_script, s_preimage, s_script], HtlcSpendPath.Unknown }
    };

    [Fact]
    public void Given_32ByteItemOfAnotherHash_When_Extracting_Then_None()
    {
        // Arrange: the shape is a preimage claim, but the item is not this HTLC's preimage
        byte[][] witness = [s_signature, Enumerable.Repeat((byte)0x08, 32).ToArray(), s_script];

        // Act
        var found = HtlcWitnessParser.TryExtractPreimage(witness, s_paymentHash, out _);

        // Assert
        Assert.Equal(HtlcSpendPath.PreimageClaim, HtlcWitnessParser.Parse(witness).Path);
        Assert.False(found);
    }

    [Fact]
    public void Given_NullOrNullItems_When_Parsing_Then_UnknownNeverThrows()
    {
        // Act / Assert
        Assert.Equal(HtlcSpendPath.Unknown, HtlcWitnessParser.Parse(null).Path);
        Assert.Equal(HtlcSpendPath.Unknown, HtlcWitnessParser.Parse([s_signature, null!, s_script]).Path);
        Assert.False(HtlcWitnessParser.TryExtractPreimage(null, s_paymentHash, out _));
        Assert.False(HtlcWitnessParser.TryExtractPreimage([s_signature, s_preimage, s_script], default, out _));
    }

    [Fact]
    public void Given_ChainTx_When_ExtractingForSpentOutpoint_Then_OnlyThatInputIsRead()
    {
        // Arrange: input 1 spends the HTLC output with the preimage; input 0 spends something else
        var htlcTxId = OnchainTestData.TxIdOf(0x61);
        var spender = new ChainTx(OnchainTestData.TxIdOf(0x62), 2, 0,
                                  [
                                      new ChainTxInput(OnchainTestData.TxIdOf(0x63), 0, 0, [s_signature, s_pubKey]),
                                      new ChainTxInput(htlcTxId, 3, 0, [s_signature, s_preimage, s_script])
                                  ], []);

        // Act
        var found = HtlcWitnessParser.TryExtractPreimage(spender, htlcTxId, 3, s_paymentHash, out var preimage);
        var otherVout = HtlcWitnessParser.TryExtractPreimage(spender, htlcTxId, 2, s_paymentHash, out _);

        // Assert
        Assert.True(found);
        Assert.Equal(s_preimage, (byte[])preimage);
        Assert.False(otherVout);
    }
}