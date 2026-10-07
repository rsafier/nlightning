using Microsoft.Extensions.DependencyInjection;
using NBitcoin;

namespace NLightning.Infrastructure.Bitcoin.Wallet;

using Domain.Bitcoin.ValueObjects;
using Domain.Bitcoin.Wallet.Constants;
using Domain.Bitcoin.Wallet.Models;
using Domain.Exceptions;
using Domain.Money;
using Domain.Onchain.Enums;
using Domain.Onchain.Models;
using Domain.Persistence.Interfaces;
using Models;

public sealed partial class WalletPsbtService
{
    private readonly SemaphoreSlim _cpfpGate = new(1, 1);

    public async Task<TxId> BumpOutputAsync(TxId txId, uint index, long feeRatePerKw, long? budgetSat = null,
                                          CancellationToken cancellationToken = default)
    {
        await _cpfpGate.WaitAsync(cancellationToken);
        try { return await BumpOutputCoreAsync(txId, index, feeRatePerKw, budgetSat, cancellationToken); }
        finally { _cpfpGate.Release(); }
    }

    private async Task<TxId> BumpOutputCoreAsync(TxId txId, uint index, long feeRatePerKw, long? budgetSat,
                                               CancellationToken cancellationToken)
    {
        if (_bitcoinChainService is null || _mempoolCatalog is null)
            throw new WalletPsbtException(WalletPsbtError.FailedPrecondition, "wallet CPFP requires a mempool catalogue and Core");
        if (feeRatePerKw is < WalletSpendService.MinFeeRatePerKw or > WalletSpendService.MaxFeeRatePerKw || budgetSat is <= 0)
            throw new WalletPsbtException(WalletPsbtError.InvalidArgument, "invalid CPFP fee rate or budget");
        await _mempoolCatalog.RefreshParentsAsync([txId], cancellationToken);
        if (!_utxoMemoryRepository.TryGetUtxo(txId, index, out var coin))
            throw new WalletPsbtException(WalletPsbtError.NotFound, "the outpoint is not a live wallet mempool output");
        if (coin.BlockHeight != 0)
            throw new WalletPsbtException(WalletPsbtError.FailedPrecondition, "confirmed wallet outputs do not need CPFP");
        var parent = await _bitcoinChainService.GetMempoolEntryAsync(new uint256((byte[])txId))
            ?? throw new WalletPsbtException(WalletPsbtError.FailedPrecondition, "CPFP parent left the mempool");
        using (var scope = _scopeFactory.CreateScope())
        {
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            foreach (var pending in await uow.BroadcastTransactionDbRepository.GetPendingAsync())
            {
                if (pending.Purpose != BroadcastPurpose.WalletSend || pending.Label != "wallet CPFP") continue;
                Transaction previous;
                try { previous = Transaction.Load(pending.RawTransaction, _network); }
                catch (FormatException) { continue; }
                if (previous.Inputs.Count == 1 && previous.Outputs.Count == 1 &&
                    previous.Inputs[0].PrevOut == new OutPoint(new uint256((byte[])txId), index))
                    return await ReplaceWalletCpfpAsync(coin, pending, previous, parent, feeRatePerKw, budgetSat, cancellationToken);
            }
        }
        var script = await NewChangeScriptAsync(coin.AddressType, coin.WalletAddress?.AccountName ?? "default");
        var weight = checked(WalletSpendService.BaseWeight + WalletWeights.GetInputWeight(coin.AddressType)
                             + WalletSpendService.GetOutputWeight(script));
        var rate = Math.Max(feeRatePerKw, await _bitcoinChainService.GetMempoolMinFeeRatePerKwAsync() ?? 0);
        var fee = CpfpFee(parent, weight, rate);
        if (budgetSat is { } budget && fee > budget)
            throw new WalletPsbtException(WalletPsbtError.FailedPrecondition, "CPFP ancestor-package fee exceeds budget");
        var amount = checked(coin.Amount.Satoshi - fee);
        if (amount < WalletSpendService.GetDustThreshold(script))
            throw new WalletPsbtException(WalletPsbtError.FailedPrecondition, "CPFP output cannot pay package fee without dust");
        await EnsureReserveKeptAsync(0, cancellationToken);
        await LeaseAsync(DefaultLockId, txId, index, DefaultLeaseDuration, cancellationToken);
        var intentCommitted = false;
        try
        {
            var child = _network.CreateTransaction();
            child.Version = 2;
            child.Inputs.Add(new TxIn(new OutPoint(new uint256((byte[])txId), index)) { Sequence = new Sequence(0xFFFFFFFD) });
            child.Outputs.Add(Money.Satoshis(amount), script);
            var psbt = PSBT.FromTransaction(child, _network);
            psbt.Inputs[0].WitnessUtxo = new TxOut(Money.Satoshis(coin.Amount.Satoshi), WalletUtxoScript(coin));
            AddKeyInfo(psbt.Inputs[0], coin);
            var finalized = await FinalizePsbtAsync(psbt.ToBytes(), cancellationToken);
            var tx = Transaction.Load(finalized.RawFinalTx, _network);
            if (tx.Inputs.Count != 1 || tx.Inputs[0].PrevOut != new OutPoint(new uint256((byte[])txId), index))
                throw new WalletPsbtException(WalletPsbtError.FailedPrecondition, "CPFP input changed after package pricing");
            var actualFee = coin.Amount.Satoshi - tx.Outputs.Sum(output => output.Value.Satoshi);
            var livePackage = await _bitcoinChainService.GetMempoolEntryAsync(new uint256((byte[])txId))
                ?? throw new WalletPsbtException(WalletPsbtError.FailedPrecondition, "CPFP parent left the mempool before publication");
            if (actualFee < CpfpFee(livePackage, WalletSpendService.GetWeight(tx), rate) ||
                (budgetSat is { } limit && actualFee > limit))
                throw new WalletPsbtException(WalletPsbtError.FailedPrecondition, "final CPFP transaction does not satisfy package target/budget");
            var row = new BroadcastTransactionModel(new SignedTransaction(new TxId(tx.GetHash().ToBytes()), tx.ToBytes()),
                BroadcastPurpose.WalletSend, null, _blockchainMonitor.LastProcessedBlockHeight,
                checked((uint)(actualFee * 1000 / WalletSpendService.GetWeight(tx))),
                fee: LightningMoney.Satoshis(actualFee))
            { Label = "wallet CPFP" };
            using (var scope = _scopeFactory.CreateScope())
            {
                var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
                uow.BroadcastTransactionDbRepository.Add(row);
                await uow.SaveChangesAsync();
                intentCommitted = true;
            }
            // Retained intent and lease let the monitor safely retry after an ambiguous send or restart.
            await _blockchainMonitor.PublishAsync(row);
            return row.TransactionId;
        }
        catch
        {
            // Release only before durable intent exists. After commit the monitor owns retry/reconciliation.
            if (!intentCommitted) await ReleaseAsync(DefaultLockId, txId, index, CancellationToken.None);
            throw;
        }
    }

