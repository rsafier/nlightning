using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NBitcoin;

namespace NLightning.Infrastructure.Bitcoin.Wallet;

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
                PrevoutSource: _silentPaymentScanner.PrevoutSource);
            await repository.SetScanStateAsync(state, cancellationToken);
            await unitOfWork.SaveChangesAsync();
        }
        else if (_silentPaymentScanner.Enabled && state.LiveCursorHeight is { } cursor &&
                 cursor < _lastProcessedBlockHeight && cursor < tip)
        {
            // A disabled/offline SP gap is recovered by the SP-only rescan worker. The global cursor is unchanged.
            state = state with
            {
                LiveFromHeight = tip,
                RescanCursorHeight = state.RescanCursorHeight is { } rescan ? Math.Min(rescan, cursor) : cursor,
                RescanCursorHash = null,
                RescanTargetHeight = tip > 0 ? tip - 1 : 0
            };
            await repository.SetScanStateAsync(state, cancellationToken);
            await unitOfWork.SaveChangesAsync();
        }
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
        IUnitOfWork unitOfWork)
    {
        if (prepared is null || _silentPaymentScanner is null) return;
        await _silentPaymentScanner.StageSpendsAsync(prepared.Block, height, unitOfWork, prepared.Matches);
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
                    var coin = new UtxoModel(output with { SpentByTransactionId = null, SpentAtHeight = null });
                    if (!removed.Any(existing => existing.TxId == coin.TxId && existing.Index == coin.Index))
                        removed.Add(coin);
                    // A creation and spend both disconnected need both facts reversed; this coin is never restored.
                    if (output.SpentAtHeight is { } spentAbove && spentAbove > forkHeight &&
                        !restored.Any(row => row.Item1.TxId == coin.TxId && row.Item1.Index == coin.Index))
                        restored.Add((coin, spentAbove));
                }
                continue;
            }
            if (output.SpentAtHeight is not { } spentHeight || spentHeight <= forkHeight) continue;
            var unspentMetadata = output with { SpentByTransactionId = null, SpentAtHeight = null };
            await unitOfWork.SilentPaymentDbRepository.UpsertOutputAsync(unspentMetadata);
            if (output.Ignored) continue;
            var coin = new UtxoModel(unspentMetadata);
            // Reversing the confirmed spend is independent of whether a mempool spend makes it selectable.
            if (!restored.Any(row => row.Item1.TxId == coin.TxId && row.Item1.Index == coin.Index))
                restored.Add((coin, spentHeight));
            var memory = _serviceProvider.GetService<Domain.Bitcoin.Interfaces.IUtxoMemoryRepository>();
            if (memory?.TryGetUtxo(coin.TxId, coin.Index, out _) == true) continue;
            var outpoint = new OutPoint(new uint256((byte[])coin.TxId), coin.Index);
            var unspent = await _bitcoinChainService.GetUnspentOutputAsync(outpoint);
            if (unspent is not { } previous || previous.Output.Value.Satoshi != output.AmountSats ||
                !previous.Output.ScriptPubKey.ToBytes().AsSpan().SequenceEqual((byte[])[0x51, 0x20, .. output.OutputKey]))
                continue;
            unitOfWork.AddUtxo(coin);
        }
        await unitOfWork.SilentPaymentDbRepository.DeleteOutputsAboveHeightAsync(forkHeight);
        var state = await unitOfWork.SilentPaymentDbRepository.GetScanStateAsync();
        if (state is not null && (state.LiveCursorHeight > forkHeight || state.RescanCursorHeight > forkHeight))
            await unitOfWork.SilentPaymentDbRepository.SetScanStateAsync(state with
            {
                LiveCursorHeight = state.LiveCursorHeight > forkHeight ? forkHeight : state.LiveCursorHeight,
                LiveCursorHash = state.LiveCursorHeight > forkHeight ? null : state.LiveCursorHash,
                RescanCursorHeight = state.RescanCursorHeight > forkHeight ? forkHeight : state.RescanCursorHeight,
                RescanCursorHash = state.RescanCursorHeight > forkHeight ? null : state.RescanCursorHash
            });
    }

}