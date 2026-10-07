using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NBitcoin;

namespace NLightning.Infrastructure.Bitcoin.Tests.Wallet.SilentPayments;

using Bitcoin.Crypto.SilentPayments;
using Bitcoin.Wallet.SilentPayments;
using Crypto.SilentPayments;
using Domain.Bitcoin.Enums;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.SilentPayments;
using Domain.Bitcoin.SilentPayments.Interfaces;
using Domain.Bitcoin.ValueObjects;
using Domain.Bitcoin.Wallet.Models;
using Domain.Crypto.ValueObjects;
using static Crypto.SilentPayments.Bip352VectorKit;

/// <summary>The receiver vectors exercise the production block pre-pass, classifier, key-source port and scanner.</summary>
public class SilentPaymentScannerTests
{
    public static TheoryData<int, int, string> ReceivingCases => Cases("receiving");

    [Theory]
    [MemberData(nameof(ReceivingCases))]
    public async Task Given_OfficialReceivingVector_When_PreparingBlock_Then_ExactOutputsAndTweakMetadataMatch(
        int index, int subcase, string description)
    {
        // Arrange
        Assert.False(string.IsNullOrWhiteSpace(description));
        var vector = Vectors[index].GetProperty("receiving")[subcase];
        var given = vector.GetProperty("given");
        var expected = vector.GetProperty("expected");
        var (block, previous) = CreateBlock(given);
        var keys = new VectorKeys(given.GetProperty("key_material"));
        var scanner = CreateScanner(new VectorPrevouts(previous), keys, minReceiveSat: 0);
        var labels = given.GetProperty("labels").EnumerateArray()
            .Select(value => new SilentPaymentLabelModel(value.GetUInt32(), $"label-{value.GetUInt32()}", 0)).ToArray();

        // Act
        using var held = await scanner.EnterAsync(TestContext.Current.CancellationToken);
        var matches = await scanner.PrepareAsync(block, 100, labels, TestContext.Current.CancellationToken);

        // Assert
        if (expected.TryGetProperty("n_outputs", out var count))
        {
            Assert.Equal(count.GetInt32(), matches.Count);
            Assert.Equal(Bip352.MaxRecipients, matches.Count);
            return;
        }
        var expectedOutputs = expected.GetProperty("outputs").EnumerateArray().ToDictionary(
            output => output.GetProperty("pub_key").GetString()!, StringComparer.Ordinal);
        Assert.Equal(expectedOutputs.Count, matches.Count);
        Assert.All(matches, match =>
        {
            Assert.False(match.Ignored);
            Assert.Equal(100u, match.BlockHeight);
            Assert.Equal(block.BlockHash, match.BlockHash);
            Assert.True(expectedOutputs.TryGetValue(Hex(match.OutputKey), out var output));
            var rawTweak = new NBitcoin.Secp256k1.Scalar(match.Tweak, out _);
            if (match.Label is { } label)
            {
                var labelTweak = Bip352.ComputeLabelTweak(keys.ScanSecret, label);
                rawTweak = rawTweak.Add(new NBitcoin.Secp256k1.Scalar(labelTweak, out _));
            }
            var combined = new byte[32];
            rawTweak.WriteToSpan(combined);
            Assert.Equal(output.GetProperty("priv_key_tweak").GetString(), Hex(combined));
        });
    }

    [Fact]
    public async Task Given_DustAtKZero_When_PreparingBlock_Then_IgnoredReceiptStillAdvancesToKOne()
    {
        // Arrange: append a second output using the vector receiver's k=1 key.
        var given = Vectors[0].GetProperty("receiving")[0].GetProperty("given");
        var (serialized, previous) = CreateBlock(given);
        var block = Block.Load(serialized.BlockData, Network.RegTest);
        var shared = Hex(Vectors[0].GetProperty("receiving")[0].GetProperty("expected").GetProperty("shared_secret"));
        var spend = Bip352.IndividualPublicKey(Hex(given.GetProperty("key_material").GetProperty("spend_priv_key")));
        var secondKey = Bip352.AddPublicTweak(spend, Bip352.SharedSecretTweak(shared, 1));
        block.Transactions[0].Outputs[0].Value = Money.Satoshis(500);
        block.Transactions[0].Outputs.Add(Money.Satoshis(2000), new Script([0x51, 0x20, .. secondKey.AsSpan(1).ToArray()]));
        var txId = new TxId(block.Transactions[0].GetHash().ToBytes());
        var inputs = previous.Values.Single();
        previous = new Dictionary<TxId, IReadOnlyList<BitcoinPrevout>> { [txId] = inputs };
        serialized = new BitcoinBlock(block.ToBytes(), new Hash(block.GetHash().ToBytes()), 1);
        var scanner = CreateScanner(new VectorPrevouts(previous), new VectorKeys(given.GetProperty("key_material")), 1000);

        // Act
        using var held = await scanner.EnterAsync(TestContext.Current.CancellationToken);
        var matches = await scanner.PrepareAsync(serialized, 100, [], TestContext.Current.CancellationToken);

        // Assert
        Assert.Contains(matches, match => match.Index == 0 && match.Ignored);
        Assert.Contains(matches, match => match.Index == 1 && !match.Ignored);
    }