    private async Task<TxId> ReplaceWalletCpfpAsync(UtxoModel coin, BroadcastTransactionModel previousRow,
        Transaction previous, WalletMempoolEntry package, long requestedRate, long? budget, CancellationToken ct)
    {
        var parentOutpoint = previous.Inputs[0].PrevOut;
        var spender = await _bitcoinChainService!.GetMempoolSpendersAsync([parentOutpoint]);
        if (spender is null || !spender.TryGetValue(parentOutpoint, out var live) || live != previous.GetHash())
            throw new WalletPsbtException(WalletPsbtError.FailedPrecondition, "the retained CPFP child is not the active mempool spender");
        // Preserve the original recipient/change script and account. Never turn a caller's payment into a CPFP.
        using (var scope = _scopeFactory.CreateScope())
        {
            var addresses = scope.ServiceProvider.GetRequiredService<IUnitOfWork>().WalletAddressesDbRepository.GetAllAddresses();
            var owner = addresses.FirstOrDefault(address => BitcoinAddress.Create(address.Address, _network).ScriptPubKey == previous.Outputs[0].ScriptPubKey);
            if (owner is null || owner.AccountName != (coin.WalletAddress?.AccountName ?? "default"))
                throw new WalletPsbtException(WalletPsbtError.FailedPrecondition, "CPFP change is not owned by the parent account");
        }
        var weight = checked(WalletSpendService.BaseWeight + WalletWeights.GetInputWeight(coin.AddressType)
            + WalletSpendService.GetOutputWeight(previous.Outputs[0].ScriptPubKey));
        var rate = Math.Max(requestedRate, await _bitcoinChainService.GetMempoolMinFeeRatePerKwAsync() ?? 0);
        var incremental = await _bitcoinChainService.GetIncrementalRelayFeeRatePerKwAsync();
        var oldFee = coin.Amount.Satoshi - previous.Outputs[0].Value.Satoshi;
        var replacementFee = Math.Max(CpfpFee(package, weight, rate), checked(oldFee + Math.Max(1, FeeSat(incremental, weight))));
        if (budget is { } max && replacementFee > max)
            throw new WalletPsbtException(WalletPsbtError.FailedPrecondition, "CPFP replacement package fee exceeds budget");
        var amount = checked(coin.Amount.Satoshi - replacementFee);
        if (amount < WalletSpendService.GetDustThreshold(previous.Outputs[0].ScriptPubKey))
            throw new WalletPsbtException(WalletPsbtError.FailedPrecondition, "CPFP replacement would make change dust");
        var replacement = previous.Clone();
        replacement.Inputs[0].WitScript = WitScript.Empty;
        replacement.Outputs[0].Value = Money.Satoshis(amount);
        var psbt = PSBT.FromTransaction(replacement, _network);
        psbt.Inputs[0].WitnessUtxo = new TxOut(Money.Satoshis(coin.Amount.Satoshi), WalletUtxoScript(coin));
        AddKeyInfo(psbt.Inputs[0], coin);
        var final = await FinalizePsbtAsync(psbt.ToBytes(), ct);
        var signed = Transaction.Load(final.RawFinalTx, _network);
        var fee = coin.Amount.Satoshi - signed.Outputs[0].Value.Satoshi;
        var livePackage = await _bitcoinChainService.GetMempoolEntryAsync(parentOutpoint.Hash)
            ?? throw new WalletPsbtException(WalletPsbtError.FailedPrecondition, "parent left mempool before replacement");
        if (signed.Inputs.Count != 1 || signed.Inputs[0].PrevOut != parentOutpoint || signed.Outputs.Count != 1 ||
            signed.Outputs[0].ScriptPubKey != previous.Outputs[0].ScriptPubKey ||
            fee < CpfpFee(livePackage, WalletSpendService.GetWeight(signed), rate) ||
            fee < oldFee + Math.Max(1, FeeSat(incremental, WalletSpendService.GetWeight(signed))))
            throw new WalletPsbtException(WalletPsbtError.FailedPrecondition, "CPFP replacement changed ownership or failed package/relay target");
        var row = new BroadcastTransactionModel(new SignedTransaction(new TxId(signed.GetHash().ToBytes()), signed.ToBytes()),
            BroadcastPurpose.WalletSend, null, _blockchainMonitor.LastProcessedBlockHeight,
            checked((uint)(fee * 1000 / WalletSpendService.GetWeight(signed))), previousRow.TransactionId,
            fee: LightningMoney.Satoshis(fee))
        { Label = "wallet CPFP" };
        using (var scope = _scopeFactory.CreateScope())
        {
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            uow.BroadcastTransactionDbRepository.Add(row);
            await uow.BroadcastTransactionDbRepository.MarkReplacedAsync(previousRow.TransactionId);
            await uow.SaveChangesAsync();
        }
        // Durable replacement intent precedes send; the monitor retries it and follows its RBF lineage after restart/reorg.
        await _blockchainMonitor.PublishAsync(row);
        return row.TransactionId;
    }

    internal static long CpfpFee(WalletMempoolEntry package, int childWeight, long ratePerKw)
    {
        if (package.AncestorVirtualSize <= 0 || package.AncestorFeeSat < 0 || childWeight <= 0 || ratePerKw <= 0)
            throw new ArgumentOutOfRangeException(nameof(package));
        var target = checked((checked(ratePerKw * checked(package.AncestorVirtualSize * 4 + childWeight)) + 999) / 1000);
        var childMinimum = checked((checked(ratePerKw * childWeight) + 999) / 1000);
        return Math.Max(childMinimum, checked(target - package.AncestorFeeSat));
    }
}