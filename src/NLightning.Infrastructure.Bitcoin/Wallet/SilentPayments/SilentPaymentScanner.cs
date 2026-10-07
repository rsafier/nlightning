using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NBitcoin;

namespace NLightning.Infrastructure.Bitcoin.Wallet.SilentPayments;

using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.SilentPayments;
using Domain.Bitcoin.SilentPayments.Interfaces;
using Domain.Bitcoin.SilentPayments.Models;
using Domain.Bitcoin.ValueObjects;
using Domain.Bitcoin.Wallet.Models;
using Domain.Crypto.ValueObjects;
using Domain.Persistence.Interfaces;

/// <summary>Computes matches before the block write unit of work. No private scan key leaves its owner.</summary>
public sealed class SilentPaymentScanner(IBlockPrevoutSource prevouts, ISilentPaymentCrypto crypto,
                                        ISilentPaymentKeySource keys, IOptions<SilentPaymentsOptions> options,
                                        ILogger<SilentPaymentScanner> logger)
{
    private static readonly Meter s_meter = new("NLightning.SilentPayments");
    private static readonly Histogram<double> s_scanMilliseconds = s_meter.CreateHistogram<double>(
        "nlightning.silentpayments.block_scan_ms", "ms");
    private static readonly Counter<long> s_dustIgnored = s_meter.CreateCounter<long>(
        "nlightning.silentpayments.dust_ignored");
    private readonly SilentPaymentsOptions _options = options.Value;
    private readonly SemaphoreSlim _roundGate = new(1, 1);
    private readonly ConcurrentDictionary<uint, CompactPubKey> _labelPointCache = new();

    public bool Enabled => _options.Enabled && _options.Receive;
    public uint? BirthdayHeight => _options.BirthdayHeight;
    public string PrevoutSource => prevouts.Source.ToString();
    public double LastScanMilliseconds { get; private set; }

    public Task InitializeAsync(CancellationToken cancellationToken = default) =>
        Enabled ? prevouts.ProbeAsync(cancellationToken) : Task.CompletedTask;

    /// <summary>Hold this lease across preparation, staging and commit, for both live blocks and rescans.</summary>
    public async ValueTask<IDisposable> EnterAsync(CancellationToken cancellationToken = default)
    {
        await _roundGate.WaitAsync(cancellationToken);
        return new ScanLease(_roundGate);
    }

    private sealed class ScanLease(SemaphoreSlim gate) : IDisposable
    {
        private SemaphoreSlim? _gate = gate;
        public void Dispose() => Interlocked.Exchange(ref _gate, null)?.Release();
    }

    /// <summary>Called under a scan lease; public arithmetic runs in parallel before a write unit of work is opened.</summary>
    public async Task<IReadOnlyList<SilentPaymentOutputModel>> PrepareAsync(BitcoinBlock block, uint height,
        IReadOnlyList<SilentPaymentLabelModel> labels, CancellationToken cancellationToken = default,
        uint? recoveryLabelCount = null)
    {
        if (!Enabled) return [];
        var watch = Stopwatch.StartNew();
        var matches = new List<SilentPaymentOutputModel>();
        try
        {
            var raw = Block.Load(block.BlockData, Network.Main);
            var eligible = raw.Transactions.Where(transaction => !transaction.IsCoinBase &&
                transaction.Outputs.Any(output => IsTaproot(output.ScriptPubKey.ToBytes()))).ToArray();
            if (eligible.Length == 0) return [];
            var previous = await prevouts.GetPrevoutsAsync(block, height, cancellationToken);
            var prepared = new byte[]?[eligible.Length];
            Parallel.For(0, eligible.Length, new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = Environment.ProcessorCount
            }, index =>
            {
                var transaction = eligible[index];
                var txId = new TxId(transaction.GetHash().ToBytes());
                if (!previous.TryGetValue(txId, out var inputs) || inputs.Count != transaction.Inputs.Count)
                    throw new InvalidOperationException($"Missing silent-payment prevouts for {txId}.");
                var publicKeys = new List<CompactPubKey>();
                byte[]? smallest = null;
                for (var i = 0; i < inputs.Count; i++)
                {
                    var script = (byte[])inputs[i].ScriptPubKey;
                    if (SilentPaymentInputClassifier.IsFutureWitnessVersion(script)) return;
                    var input = transaction.Inputs[i];
                    var outpoint = new byte[36];
                    input.PrevOut.Hash.ToBytes().CopyTo(outpoint, 0);
                    BinaryPrimitives.WriteUInt32LittleEndian(outpoint.AsSpan(32), input.PrevOut.N);
                    if (smallest is null || outpoint.AsSpan().SequenceCompareTo(smallest) < 0) smallest = outpoint;
                    if (crypto.TryGetInputPublicKey(script, input.ScriptSig.ToBytes(), input.WitScript.Pushes.ToArray(), out var key))
                        publicKeys.Add(key);
                }
                if (smallest is null || !crypto.TrySumPublicKeys(publicKeys, out var sum)) return;
                try
                {
                    var hash = crypto.ComputeInputHash(smallest, sum);
                    prepared[index] = (byte[])crypto.TweakInputPublicKey(sum, hash);
                }
                catch (ArgumentException)
                {
                    // BIP 352 invalid aggregate/hash makes only this transaction ineligible.
                }
            });
            var labelCount = recoveryLabelCount ?? checked((uint)_options.RecoveryLabelCount);
            if (labelCount > 100_000) throw new ArgumentOutOfRangeException(nameof(recoveryLabelCount));
            var labelPoints = new Dictionary<uint, CompactPubKey> { [0] = GetLabelPoint(0) };
            for (uint label = 1; label <= labelCount; label++)
                labelPoints[label] = GetLabelPoint(label);
            foreach (var label in labels) labelPoints[label.M] = GetLabelPoint(label.M);
            var scanContext = crypto.PrepareScanContext(labelPoints);
            for (var index = 0; index < eligible.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (prepared[index] is not { } inputKey) continue;
                var transaction = eligible[index];
                var shared = new byte[33];
                try
                {
                    keys.ComputeScanSharedSecret(inputKey, shared);
                    var candidates = transaction.Outputs.Select((output, outputIndex) => (output, outputIndex))
                        .Where(x => IsTaproot(x.output.ScriptPubKey.ToBytes()))
                        .Select(x => new SilentPaymentScanCandidate((uint)x.outputIndex,
                            x.output.ScriptPubKey.ToBytes().AsSpan(2).ToArray())).ToArray();
                    var discovered = scanContext is null
                        ? crypto.Scan(shared, keys.SpendPubKey, candidates, labelPoints)
                        : crypto.ScanPrepared(shared, keys.SpendPubKey, candidates, scanContext);
                    foreach (var match in discovered)
                    {
                        var amount = transaction.Outputs[(int)match.OutputIndex].Value.Satoshi;
                        var ignored = amount < _options.MinReceiveSat;
                        if (ignored) s_dustIgnored.Add(1);
                        matches.Add(new SilentPaymentOutputModel(new TxId(transaction.GetHash().ToBytes()), match.OutputIndex,
                            match.OutputKey32, match.Tweak32, match.Label, amount, height, block.BlockHash, Ignored: ignored));
                    }
                }
                finally { CryptographicOperations.ZeroMemory(shared); }
            }
            return matches;
        }
        catch
        {
            foreach (var match in matches) CryptographicOperations.ZeroMemory(match.Tweak);
            throw;
        }
        finally
        {
            LastScanMilliseconds = watch.Elapsed.TotalMilliseconds;
            s_scanMilliseconds.Record(LastScanMilliseconds);
            if (LastScanMilliseconds > 2500)
                logger.LogWarning("Silent payment scan of block {Height} took {ElapsedMs} ms", height, LastScanMilliseconds);
        }
    }

    /// <summary>Stages new discoveries, including dust metadata. Memory changes only through the committing unit of work.</summary>
    public async Task<IReadOnlyList<UtxoModel>> StageReceiptsAsync(IReadOnlyList<SilentPaymentOutputModel> matches,
        IUnitOfWork unitOfWork, IUtxoMemoryRepository? memory = null, bool materializeUtxos = true,
        CancellationToken cancellationToken = default)
    {
        var received = new List<UtxoModel>();
        foreach (var match in matches)
        {
            if (memory?.TryGetUtxo(match.TransactionId, match.Index, out _) == true) continue;
            var existing = await unitOfWork.SilentPaymentDbRepository.GetOutputAsync(
                match.TransactionId, match.Index, cancellationToken);
            if (existing is not null && (materializeUtxos || !existing.Ignored || match.Ignored)) continue;
            // A lower receive threshold can recover previously ignored receipts in the existing database. Only
            // historical recovery promotes them: subsequent blocks and final chain proof determine whether they
            // are still unspent, before any promoted coin can become selectable.
            var receipt = existing is { Ignored: true }
                ? match with { SpentByTransactionId = null, SpentAtHeight = null }
                : match;
            await unitOfWork.SilentPaymentDbRepository.UpsertOutputAsync(receipt, cancellationToken);
            if (receipt.Ignored) continue;
            var coin = new UtxoModel(receipt);
            if (materializeUtxos) unitOfWork.AddUtxo(coin);
            received.Add(coin);
        }
        return received;
    }

    /// <summary>Retains metadata for every spent discovery, including same-block receipts and ignored dust.</summary>
    public async Task<IReadOnlyList<(SilentPaymentOutputModel Output, TxId Spender)>> StageSpendsAsync(
        BitcoinBlock block, uint height, IUnitOfWork unitOfWork, IReadOnlyList<SilentPaymentOutputModel> matches,
        CancellationToken cancellationToken = default)
    {
        var raw = Block.Load(block.BlockData, Network.Main);
        var staged = matches.ToDictionary(output => (output.TransactionId, output.Index));
        var spent = new List<(SilentPaymentOutputModel, TxId)>();
        foreach (var transaction in raw.Transactions.Where(transaction => !transaction.IsCoinBase))
        {
            var spender = new TxId(transaction.GetHash().ToBytes());
            foreach (var input in transaction.Inputs)
            {
                var key = (new TxId(input.PrevOut.Hash.ToBytes()), input.PrevOut.N);
                var output = await unitOfWork.SilentPaymentDbRepository.GetOutputAsync(key.Item1, key.N, cancellationToken);
                output ??= staged.GetValueOrDefault(key);
                if (output is null || output.SpentByTransactionId == spender && output.SpentAtHeight == height) continue;
                await unitOfWork.SilentPaymentDbRepository.UpsertOutputAsync(output with
                { SpentByTransactionId = spender, SpentAtHeight = height }, cancellationToken);
                if (!output.Ignored) unitOfWork.TrySpendUtxo(output.TransactionId, output.Index);
                spent.Add((output, spender));
            }
        }
        return spent;
    }

    private CompactPubKey GetLabelPoint(uint label) => _labelPointCache.GetOrAdd(label, keys.GetLabelPoint);

    private static bool IsTaproot(byte[] script) => script is [0x51, 0x20, ..] && script.Length == 34;
}