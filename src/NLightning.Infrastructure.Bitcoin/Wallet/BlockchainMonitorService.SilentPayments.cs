using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NBitcoin;

namespace NLightning.Infrastructure.Bitcoin.Wallet;

using Domain.Accounting.Constants;
using Domain.Bitcoin.Events;
using Domain.Bitcoin.SilentPayments;
using Domain.Bitcoin.ValueObjects;
using Domain.Bitcoin.Wallet.Models;
using Domain.Crypto.ValueObjects;
using Domain.Persistence.Interfaces;
using SilentPayments;

public partial class BlockchainMonitorService
{
    private SilentPaymentScanner? _silentPaymentScanner;

    private sealed record PreparedSilentPaymentBlock(BitcoinBlock Block,
        IReadOnlyList<SilentPaymentOutputModel> Matches, IReadOnlyList<SilentPaymentLabelModel> Labels,
        SilentPaymentScanState State, bool AdvanceLiveCursor);

    private async Task InitializeSilentPaymentsAsync(uint tip, CancellationToken cancellationToken)
    {
        var options = _serviceProvider.GetService<IOptions<SilentPaymentsOptions>>()?.Value;
        if (options is null) return;
        using var scope = _serviceProvider.CreateScope();
        using var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var repository = unitOfWork.SilentPaymentDbRepository;
        var state = await repository.GetScanStateAsync(cancellationToken);
        if (state is null && (!options.Enabled || !options.Receive)) return;
        _silentPaymentScanner = _serviceProvider.GetRequiredService<SilentPaymentScanner>();
        using var held = await _silentPaymentScanner.EnterAsync(cancellationToken);
        await _silentPaymentScanner.InitializeAsync(cancellationToken);
        if (state is null)
        {
            state = new SilentPaymentScanState(options.BirthdayHeight ?? tip, tip,
                PrevoutSource: _silentPaymentScanner.PrevoutSource,
                RecoveryLabelCount: checked((uint)options.RecoveryLabelCount));
            await repository.SetScanStateAsync(state, cancellationToken);
            await unitOfWork.SaveChangesAsync();
        }
        else if (_silentPaymentScanner.Enabled &&
                 (state.LiveCursorHeight ?? (state.LiveFromHeight > 0 ? state.LiveFromHeight - 1 : (uint?)null)) is { } cursor &&
                 cursor < _lastProcessedBlockHeight && cursor < tip)
        {
            // A disabled/offline SP gap is recovered by the SP-only rescan worker. The global cursor is unchanged.
            var resume = state.RescanCursorHeight is { } rescan ? Math.Min(rescan, cursor) : cursor;
            var resumeHash = new Hash((await _bitcoinChainService.GetBlockHashAsync(resume)).ToBytes());
            state = state with
            {
                LiveFromHeight = tip,
                RescanCursorHeight = resume,
                RescanCursorHash = resumeHash,
                RescanTargetHeight = tip > 0 ? tip - 1 : 0
            };
            await repository.SetScanStateAsync(state, cancellationToken);
            await unitOfWork.SaveChangesAsync();
        }

        // NL-1298: say at start whether and from where silent payments are scanned
        _logger.LogInformation(
            "Silent payment scanning {State}: birthday {Birthday}, live from {LiveFrom}, live cursor {Cursor}, prevouts "
          + "from {Source}{Rescan}", _silentPaymentScanner.Enabled ? "on" : "off (receive disabled)",
            state.BirthdayHeight, state.LiveFromHeight, state.LiveCursorHeight?.ToString() ?? "none",
            _silentPaymentScanner.PrevoutSource,
            state.RescanTargetHeight is { } target ? $", rescanning to {target}" : string.Empty);
    }