    [Fact]
    public async Task Given_FutureWitnessInput_When_PreparingBlock_Then_WholeTransactionIsIneligible()
    {
        // Arrange
        var given = Vectors[0].GetProperty("receiving")[0].GetProperty("given");
        var (block, previous) = CreateBlock(given);
        var inputList = previous.Values.Single().ToArray();
        inputList[0] = new BitcoinPrevout(10_000, new byte[] { 0x52, 0x20, .. new byte[32] });
        previous = new Dictionary<TxId, IReadOnlyList<BitcoinPrevout>> { [previous.Keys.Single()] = inputList };
        var scanner = CreateScanner(new VectorPrevouts(previous), new VectorKeys(given.GetProperty("key_material")), 0);

        // Act
        using var held = await scanner.EnterAsync(TestContext.Current.CancellationToken);
        var matches = await scanner.PrepareAsync(block, 100, [], TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(matches);
    }

    [Fact]
    public async Task Given_MissingPrevouts_When_PreparingBlock_Then_FailsInsteadOfSkippingPossiblePayments()
    {
        // Arrange
        var given = Vectors[0].GetProperty("receiving")[0].GetProperty("given");
        var (block, _) = CreateBlock(given);
        var scanner = CreateScanner(new VectorPrevouts(new Dictionary<TxId, IReadOnlyList<BitcoinPrevout>>()),
            new VectorKeys(given.GetProperty("key_material")), 0);

        // Act / Assert
        using var held = await scanner.EnterAsync(TestContext.Current.CancellationToken);
        var error = await Assert.ThrowsAsync<AggregateException>(() => scanner.PrepareAsync(block, 100, [], TestContext.Current.CancellationToken));
        Assert.IsType<InvalidOperationException>(Assert.Single(error.InnerExceptions));
    }

    private static SilentPaymentScanner CreateScanner(IBlockPrevoutSource previous, ISilentPaymentKeySource keys,
        long minReceiveSat) => new(previous, new SilentPaymentCrypto(), keys, Options.Create(new SilentPaymentsOptions
        { Enabled = true, RecoveryLabelCount = 0, MinReceiveSat = minReceiveSat }), NullLogger<SilentPaymentScanner>.Instance);

    private static (BitcoinBlock Block, IReadOnlyDictionary<TxId, IReadOnlyList<BitcoinPrevout>> Previous) CreateBlock(JsonElement given)
    {
        var transaction = Network.RegTest.CreateTransaction();
        var previous = new List<BitcoinPrevout>();
        foreach (var input in given.GetProperty("vin").EnumerateArray())
        {
            var outpoint = Outpoint(input);
            transaction.Inputs.Add(new TxIn(new OutPoint(new uint256(outpoint.AsSpan(0, 32).ToArray()),
                System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(outpoint.AsSpan(32))),
                new Script(Hex(input.GetProperty("scriptSig")))) { WitScript = new WitScript(Witness(input)) });
            previous.Add(new BitcoinPrevout(10_000,
                Hex(input.GetProperty("prevout").GetProperty("scriptPubKey").GetProperty("hex"))));
        }
        foreach (var output in given.GetProperty("outputs").EnumerateArray())
            transaction.Outputs.Add(Money.Satoshis(2000), new Script([0x51, 0x20, .. Hex(output)]));
        var block = Network.RegTest.Consensus.ConsensusFactory.CreateBlock();
        block.Transactions.Add(transaction);
        block.UpdateMerkleRoot();
        return (new BitcoinBlock(block.ToBytes(), new Hash(block.GetHash().ToBytes()), 1),
            new Dictionary<TxId, IReadOnlyList<BitcoinPrevout>> { [new TxId(transaction.GetHash().ToBytes())] = previous });
    }

    private sealed class VectorPrevouts(IReadOnlyDictionary<TxId, IReadOnlyList<BitcoinPrevout>> previous) : IBlockPrevoutSource
    {
        public SilentPaymentPrevoutSource Source => SilentPaymentPrevoutSource.GetRawTransaction;
        public Task ProbeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ValidateHeightAsync(uint height, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyDictionary<TxId, IReadOnlyList<BitcoinPrevout>>> GetPrevoutsAsync(BitcoinBlock block,
            uint height, CancellationToken cancellationToken = default) => Task.FromResult(previous);
    }

    private sealed class VectorKeys(JsonElement material) : ISilentPaymentKeySource
    {
        public byte[] ScanSecret { get; } = Hex(material.GetProperty("scan_priv_key"));
        public CompactPubKey ScanPubKey => new(Bip352.IndividualPublicKey(ScanSecret));
        public CompactPubKey SpendPubKey => new(Bip352.IndividualPublicKey(Hex(material.GetProperty("spend_priv_key"))));
        public bool RecoverableElsewhere => true;
        public void ComputeScanSharedSecret(ReadOnlySpan<byte> point, Span<byte> destination) =>
            Bip352.ComputeSharedSecret(point, ScanSecret).CopyTo(destination);
        public void GetLabelTweak(uint label, Span<byte> destination) => Bip352.ComputeLabelTweak(ScanSecret, label).CopyTo(destination);
        public CompactPubKey GetLabelPoint(uint label) => new(Bip352.IndividualPublicKey(Bip352.ComputeLabelTweak(ScanSecret, label)));
    }
}