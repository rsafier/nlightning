using System.Globalization;
using NBitcoin;

namespace NLightning.Infrastructure.Bitcoin.Wallet.SilentPayments;

using Domain.Accounting.Constants;
using Domain.Accounting.Enums;
using Domain.Accounting.Models;
using Domain.Accounting.Services;
using Domain.Bitcoin.ValueObjects;
using Domain.Bitcoin.Wallet.Models;
using Domain.Money;
using Domain.Persistence.Interfaces;

/// <summary>Journal-backed ordinary wallet custody discovered by a bounded key-derived recovery catalogue.</summary>
public static class WalletRecoveryAccounting
{
    public const string RecoveredCustody = "recoveredCustody";

    public static async Task StageBlockAsync(IUnitOfWork uow, Block block, uint height,
        IReadOnlyList<WalletAddressModel> catalogue, Network network, CancellationToken cancellationToken)
    {
        var addresses = catalogue.ToDictionary(address => address.Address);
        foreach (var transaction in block.Transactions.Where(transaction => !transaction.IsCoinBase))
        {
            var transactionId = new TxId(transaction.GetHash().ToBytes());
            var source = await SilentPaymentAccounting.GetSourceAsync(uow, transaction, cancellationToken);
            for (uint index = 0; index < transaction.Outputs.Count; index++)
            {
                var output = transaction.Outputs[(int)index];
                var address = output.ScriptPubKey.GetDestinationAddress(network)?.ToString();
                if (address is null || !addresses.TryGetValue(address, out var owned)) continue;
                await uow.WalletAddressesDbRepository.ReserveAsync(owned);
                var baseKey = AccountingEventKeys.WalletReceived(transactionId, index);
                var existing = await uow.AccountingEventDbRepository.GetByKeyPrefixAsync(baseKey, cancellationToken);
                var key = AccountingConfirmations.NextConfirmationKey(baseKey, existing);
                if (key is null) continue;
                uow.AccountingEventDbRepository.Add(new AccountingEventModel
                {
                    EventKey = key,
                    Kind = AccountingEventKind.WalletReceived,
                    OccurredAt = block.Header.BlockTime,
                    BlockHeight = height,
                    TxId = transactionId,
                    OutputIndex = index,
                    AmountMsat = checked(output.Value.Satoshi * 1000),
                    Finality = AccountingFinality.Confirmed,
                    Details = AccountingDetailsCodec.Create((RecoveredCustody, "true"), ("receiptSource", "wallet_recovery"),
                        ("address", owned.Address), ("addressType", Enum.GetName(owned.AddressType)),
                        ("recoveryAddressIndex", owned.Index.ToString(CultureInfo.InvariantCulture)),
                        ("change", owned.IsChange ? "true" : "false"), (AccountingDetailKeys.Source, source))
                });
            }
        }
        await StageInputSpendsAsync(uow, block, height, cancellationToken);
    }

    /// <summary>Retains raw historical wallet transactions and complete proven ownership in the block's own save.</summary>
    public static async Task StageWalletHistoryAsync(IUnitOfWork uow, Block block, uint height,
        CancellationToken cancellationToken)
    {
        if (uow.WalletTransactionDbRepository is not { } history) return;
        foreach (var transaction in block.Transactions.Where(transaction => !transaction.IsCoinBase))
        {
            var transactionId = new TxId(transaction.GetHash().ToBytes());
            var outputs = new List<uint>();
            var inputs = new List<WalletTransactionInput>();
            for (uint index = 0; index < transaction.Outputs.Count; index++)
            {
                var amount = await OwnedAmountAsync(transactionId, index);
                if (amount is null) continue;
                if (amount != transaction.Outputs[(int)index].Value.Satoshi)
                    throw new InvalidOperationException("Wallet history output differs from its custody evidence.");
                outputs.Add(index);
            }
            for (uint index = 0; index < transaction.Inputs.Count; index++)
            {
                var point = transaction.Inputs[(int)index].PrevOut;
                if (await OwnedAmountAsync(new TxId(point.Hash.ToBytes()), point.N) is { } amount)
                    inputs.Add(new WalletTransactionInput(index, amount));
            }
            if (outputs.Count == 0 && inputs.Count == 0) continue;
            // Raw bytes retain every input outpoint. The repository unions ownership with the existing description,
            // including ordinary/imported wallet observations and transactions a rollback made unconfirmed.
            await history.StageConfirmedAsync(new WalletTransactionRecord(transactionId, transaction.ToBytes(), height,
                block.GetHash().ToBytes(), block.Header.BlockTime, outputs, inputs));
        }

        async Task<long?> OwnedAmountAsync(TxId transactionId, uint index)
        {
            var output = await uow.SilentPaymentDbRepository.GetOutputAsync(transactionId, index, cancellationToken);
            if (output is not null) return output.Ignored ? null : output.AmountSats;
            var key = AccountingEventKeys.WalletReceived(transactionId, index);
            var facts = await uow.AccountingEventDbRepository.GetByKeyPrefixAsync(key, cancellationToken);
            var received = AccountingConfirmations.FindStanding(key, facts);
            if (received is null) return null;
            if (received.AmountMsat < 0 || received.AmountMsat % 1000 != 0)
                throw new InvalidOperationException("Wallet history custody evidence is not a nonnegative satoshi amount.");
            return received.AmountMsat / 1000;
        }
    }