    private async Task<PreparedSilentPaymentBlock?> PrepareSilentPaymentBlockAsync(Block block, uint height,
        CancellationToken cancellationToken)
    {
        if (_silentPaymentScanner is null) return null;
        SilentPaymentScanState state;
        IReadOnlyList<SilentPaymentLabelModel> labels;
        using (var scope = _serviceProvider.CreateScope())
        {
            using var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            state = await unitOfWork.SilentPaymentDbRepository.GetScanStateAsync(cancellationToken)
                 ?? throw new InvalidOperationException("Silent payment scan state is missing.");
            labels = await unitOfWork.SilentPaymentDbRepository.GetLabelsAsync(cancellationToken);
        }
        var serialized = new BitcoinBlock(block.ToBytes(), new Hash(block.GetHash().ToBytes()), block.Transactions.Count);
        var advance = _silentPaymentScanner.Enabled && height >= state.LiveFromHeight;
        var matches = advance ? await _silentPaymentScanner.PrepareAsync(serialized, height, labels, cancellationToken) : [];
        return new PreparedSilentPaymentBlock(serialized, matches, labels, state, advance);
    }

    private async Task StageSilentPaymentInputOwnershipAsync(PreparedSilentPaymentBlock? prepared,
        IUnitOfWork unitOfWork, BlockEffects effects, Block block)
    {
        if (prepared is null || _silentPaymentScanner is null) return;
        var accepted = prepared.Matches.Where(output => !output.Ignored)
            .Select(output => new OutPoint(new uint256((byte[])output.TransactionId), output.Index)).ToHashSet();
        foreach (var transaction in block.Transactions.Where(transaction => !transaction.IsCoinBase))
            foreach (var input in transaction.Inputs)
            {
                var point = new TxId(input.PrevOut.Hash.ToBytes());
                if (accepted.Contains(input.PrevOut) ||
                    await unitOfWork.SilentPaymentDbRepository.GetOutputAsync(point, input.PrevOut.N) is { Ignored: false })
                {
                    effects.SilentPaymentInputs.Add(input.PrevOut);
                    continue;
                }
                var receiptKey = AccountingEventKeys.WalletReceived(point, input.PrevOut.N);
                var receipts = await unitOfWork.AccountingEventDbRepository.GetByKeyPrefixAsync(receiptKey);
                var receipt = Domain.Accounting.Services.AccountingConfirmations.FindStanding(receiptKey, receipts);
                if (receipt?.Details.GetValueOrDefault(WalletRecoveryAccounting.RecoveredCustody) == "true")
                    effects.SilentPaymentInputs.Add(input.PrevOut);
            }
    }

    private async Task StageSilentPaymentSettlementsAsync(PreparedSilentPaymentBlock? prepared,
        IUnitOfWork unitOfWork, BlockEffects effects, Block block)
    {
        if (prepared is null || _silentPaymentScanner is null) return;
        if (effects.SilentPaymentInputs.Count == 0)
        {
            await SilentPaymentAccounting.StageCollaborativeFlowsAsync(unitOfWork, block, effects.Height,
                _timeProvider, CancellationToken.None);
            return;
        }
        await WalletRecoveryAccounting.StageInputSpendsAsync(unitOfWork, block, effects.Height, CancellationToken.None);
        var memory = _serviceProvider.GetService<Domain.Bitcoin.Interfaces.IUtxoMemoryRepository>();
        var excluded = block.Transactions.Where(transaction => !transaction.Inputs.Any(input =>
                effects.SilentPaymentInputs.Contains(input.PrevOut)) ||
                ClassifyWalletTransaction(transaction, memory, effects).Source == ChannelSource)
            .Select(transaction => new TxId(transaction.GetHash().ToBytes())).ToHashSet();
        await SilentPaymentAccounting.StageSettlementsAsync(unitOfWork, block, effects.Height, prepared.Labels,
            _network, _timeProvider, CancellationToken.None, excluded);
    }

