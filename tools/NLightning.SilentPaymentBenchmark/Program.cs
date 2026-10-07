using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NBitcoin;

namespace NLightning.SilentPaymentBenchmark;

using Domain.Bitcoin.Enums;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.SilentPayments;
using Domain.Bitcoin.SilentPayments.Models;
using Domain.Bitcoin.ValueObjects;
using Domain.Bitcoin.Wallet.Models;
using Domain.Protocol.ValueObjects;
using Infrastructure.Bitcoin.Crypto.SilentPayments;
using Infrastructure.Bitcoin.Managers;
using Infrastructure.Bitcoin.Wallet.SilentPayments;

/// <summary>Offline synthetic-block scanner proof. No RPC, database, or network access; prevouts are preloaded.</summary>
public static class Program
{
    public static async Task Main(string[] args)
    {
        var warmup = ReadCount(args, "--warmup", 2, 0, 20);
        var samples = ReadCount(args, "--samples", 10, 1, 100);
        var labels = ReadCount(args, "--labels", 100, 0, 100_000);
        var adversarial = args.Contains("--adversarial");
        var directory = Path.Combine(Path.GetTempPath(), "nltg-sp-benchmark-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            // Public test mnemonic, used only in memory; never saved to a key file.
            using var keys = SecureKeyManager.FromMnemonic(
                "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about",
                string.Empty, BitcoinNetwork.Regtest, Path.Combine(directory, "unsaved.json"));
            var crypto = new SilentPaymentCrypto();
            var (block, source) = BuildBlock(keys, crypto, adversarial);
            var scanner = new SilentPaymentScanner(source, crypto, keys,
                Options.Create(new SilentPaymentsOptions { Enabled = true, Receive = true, RecoveryLabelCount = labels }),
                NullLogger<SilentPaymentScanner>.Instance);
            var measurements = new List<double>();
            var allocations = new List<long>();
            var expected = adversarial ? 2323 : 0;
            for (var index = 0; index < warmup + samples; index++)
            {
                using var lease = await scanner.EnterAsync();
                var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
                var timer = Stopwatch.StartNew();
                var found = await scanner.PrepareAsync(block, 200, []);
                timer.Stop();
                var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
                Console.Error.WriteLine($"Round {index + 1}/{warmup + samples}: {timer.Elapsed.TotalMilliseconds:F1} ms, {found.Count} matches");
                try
                {
                    if (found.Count != expected)
                        throw new InvalidOperationException($"Expected {expected} matches, got {found.Count}.");
                    if (index >= warmup)
                    {
                        measurements.Add(timer.Elapsed.TotalMilliseconds);
                        allocations.Add(allocated);
                    }
                }
                finally
                {
                    foreach (var output in found) CryptographicOperations.ZeroMemory(output.Tweak);
                }
            }
            measurements.Sort();
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                runtime = RuntimeInformation.FrameworkDescription,
                os = RuntimeInformation.OSDescription,
                architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                logical_cpus = Environment.ProcessorCount,
                workload = adversarial ? "4000 transactions, 2000 eligible, one 2323-output K_max transaction" : "4000 transactions, 2000 eligible, four taproot outputs each",
                serialized_block_bytes = block.BlockData.Length,
                configured_recovery_labels = labels,
                warmup_rounds = warmup,
                sample_rounds = samples,
                matched_outputs_per_round = expected,
                min_ms = measurements[0],
                median_ms = (measurements[(measurements.Count - 1) / 2] + measurements[measurements.Count / 2]) / 2,
                p95_ms = measurements[(int)Math.Ceiling(measurements.Count * 0.95) - 1],
                max_ms = measurements[^1],
                samples_ms = measurements,
                allocated_bytes_per_sample = allocations,
                limitation = "Preloaded prevouts; includes block parsing and scanner maths, excludes RPC/storage. Cloud timing does not certify the Mac reference-machine budget."
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static (BitcoinBlock Block, InMemoryPrevouts Source) BuildBlock(SecureKeyManager keys,
        SilentPaymentCrypto crypto, bool adversarial)
    {
        var block = Network.RegTest.Consensus.ConsensusFactory.CreateBlock();
        var previous = new Dictionary<TxId, IReadOnlyList<BitcoinPrevout>>();
        var senderSecret = new byte[32];
        senderSecret[31] = 2;
        try
        {
            using var sender = new Key(senderSecret);
            using var unrelated = new Key(Enumerable.Repeat((byte)9, 32).ToArray());
            var ordinaryOutput = unrelated.PubKey.GetAddress(ScriptPubKeyType.TaprootBIP86, Network.RegTest).ScriptPubKey;
            for (var index = 0; index < 4000; index++)
            {
                var parent = new byte[32];
                BitConverter.GetBytes(index + 1).CopyTo(parent, 0);
                var transaction = Network.RegTest.CreateTransaction();
                var input = new TxIn(new OutPoint(new uint256(parent), 0));
                input.WitScript = new WitScript(Op.GetPushOp(new byte[] { 1 }), Op.GetPushOp(sender.PubKey.ToBytes()));
                transaction.Inputs.Add(input);
                if (index < 2000)
                {
                    if (adversarial && index == 0)
                    {
                        var outpoint = new byte[36];
                        parent.CopyTo(outpoint, 0);
                        var recipient = new SilentPaymentRecipient(keys.ScanPubKey, keys.SpendPubKey);
                        var derived = crypto.DeriveOutputs([new SilentPaymentSenderInput(outpoint, senderSecret, false)],
                            Enumerable.Repeat(recipient, 2323).ToArray());
                        try
                        {
                            foreach (var output in derived)
                                transaction.Outputs.Add(Money.Satoshis(1000), new Script([0x51, 0x20, .. output.OutputKey32]));
                        }
                        finally { foreach (var output in derived) CryptographicOperations.ZeroMemory(output.Tweak32); }
                    }
                    else
                    {
                        var count = adversarial ? 1 : 4;
                        for (var output = 0; output < count; output++)
                            transaction.Outputs.Add(Money.Satoshis(1000), ordinaryOutput);
                    }
                    previous[new TxId(transaction.GetHash().ToBytes())] =
                        [new BitcoinPrevout(3_000_000, sender.PubKey.WitHash.ScriptPubKey.ToBytes())];
                }
                else transaction.Outputs.Add(Money.Satoshis(1000), sender.PubKey.WitHash.ScriptPubKey);
                block.Transactions.Add(transaction);
            }
            block.UpdateMerkleRoot();
            return (new BitcoinBlock(block.ToBytes(), block.GetHash().ToBytes(), block.Transactions.Count),
                new InMemoryPrevouts(previous));
        }
        finally { CryptographicOperations.ZeroMemory(senderSecret); }
    }

    private static int ReadCount(string[] args, string name, int fallback, int minimum, int maximum)
    {
        var index = Array.IndexOf(args, name);
        if (index < 0) return fallback;
        if (index + 1 >= args.Length || !int.TryParse(args[index + 1], out var value) || value < minimum || value > maximum)
            throw new ArgumentException($"{name} must be between {minimum} and {maximum}.");
        return value;
    }

    private sealed class InMemoryPrevouts(IReadOnlyDictionary<TxId, IReadOnlyList<BitcoinPrevout>> previous) : IBlockPrevoutSource
    {
        public SilentPaymentPrevoutSource Source => SilentPaymentPrevoutSource.GetBlock;
        public Task ProbeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ValidateHeightAsync(uint height, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyDictionary<TxId, IReadOnlyList<BitcoinPrevout>>> GetPrevoutsAsync(BitcoinBlock block,
            uint height, CancellationToken cancellationToken = default) => Task.FromResult(previous);
    }
}