    public static async Task StageInputSpendsAsync(IUnitOfWork uow, Block block, uint height,
        CancellationToken cancellationToken)
    {
        foreach (var transaction in block.Transactions.Where(transaction => !transaction.IsCoinBase))
        {
            foreach (var input in transaction.Inputs)
            {
                var point = new TxId(input.PrevOut.Hash.ToBytes());
                var receiptKey = AccountingEventKeys.WalletReceived(point, input.PrevOut.N);
                var receipts = await uow.AccountingEventDbRepository.GetByKeyPrefixAsync(receiptKey, cancellationToken);
                var received = AccountingConfirmations.FindStanding(receiptKey, receipts);
                if (received is null || !received.Details.TryGetValue(RecoveredCustody, out var recovered) || recovered != "true") continue;
                await StageSpendAsync(uow, received, transaction, height, block.Header.BlockTime, cancellationToken);
            }
        }
    }

    public static async Task StageSpendAsync(IUnitOfWork uow, AccountingEventModel receipt, Transaction spender,
        uint height, DateTimeOffset occurredAt, CancellationToken cancellationToken)
    {
        var baseKey = AccountingEventKeys.WalletOutputSpent(receipt.TxId!.Value, receipt.OutputIndex!.Value);
        var existing = await uow.AccountingEventDbRepository.GetByKeyPrefixAsync(baseKey, cancellationToken);
        var key = AccountingConfirmations.NextConfirmationKey(baseKey, existing);
        if (key is null) return;
        uow.TrySpendUtxo(receipt.TxId.Value, receipt.OutputIndex.Value);
        uow.AccountingEventDbRepository.Add(new AccountingEventModel
        {
            EventKey = key,
            Kind = AccountingEventKind.WalletOutputSpent,
            OccurredAt = occurredAt,
            BlockHeight = height,
            TxId = receipt.TxId,
            OutputIndex = receipt.OutputIndex,
            AmountMsat = -receipt.AmountMsat,
            Finality = AccountingFinality.Confirmed,
            Details = AccountingDetailsCodec.Create((RecoveredCustody, "true"), ("address", receipt.Details.GetValueOrDefault("address")),
                ("addressType", receipt.Details.GetValueOrDefault("addressType")),
                ("spentBy", new TxId(spender.GetHash().ToBytes()).ToString()), (AccountingDetailKeys.Source, AccountingDetailKeys.WalletSource))
        });
    }

    public static async Task<IReadOnlyList<(AccountingEventModel Receipt, UtxoModel Coin)>> GetUnspentAsync(
        IUnitOfWork uow, CancellationToken cancellationToken, IReadOnlyList<WalletAddressModel>? catalogue = null)
    {
        var facts = await uow.AccountingEventDbRepository.GetByKeyPrefixAsync("wallet:", cancellationToken);
        var addresses = uow.WalletAddressesDbRepository.GetAllAddresses().Concat(catalogue ?? [])
            .GroupBy(address => address.Address).ToDictionary(group => group.Key, group => group.First());
        var result = new List<(AccountingEventModel, UtxoModel)>();
        foreach (var point in facts.Where(fact => fact.Kind == AccountingEventKind.WalletReceived && fact.TxId is not null && fact.OutputIndex is not null)
                     .Select(fact => (fact.TxId!.Value, fact.OutputIndex!.Value)).Distinct())
        {
            var receiptKey = AccountingEventKeys.WalletReceived(point.Item1, point.Item2);
            var receipt = AccountingConfirmations.FindStanding(receiptKey, facts.Where(fact => fact.EventKey.StartsWith(receiptKey, StringComparison.Ordinal)).ToArray());
            if (receipt is null || !receipt.Details.TryGetValue(RecoveredCustody, out var recovered) || recovered != "true") continue;
            var spentKey = AccountingEventKeys.WalletOutputSpent(point.Item1, point.Item2);
            if (AccountingConfirmations.FindStanding(spentKey, facts.Where(fact => fact.EventKey.StartsWith(spentKey, StringComparison.Ordinal)).ToArray()) is not null) continue;
            if (!receipt.Details.TryGetValue("address", out var address) || !addresses.TryGetValue(address, out var owned))
                throw new InvalidOperationException("Recovered ordinary custody has no key-derived address catalogue entry.");
            result.Add((receipt, new UtxoModel(point.Item1, point.Item2, LightningMoney.Satoshis(receipt.AmountMsat / 1000),
                receipt.BlockHeight!.Value, owned)));
        }
        return result;
    }
}