    private async Task StageSilentPaymentReceiptsAsync(PreparedSilentPaymentBlock? prepared, IUnitOfWork unitOfWork,
        BlockEffects effects, Block block)
    {
        if (prepared is null || _silentPaymentScanner is null) return;
        var memory = _serviceProvider.GetService<Domain.Bitcoin.Interfaces.IUtxoMemoryRepository>();
        var received = await _silentPaymentScanner.StageReceiptsAsync(prepared.Matches, unitOfWork, memory);
        foreach (var coin in received)
        {
            var transaction = block.Transactions.Single(transaction => new TxId(transaction.GetHash().ToBytes()) == coin.TxId);
            effects.StagedDeposits[new OutPoint(new uint256((byte[])coin.TxId), coin.Index)] = coin;
            var source = ClassifyWalletTransaction(transaction, memory, effects);
            var labelName = prepared.Labels.FirstOrDefault(label => label.M == coin.SilentPayment!.Label)?.Name;
            CollectWalletReceived(coin, null, source, effects, labelName, block.Header.BlockTime);
            var destination = new Script([0x51, 0x20, .. coin.SilentPayment!.OutputKey]).GetDestinationAddress(_network)!.ToString();
            effects.Movements.Add(new WalletMovementEventArgs(destination, coin.Amount, coin.TxId,
                effects.Height, coin.Index));
        }
    }

    private async Task StageSilentPaymentSpendsAndStateAsync(PreparedSilentPaymentBlock? prepared, uint height,
        IUnitOfWork unitOfWork, BlockEffects effects, Block block)
    {
        if (prepared is null || _silentPaymentScanner is null) return;
        var spends = await _silentPaymentScanner.StageSpendsAsync(prepared.Block, height, unitOfWork, prepared.Matches);
        var memory = _serviceProvider.GetService<Domain.Bitcoin.Interfaces.IUtxoMemoryRepository>();
        foreach (var (output, spender) in spends)
        {
            if (output.Ignored || effects.Accounting.Any(candidate =>
                    candidate.BaseKey == AccountingEventKeys.WalletOutputSpent(output.TransactionId, output.Index)))
                continue;
            // Recovery receipts can exist only in metadata until their unspent state is proven. Their live spends
            // still need a journal fact in the same save as the spend marker, even with no selectable UTXO.
            var coin = new UtxoModel(output with { SpentByTransactionId = null, SpentAtHeight = null });
            var transaction = block.Transactions.Single(transaction => new TxId(transaction.GetHash().ToBytes()) == spender);
            var source = ClassifyWalletTransaction(transaction, memory, effects);
            if (source.Source == ExternalSource) source = new WalletTransactionSource(WalletSource, null, null);
            CollectWalletOutputSpent(coin, transaction, source, memory, effects, block.Header.BlockTime);
        }
        if (spends.Any(spend => !spend.Output.Ignored))
        {
            var observations = new Dictionary<OutPoint, UtxoModel>(effects.StagedDeposits);
            foreach (var (output, _) in spends.Where(spend => !spend.Output.Ignored))
                observations[new OutPoint(new uint256((byte[])output.TransactionId), output.Index)] =
                    new UtxoModel(output with { SpentByTransactionId = null, SpentAtHeight = null });
            foreach (var spender in spends.Where(spend => !spend.Output.Ignored).Select(spend => spend.Spender).Distinct())
            {
                var transaction = block.Transactions.Single(transaction => new TxId(transaction.GetHash().ToBytes()) == spender);
                if (DescribeWalletTransaction(transaction, height, block.GetHash().ToString(), observations,
                        timestamp: block.Header.BlockTime) is not { } observed) continue;
                var existing = effects.WalletTransactions.FindIndex(item => item.TxHash == observed.TxHash);
                if (existing >= 0) effects.WalletTransactions[existing] = observed;
                else effects.WalletTransactions.Add(observed);
            }
        }
        if (prepared.AdvanceLiveCursor && (prepared.State.LiveCursorHeight is null || height >= prepared.State.LiveCursorHeight))
            await unitOfWork.SilentPaymentDbRepository.SetScanStateAsync(prepared.State with
            {
                LiveCursorHeight = height,
                LiveCursorHash = prepared.Block.BlockHash,
                PrevoutSource = _silentPaymentScanner.PrevoutSource
            });
    }

