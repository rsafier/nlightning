using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NBitcoin;

namespace NLightning.Application.Bitcoin.SilentPayments;

using Domain.Accounting.Constants;
using Domain.Accounting.Enums;
using Domain.Accounting.Labels;
using Domain.Accounting.Models;
using Domain.Accounting.Services;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.SilentPayments;
using Domain.Bitcoin.SilentPayments.Interfaces;
using Domain.Bitcoin.SilentPayments.Models;
using Domain.Bitcoin.ValueObjects;
using Domain.Bitcoin.Wallet.Models;
using Domain.Crypto.ValueObjects;
using Domain.Node.Options;
using Domain.Persistence.Interfaces;
using Infrastructure.Bitcoin.Networks;
using Infrastructure.Bitcoin.Wallet.Interfaces;
using Infrastructure.Bitcoin.Wallet.SilentPayments;
using Hash = Domain.Crypto.ValueObjects.Hash;

/// <summary>Durable SP-only recovery. A block's discoveries, spends, journal facts and cursor commit together.</summary>
public sealed class SilentPaymentService(IServiceScopeFactory scopes, IBitcoinChainService chain,
    IBlockchainMonitor monitor, IBlockPrevoutSource prevouts, SilentPaymentScanner scanner,
    ISilentPaymentKeySource keys, ISilentPaymentCrypto crypto, IOptions<SilentPaymentsOptions> options,
    IOptions<NodeOptions> nodeOptions, ILogger<SilentPaymentService> logger, TimeProvider? timeProvider = null)
    : BackgroundService, ISilentPaymentService
{
    private const int FinalizationBatchSize = 100;
    private readonly SilentPaymentsOptions _options = options.Value;
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private string? _lastError;

    public async Task<SilentPaymentAddressResult> GetAddressAsync(string? labelName = null,
                                                                 CancellationToken cancellationToken = default)
    {
        RequireReceiving();
        using var held = await scanner.EnterAsync(cancellationToken);
        await using var scope = scopes.CreateAsyncScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var state = await EnsureStateAsync(uow, cancellationToken);
        uint? label = null;
        if (labelName is not null)
        {
            labelName = SourceLabels.Create(labelName, null).Label
                ?? throw new ArgumentException("A label name cannot be empty.", nameof(labelName));
            if (labelName == "change")
                label = 0;
            else
            {
                var labels = await uow.SilentPaymentDbRepository.GetLabelsAsync(cancellationToken);
                var existing = labels.FirstOrDefault(item => item.Name == labelName);
                if (existing is not null)
                    label = existing.M;
                else
                {
                    if (labels.Count >= _options.MaxLabels)
                        throw new InvalidOperationException("Silent payment operator label limit reached.");
                    label = labels.Count == 0 ? 1 : checked(labels.Max(item => item.M) + 1);
                    uow.SilentPaymentDbRepository.AddLabel(new SilentPaymentLabelModel(label.Value, labelName,
                        state.LiveCursorHeight ?? state.LiveFromHeight));
                }
            }
        }

        var address = EncodeAddress(label);
        cancellationToken.ThrowIfCancellationRequested();
        await uow.SaveChangesAsync();
        return new SilentPaymentAddressResult(address, label, labelName, keys.RecoverableElsewhere);
    }

    public async Task<IReadOnlyList<SilentPaymentLabelInfo>> ListLabelsAsync(CancellationToken cancellationToken = default)
    {
        RequireReceiving();
        using var held = await scanner.EnterAsync(cancellationToken);
        await using var scope = scopes.CreateAsyncScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var labels = await uow.SilentPaymentDbRepository.GetLabelsAsync(cancellationToken);
        var result = new List<SilentPaymentLabelInfo> { new(0, "change", 0, EncodeAddress(0), true) };
        result.AddRange(labels.Select(label => new SilentPaymentLabelInfo(label.M, label.Name,
            label.CreatedAtHeight, EncodeAddress(label.M))));
        return result;
    }

    public async Task<SilentPaymentStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        using var held = await scanner.EnterAsync(cancellationToken);
        return await ReadStatusAsync(cancellationToken);
    }

    public async Task<SilentPaymentStatus> StartRescanAsync(uint fromHeight, uint? recoveryLabels = null,
                                                           CancellationToken cancellationToken = default)
    {
        RequireReceiving();
        var count = recoveryLabels ?? checked((uint)_options.RecoveryLabelCount);
        if (count > 100_000)
            throw new ArgumentOutOfRangeException(nameof(recoveryLabels), "Recovery labels must be 0..100000.");
        using var held = await scanner.EnterAsync(cancellationToken);
        await prevouts.ProbeAsync(cancellationToken);
        await prevouts.ValidateHeightAsync(fromHeight, cancellationToken);
        await using var scope = scopes.CreateAsyncScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var state = await EnsureStateAsync(uow, cancellationToken);
        if (state.RescanTargetHeight is not null)
            throw new InvalidOperationException("A silent payment rescan is already pending; cancel it before restarting.");
        if (state.LiveFromHeight == 0 || fromHeight >= state.LiveFromHeight)
            throw new ArgumentOutOfRangeException(nameof(fromHeight), "Rescan starts before the live scanning boundary.");
        uint? cursor = fromHeight == 0 ? null : fromHeight - 1;
        Hash? cursorHash = null;
        if (cursor is { } height)
            cursorHash = new Hash((await chain.GetBlockHashAsync(height).WaitAsync(cancellationToken)).ToBytes());
        await uow.SilentPaymentDbRepository.SetScanStateAsync(state with
        {
            BirthdayHeight = Math.Min(state.BirthdayHeight, fromHeight),
            RescanCursorHeight = cursor,
            RescanCursorHash = cursorHash,
            RescanTargetHeight = state.LiveFromHeight - 1,
            RecoveryLabelCount = count
        }, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        await uow.SaveChangesAsync();
        _lastError = null;
        return await ReadStatusAsync(cancellationToken);
    }

    public async Task<SilentPaymentStatus> CancelRescanAsync(CancellationToken cancellationToken = default)
    {
        using var held = await scanner.EnterAsync(cancellationToken);
        await using var scope = scopes.CreateAsyncScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        if (await uow.SilentPaymentDbRepository.GetScanStateAsync(cancellationToken) is { } state)
        {
            await uow.SilentPaymentDbRepository.SetScanStateAsync(state with
            { RescanTargetHeight = null, RescanCursorHeight = null, RescanCursorHash = null }, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            await uow.SaveChangesAsync();
        }
        _lastError = null;
        return await ReadStatusAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var worked = false;
            try
            {
                if (_options.Enabled && _options.Receive)
                    worked = await ProcessNextBlockAsync(stoppingToken);
                _lastError = null;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                if (_lastError != exception.Message)
                    logger.LogError(exception, "Silent payment recovery paused; its durable cursor has not advanced");
                _lastError = exception.Message;
            }
            var delay = worked && _options.RescanBlocksPerSecond > 0
                ? TimeSpan.FromSeconds(1d / _options.RescanBlocksPerSecond)
                : worked ? TimeSpan.Zero : TimeSpan.FromSeconds(1);
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, _time, stoppingToken);
        }
    }

    /// <summary>One crash-safe block, also used by recovery proofs without starting a hosted polling loop.</summary>
    internal async Task<bool> ProcessNextBlockAsync(CancellationToken cancellationToken)
    {
        using var held = await scanner.EnterAsync(cancellationToken);
        SilentPaymentScanState? state;
        IReadOnlyList<SilentPaymentLabelModel> labels;
        await using (var read = scopes.CreateAsyncScope())
        {
            var uow = read.ServiceProvider.GetRequiredService<IUnitOfWork>();
            state = await uow.SilentPaymentDbRepository.GetScanStateAsync(cancellationToken);
            if (state?.RescanTargetHeight is null)
                return false;
            labels = await uow.SilentPaymentDbRepository.GetLabelsAsync(cancellationToken);
        }
        await ValidateCursorAsync(state, cancellationToken);
        if (state.RescanCursorHeight is { } cursor && cursor >= state.RescanTargetHeight)
            return await FinishAsync(state, labels, cancellationToken);
        var height = state.RescanCursorHeight is { } previous ? checked(previous + 1) : state.BirthdayHeight;
        if (height >= state.LiveFromHeight)
            throw new InvalidOperationException("Silent payment recovery cursor crossed the live boundary.");
        var block = await chain.GetBlockAsync(height).WaitAsync(cancellationToken)
            ?? throw new InvalidOperationException($"Block {height} unavailable; recovery requires retained block data.");
        var blockValue = ToBlock(block);
        var matches = await scanner.PrepareAsync(blockValue, height, labels, cancellationToken, state.RecoveryLabelCount);
        await using var write = scopes.CreateAsyncScope();
        var work = write.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var received = await scanner.StageReceiptsAsync(matches, work, materializeUtxos: false,
            cancellationToken: cancellationToken);
        foreach (var receipt in received)
            await StageFactAsync(work, receipt.SilentPayment!, null, block, height, labels, cancellationToken);
        var spent = await scanner.StageSpendsAsync(blockValue, height, work, matches, cancellationToken);
        foreach (var (output, spender) in spent)
            if (!output.Ignored)
                await StageFactAsync(work, output, spender, block, height, labels, cancellationToken);
        await StageHistoricalSettlementsAsync(work, block, height, cancellationToken);
        await RequireCanonicalAsync(block, height, cancellationToken);
        await work.SilentPaymentDbRepository.SetScanStateAsync(state with
        { RescanCursorHeight = height, RescanCursorHash = blockValue.BlockHash, PrevoutSource = scanner.PrevoutSource }, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        await work.SaveChangesAsync();
        return true;
    }

    private async Task<bool> FinishAsync(SilentPaymentScanState state, IReadOnlyList<SilentPaymentLabelModel> labels,
                                         CancellationToken cancellationToken)
    {
        var tip = await chain.GetCurrentBlockHeightAsync().WaitAsync(cancellationToken);
        if (monitor.LastProcessedBlockHeight < tip)
            return false;
        var tipHash = await chain.GetBlockHashAsync(tip).WaitAsync(cancellationToken);
        await using var scope = scopes.CreateAsyncScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var current = (await uow.UtxoDbRepository.GetUnspentAsync()).Select(coin => (coin.TxId, coin.Index)).ToHashSet();
        var outputs = await uow.SilentPaymentDbRepository.GetOutputsAsync(cancellationToken);
        var missing = outputs.Where(output => !output.Ignored && output.SpentByTransactionId is null &&
            !current.Contains((output.TransactionId, output.Index))).ToArray();
        var batch = missing.Take(FinalizationBatchSize).ToDictionary(output => (output.TransactionId, output.Index));
        var auditNeeded = new HashSet<(TxId, uint)>();
        var proofs = new Dictionary<(TxId, uint), (TxOut Output, uint Height)>();
        foreach (var (point, output) in batch)
        {
            var proof = await chain.GetConfirmedUnspentOutputAsync(new OutPoint(new uint256(output.TransactionId), output.Index))
                .WaitAsync(cancellationToken);
            if (proof is null)
                auditNeeded.Add(point);
            else
                proofs.Add(point, proof.Value);
        }
        // Live may have observed these spends before historical discovery existed. Audit only this batch's missing points.
        if (auditNeeded.Count != 0)
            for (var height = state.LiveFromHeight; height <= tip && auditNeeded.Count != 0; height++)
            {
                var block = await chain.GetBlockAsync(height).WaitAsync(cancellationToken)
                    ?? throw new InvalidOperationException($"Block {height} unavailable for silent payment spend recovery.");
                foreach (var transaction in block.Transactions.Where(transaction => !transaction.IsCoinBase))
                    foreach (var input in transaction.Inputs)
                    {
                        var point = (new TxId(input.PrevOut.Hash.ToBytes()), input.PrevOut.N);
                        if (!auditNeeded.Remove(point)) continue;
                        var output = batch[point];
                        var spender = new TxId(transaction.GetHash().ToBytes());
                        await uow.SilentPaymentDbRepository.SetSpentAsync(output.TransactionId, output.Index,
                            spender, height, cancellationToken);
                        await StageFactAsync(uow, output, spender, block, height, labels, cancellationToken);
                    }
                await StageHistoricalSettlementsAsync(uow, block, height, cancellationToken);
                await RequireCanonicalAsync(block, height, cancellationToken);
                if (height == uint.MaxValue) break;
            }
        if (auditNeeded.Count != 0)
            throw new InvalidOperationException("A recovered output is spent but its confirmed spender was not found; recovery remains pending.");
        var pendingSpends = false;
        foreach (var (point, proof) in proofs)
        {
            var output = batch[point];
            if (proof.Output.Value.Satoshi != output.AmountSats ||
                !proof.Output.ScriptPubKey.ToBytes().AsSpan().SequenceEqual(new byte[] { 0x51, 0x20 }.Concat(output.OutputKey).ToArray()))
                throw new InvalidOperationException("Recovered output differs from its current-chain proof.");
            if (await chain.GetUnspentOutputAsync(new OutPoint(new uint256(output.TransactionId), output.Index))
                    .WaitAsync(cancellationToken) is null)
            {
                pendingSpends = true;
                continue;
            }
            uow.AddUtxo(new UtxoModel(output));
        }
        if (await chain.GetCurrentBlockHeightAsync().WaitAsync(cancellationToken) != tip ||
            await chain.GetBlockHashAsync(tip).WaitAsync(cancellationToken) != tipHash)
            throw new InvalidOperationException("Chain changed during silent payment recovery finalization.");
        await ValidateCursorAsync(state, cancellationToken);
        await uow.SilentPaymentDbRepository.SetScanStateAsync(state with
        { RescanTargetHeight = pendingSpends || missing.Length > batch.Count ? state.RescanTargetHeight : null }, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        await uow.SaveChangesAsync();
        return !pendingSpends;
    }

    // Historical custody facts must also settle Clearing. Recover the complete transaction only when every
    // input belongs to the recovered wallet; shared transactions require their retained purpose/accounting context.
    private async Task StageHistoricalSettlementsAsync(IUnitOfWork uow, Block block, uint height,
                                                       CancellationToken cancellationToken)
    {
        var network = nodeOptions.Value.BitcoinNetwork.ToNBitcoinNetwork();
        var addresses = uow.WalletAddressesDbRepository.GetAllAddresses().Select(address => address.Address).ToHashSet();
        foreach (var transaction in block.Transactions.Where(transaction => !transaction.IsCoinBase))
        {
            var transactionId = new TxId(transaction.GetHash().ToBytes());
            var inputs = new List<SilentPaymentOutputModel?>();
            foreach (var input in transaction.Inputs)
                inputs.Add(await uow.SilentPaymentDbRepository.GetOutputAsync(new TxId(input.PrevOut.Hash.ToBytes()),
                    input.PrevOut.N, cancellationToken));
            if (!inputs.Any(input => input is { Ignored: false } && input.SpentByTransactionId == transactionId))
                continue;
            var baseKey = AccountingEventKeys.WalletSent(transactionId);
            var prior = await uow.AccountingEventDbRepository.GetByKeyPrefixAsync(baseKey, cancellationToken);
            var key = AccountingConfirmations.NextConfirmationKey(baseKey, prior);
            var standing = AccountingConfirmations.FindStanding(baseKey, prior);
            if (standing is not null && (!standing.Details.TryGetValue("recovered", out var recovered) || recovered != "true"))
                continue;
            // A retained broadcast may represent funding, a sweep or a shared transaction. Its original writer
            // owns settlement; never relabel those movements as a recovered withdrawal.
            var broadcast = await uow.BroadcastTransactionDbRepository.GetByTransactionIdAsync(transactionId);
            if (broadcast is not null && broadcast.Purpose != Domain.Onchain.Enums.BroadcastPurpose.WalletSend)
                continue;
            long inputSat = 0;
            for (var index = 0; index < inputs.Count; index++)
            {
                if (inputs[index] is { Ignored: false } owned)
                    inputSat = checked(inputSat + owned.AmountSats);
                else
                {
                    // Ordinary wallet custody already journaled by the normal monitor is usable proof after its
                    // UTXO was deleted. No script guess or label-zero heuristic establishes input ownership.
                    var point = transaction.Inputs[index].PrevOut;
                    var receipts = await uow.AccountingEventDbRepository.GetByKeyPrefixAsync(
                        AccountingEventKeys.WalletReceived(new TxId(point.Hash.ToBytes()), point.N), cancellationToken);
                    var receivedKey = AccountingEventKeys.WalletReceived(new TxId(point.Hash.ToBytes()), point.N);
                    var received = AccountingConfirmations.FindStanding(receivedKey, receipts);
                    var spends = await uow.AccountingEventDbRepository.GetByKeyPrefixAsync(
                        AccountingEventKeys.WalletOutputSpent(new TxId(point.Hash.ToBytes()), point.N), cancellationToken);
                    var spentKey = AccountingEventKeys.WalletOutputSpent(new TxId(point.Hash.ToBytes()), point.N);
                    var spent = AccountingConfirmations.FindStanding(spentKey, spends);
                    if (received is null || spent is null || spent.BlockHeight != height ||
                        !spent.Details.TryGetValue("spentBy", out var spentBy) || spentBy != transactionId.ToString())
                        throw new InvalidOperationException("Recovered spend has an input without complete wallet accounting provenance; restore its original ledger before continuing.");
                    inputSat = checked(inputSat + received.AmountMsat / 1000);
                }
            }
            long externalSat = 0;
            long outputSat = 0;
            var externalOutputs = 0;
            for (var index = 0; index < transaction.Outputs.Count; index++)
            {
                var output = transaction.Outputs[index];
                outputSat = checked(outputSat + output.Value.Satoshi);
                var owned = await uow.SilentPaymentDbRepository.GetOutputAsync(transactionId, (uint)index, cancellationToken);
                var address = output.ScriptPubKey.GetDestinationAddress(network)?.ToString();
                if (owned is { Ignored: false }) continue;
                if (address is not null && addresses.Contains(address))
                {
                    var receivedKey = AccountingEventKeys.WalletReceived(transactionId, (uint)index);
                    var receipts = await uow.AccountingEventDbRepository.GetByKeyPrefixAsync(receivedKey, cancellationToken);
                    if (AccountingConfirmations.FindStanding(receivedKey, receipts) is null)
                        throw new InvalidOperationException("Recovered spend returns ordinary wallet change without its custody journal; restore the ordinary wallet ledger before continuing.");
                    continue;
                }
                // Ignored SP outputs were deliberately excluded from custody and remain an equity transfer out.
                externalSat = checked(externalSat + output.Value.Satoshi);
                externalOutputs++;
            }
            var feeSat = checked(inputSat - outputSat);
            if (feeSat < 0) throw new InvalidOperationException("Recovered spend outputs exceed its proven wallet inputs.");
            var amountMsat = -checked(externalSat * 1000);
            var feeMsat = checked(feeSat * 1000);
            if (standing is not null)
            {
                if (standing.AmountMsat == amountMsat && standing.FeeMsat == feeMsat) continue;
                // Threshold promotion can reveal own change previously excluded from custody. Correct only our
                // recovery settlement, retaining the original immutable fact and its explicit policy reversal.
                var reversal = new AccountingEventModel
                {
                    EventKey = AccountingEventKeys.Reversal(standing.EventKey, standing.BlockHeight!.Value),
                    Kind = AccountingEventKind.Reversal, OccurredAt = _time.GetUtcNow(), BlockHeight = standing.BlockHeight,
                    TxId = transactionId, AmountMsat = -standing.AmountMsat, FeeMsat = -standing.FeeMsat,
                    Finality = AccountingFinality.Confirmed,
                    Details = AccountingDetailsCodec.Create((AccountingConfirmations.ReversesDetail, standing.EventKey),
                        (AccountingConfirmations.OriginalKindDetail, standing.Kind.ToString()), ("reason", "recovered_custody_policy_changed"))
                };
                uow.AccountingEventDbRepository.Add(reversal);
                key = AccountingConfirmations.NextConfirmationKey(baseKey, prior.Append(reversal).ToArray());
            }
            uow.AccountingEventDbRepository.Add(new AccountingEventModel
            {
                EventKey = key!, Kind = AccountingEventKind.WalletSent, OccurredAt = block.Header.BlockTime,
                BlockHeight = height, TxId = transactionId, AmountMsat = amountMsat,
                FeeMsat = feeMsat, Finality = AccountingFinality.Confirmed,
                Details = AccountingDetailsCodec.Create(("recovered", "true"), ("purposeUnknown", broadcast is null ? "true" : null),
                    ("externalOutputs", externalOutputs.ToString(CultureInfo.InvariantCulture)))
            });
        }
    }

    private async Task StageFactAsync(IUnitOfWork uow, SilentPaymentOutputModel output, TxId? spender,
        Block block, uint height, IReadOnlyList<SilentPaymentLabelModel> labels, CancellationToken cancellationToken)
    {
        var baseKey = spender is null ? AccountingEventKeys.WalletReceived(output.TransactionId, output.Index)
            : AccountingEventKeys.WalletOutputSpent(output.TransactionId, output.Index);
        var prior = await uow.AccountingEventDbRepository.GetByKeyPrefixAsync(baseKey, cancellationToken);
        var key = AccountingConfirmations.NextConfirmationKey(baseKey, prior);
        if (key is null) return;
        var txid = spender ?? output.TransactionId;
        var transaction = block.Transactions.First(transaction => new TxId(transaction.GetHash().ToBytes()) == txid);
        var source = AccountingDetailKeys.ExternalSource;
        if (await uow.BroadcastTransactionDbRepository.GetByTransactionIdAsync(txid) is not null)
            source = AccountingDetailKeys.BroadcastSource;
        else
            foreach (var input in transaction.Inputs)
                if (await uow.SilentPaymentDbRepository.GetOutputAsync(new TxId(input.PrevOut.Hash.ToBytes()), input.PrevOut.N,
                        cancellationToken) is not null || await uow.UtxoDbRepository.GetByIdAsync(new TxId(input.PrevOut.Hash.ToBytes()), input.PrevOut.N) is not null)
                { source = AccountingDetailKeys.WalletSource; break; }
        var name = output.Label is { } label ? labels.FirstOrDefault(item => item.M == label)?.Name : null;
        uow.AccountingEventDbRepository.Add(new AccountingEventModel
        {
            EventKey = key, Kind = spender is null ? AccountingEventKind.WalletReceived : AccountingEventKind.WalletOutputSpent,
            OccurredAt = block.Header.BlockTime, BlockHeight = height, TxId = output.TransactionId, OutputIndex = output.Index,
            AmountMsat = checked(output.AmountSats * 1000) * (spender is null ? 1 : -1), Finality = AccountingFinality.Confirmed,
            Details = AccountingDetailsCodec.Create((AccountingDetailKeys.Source, source), ("addressType", "P2Tr"),
                ("address", new Script(new byte[] { 0x51, 0x20 }.Concat(output.OutputKey).ToArray())
                    .GetDestinationAddress(nodeOptions.Value.BitcoinNetwork.ToNBitcoinNetwork())?.ToString()),
                ("receiptSource", "silent_payment"), ("silentPayment", "true"), (AccountingDetailKeys.Label, name),
                ("silentPaymentLabel", output.Label?.ToString(CultureInfo.InvariantCulture)), ("spentBy", spender?.ToString()))
        });
    }

    private async Task<SilentPaymentScanState> EnsureStateAsync(IUnitOfWork uow, CancellationToken cancellationToken)
    {
        if (await uow.SilentPaymentDbRepository.GetScanStateAsync(cancellationToken) is { } state) return state;
        var tip = await chain.GetCurrentBlockHeightAsync().WaitAsync(cancellationToken);
        state = new SilentPaymentScanState(_options.BirthdayHeight ?? tip, tip,
            RecoveryLabelCount: checked((uint)_options.RecoveryLabelCount));
        await uow.SilentPaymentDbRepository.SetScanStateAsync(state, cancellationToken);
        return state;
    }

    private async Task<SilentPaymentStatus> ReadStatusAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var state = await uow.SilentPaymentDbRepository.GetScanStateAsync(cancellationToken);
        var outputs = await uow.SilentPaymentDbRepository.GetOutputsAsync(cancellationToken);
        return new SilentPaymentStatus(_options.Enabled, _options.Enabled && _options.Send,
            _options.Enabled && _options.Receive, keys.RecoverableElsewhere, state?.BirthdayHeight, state?.LiveFromHeight,
            state?.LiveCursorHeight, state?.RescanCursorHeight, state?.RescanTargetHeight, state?.RecoveryLabelCount ?? 0,
            state?.PrevoutSource, outputs.Count(output => !output.Ignored), outputs.Count(output => output.Ignored),
            outputs.Count(output => !output.Ignored && output.SpentByTransactionId is null), scanner.LastScanMilliseconds, _lastError);
    }

    private string EncodeAddress(uint? label)
    {
        var spend = keys.SpendPubKey;
        if (label is { } value && !crypto.TrySumPublicKeys([spend, keys.GetLabelPoint(value)], out spend))
            throw new InvalidOperationException("Silent payment label yields an invalid spend key.");
        return SilentPaymentAddressCodec.Encode(keys.ScanPubKey, spend, nodeOptions.Value.BitcoinNetwork);
    }

    private void RequireReceiving()
    {
        if (!_options.Enabled || !_options.Receive)
            throw new InvalidOperationException("Silent payment receiving is disabled.");
        if (nodeOptions.Value.BitcoinNetwork.Name == "mainnet" && !_options.AllowMainnet)
            throw new InvalidOperationException("Silent payment mainnet use requires AllowMainnet=true.");
    }

    private async Task ValidateCursorAsync(SilentPaymentScanState state, CancellationToken cancellationToken)
    {
        if (state.RescanCursorHeight is { } height && state.RescanCursorHash is { } hash &&
            new Hash((await chain.GetBlockHashAsync(height).WaitAsync(cancellationToken)).ToBytes()) != hash)
            throw new InvalidOperationException("Recovery checkpoint was reorganized; waiting for the wallet rollback before advancing.");
    }

    private async Task RequireCanonicalAsync(Block block, uint height, CancellationToken cancellationToken)
    {
        if (await chain.GetBlockHashAsync(height).WaitAsync(cancellationToken) != block.GetHash())
            throw new InvalidOperationException("Chain changed while preparing a silent payment recovery block.");
    }

    private static BitcoinBlock ToBlock(Block block) => new(block.ToBytes(), new Hash(block.GetHash().ToBytes()), block.Transactions.Count);
}