    private async Task StageSilentPaymentRollbackAsync(IUnitOfWork unitOfWork, uint forkHeight,
        List<UtxoModel> removed, List<(UtxoModel, uint)> restored)
    {
        if (_silentPaymentScanner is null) return;
        var outputs = await unitOfWork.SilentPaymentDbRepository.GetOutputsAsync();
        foreach (var output in outputs)
        {
            if (output.BlockHeight > forkHeight)
            {
                // Historical rescan facts may never have materialized a UTXO; their receipts still need reversal.
                if (!output.Ignored)
                {
                    var disconnectedCoin = new UtxoModel(output with { SpentByTransactionId = null, SpentAtHeight = null });
                    if (!removed.Any(existing => existing.TxId == disconnectedCoin.TxId && existing.Index == disconnectedCoin.Index))
                        removed.Add(disconnectedCoin);
                    // A creation and spend both disconnected need both facts reversed; this coin is never restored.
                    if (output.SpentAtHeight is { } spentAbove && spentAbove > forkHeight &&
                        !restored.Any(row => row.Item1.TxId == disconnectedCoin.TxId && row.Item1.Index == disconnectedCoin.Index))
                        restored.Add((disconnectedCoin, spentAbove));
                }
                continue;
            }
            if (output.SpentAtHeight is not { } spentHeight || spentHeight <= forkHeight) continue;
            var unspentMetadata = output with { SpentByTransactionId = null, SpentAtHeight = null };
            await unitOfWork.SilentPaymentDbRepository.UpsertOutputAsync(unspentMetadata);
            if (output.Ignored) continue;
            var coin = new UtxoModel(unspentMetadata);
            // A disconnected spend restores confirmed custody even when it is rebroadcast into the mempool.
            // Pending broadcast outpoint exclusion and fee reservations keep our pending inputs unselectable.
            if (!restored.Any(row => row.Item1.TxId == coin.TxId && row.Item1.Index == coin.Index))
                restored.Add((coin, spentHeight));
            var memory = _serviceProvider.GetService<Domain.Bitcoin.Interfaces.IUtxoMemoryRepository>();
            if (memory?.TryGetUtxo(coin.TxId, coin.Index, out _) == true) continue;
            var outpoint = new OutPoint(new uint256((byte[])coin.TxId), coin.Index);
            var unspent = await _bitcoinChainService.GetConfirmedUnspentOutputAsync(outpoint);
            if (unspent is not { } previous || previous.Output.Value.Satoshi != output.AmountSats ||
                !previous.Output.ScriptPubKey.ToBytes().AsSpan().SequenceEqual((byte[])[0x51, 0x20, .. output.OutputKey]))
                continue;
            unitOfWork.AddUtxo(coin);
        }
        await unitOfWork.SilentPaymentDbRepository.DeleteOutputsAboveHeightAsync(forkHeight);
        var state = await unitOfWork.SilentPaymentDbRepository.GetScanStateAsync();
        if (state is not null && (state.LiveCursorHeight > forkHeight || state.RescanCursorHeight > forkHeight))
        {
            var forkHash = TryGetKnownHash(forkHeight, out var knownFork) ? knownFork : (Hash?)null;
            await unitOfWork.SilentPaymentDbRepository.SetScanStateAsync(state with
            {
                LiveCursorHeight = state.LiveCursorHeight > forkHeight ? forkHeight : state.LiveCursorHeight,
                LiveCursorHash = state.LiveCursorHeight > forkHeight ? forkHash : state.LiveCursorHash,
                RescanCursorHeight = state.RescanCursorHeight > forkHeight ? forkHeight : state.RescanCursorHeight,
                RescanCursorHash = state.RescanCursorHeight > forkHeight ? forkHash : state.RescanCursorHash
            });
        }
    }

}