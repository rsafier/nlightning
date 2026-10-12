using System.Globalization;
using NBitcoin;

namespace NLightning.Infrastructure.Bitcoin.Wallet.SilentPayments;

using Domain.Accounting.Constants;
using Domain.Accounting.Enums;
using Domain.Accounting.Labels;
using Domain.Accounting.Models;
using Domain.Accounting.Services;
using Domain.Bitcoin.ValueObjects;
using Domain.Bitcoin.Wallet.Models;
using Domain.Persistence.Interfaces;

/// <summary>Shared immutable custody and transaction settlement facts for live scanning and historical recovery.</summary>
public static class SilentPaymentAccounting
{
    /// <summary>Settles an authorized collaborative broadcast from the custody legs actually recorded, including
    /// metadata-only inputs and newly accepted change. Foreign inputs and the shared fee remain unknown.</summary>
    public static async Task StageCollaborativeFlowsAsync(IUnitOfWork uow, Block block, uint height,
        TimeProvider time, CancellationToken cancellationToken)
    {
        foreach (var transaction in block.Transactions.Where(transaction => !transaction.IsCoinBase))
        {
            var transactionId = new TxId(transaction.GetHash().ToBytes());
            var broadcast = await uow.BroadcastTransactionDbRepository.GetByTransactionIdAsync(transactionId);
            if (broadcast?.Purpose != Domain.Onchain.Enums.BroadcastPurpose.WalletCollaborative) continue;
            long delta = 0;
            for (uint index = 0; index < transaction.Outputs.Count; index++)
            {
                var key = AccountingEventKeys.WalletReceived(transactionId, index);
                var facts = await uow.AccountingEventDbRepository.GetByKeyPrefixAsync(key, cancellationToken);
                var receipt = AccountingConfirmations.FindStanding(key, facts);
                if (receipt?.Details.GetValueOrDefault(AccountingDetailKeys.Source) is
                    AccountingDetailKeys.WalletSource or AccountingDetailKeys.BroadcastSource)
                    delta = checked(delta + receipt.AmountMsat);
            }
            foreach (var input in transaction.Inputs)
            {
                var key = AccountingEventKeys.WalletOutputSpent(new TxId(input.PrevOut.Hash.ToBytes()), input.PrevOut.N);
                var facts = await uow.AccountingEventDbRepository.GetByKeyPrefixAsync(key, cancellationToken);
                var spent = AccountingConfirmations.FindStanding(key, facts);
                if (spent is null || spent.Details.GetValueOrDefault("spentBy") != transactionId.ToString()) continue;
                if (spent.AmountMsat > 0)
                    throw new InvalidOperationException("Collaborative input custody must be a debit.");
                delta = checked(delta + spent.AmountMsat);
            }
            var baseKey = AccountingEventKeys.WalletSent(transactionId);
            var prior = await uow.AccountingEventDbRepository.GetByKeyPrefixAsync(baseKey, cancellationToken);
            var standing = AccountingConfirmations.FindStanding(baseKey, prior);
            if ((standing is null && delta == 0) || (standing is not null && standing.AmountMsat == delta && standing.FeeMsat == 0))
                continue;
            var confirmation = AccountingConfirmations.NextConfirmationKey(baseKey, prior);
            if (standing is not null)
            {
                var reversal = new AccountingEventModel
                {
                    EventKey = AccountingEventKeys.Reversal(standing.EventKey, standing.BlockHeight!.Value),
                    Kind = AccountingEventKind.Reversal,
                    OccurredAt = time.GetUtcNow(),
                    BlockHeight = standing.BlockHeight,
                    TxId = transactionId,
                    ChannelId = standing.ChannelId,
                    AmountMsat = -standing.AmountMsat,
                    FeeMsat = -standing.FeeMsat,
                    Finality = AccountingFinality.Confirmed,
                    Details = AccountingDetailsCodec.Create((AccountingConfirmations.ReversesDetail, standing.EventKey),
                        (AccountingConfirmations.OriginalKindDetail, standing.Kind.ToString()), ("reason", "collaborative_custody_changed"))
                };
                uow.AccountingEventDbRepository.Add(reversal);
                confirmation = AccountingConfirmations.NextConfirmationKey(baseKey, prior.Append(reversal).ToArray());
            }
            uow.AccountingEventDbRepository.Add(new AccountingEventModel
            {
                EventKey = confirmation!,
                Kind = AccountingEventKind.WalletSent,
                OccurredAt = block.Header.BlockTime,
                BlockHeight = height,
                TxId = transactionId,
                ChannelId = broadcast.ChannelId,
                AmountMsat = delta,
                Finality = AccountingFinality.Confirmed,
                Details = AccountingDetailsCodec.Create([
                    ("purpose", nameof(Domain.Onchain.Enums.BroadcastPurpose.WalletCollaborative)),
                    ("collaborative", "true"), ("feeUnknown", "true"),
                    .. SourceLabels.FromStored(broadcast.Label, broadcast.Tags).ToDetailPairs()
                ])
            });
        }
    }

    // Historical custody facts must also settle Clearing. Recover the complete transaction only when every
    // input belongs to the recovered wallet; shared transactions require their retained purpose/accounting context.
    public static async Task StageSettlementsAsync(IUnitOfWork uow, Block block, uint height,
        IReadOnlyList<SilentPaymentLabelModel> labels, Network network, TimeProvider time,
        CancellationToken cancellationToken, IReadOnlySet<TxId>? excludedTransactions = null,
        IReadOnlyList<WalletAddressModel>? recoveryCatalogue = null)
    {
        await StageCollaborativeFlowsAsync(uow, block, height, time, cancellationToken);
        var addresses = uow.WalletAddressesDbRepository.GetAllAddresses().Concat(recoveryCatalogue ?? [])
            .Select(address => address.Address).ToHashSet();
        foreach (var transaction in block.Transactions.Where(transaction => !transaction.IsCoinBase))
        {
            var transactionId = new TxId(transaction.GetHash().ToBytes());
            if (excludedTransactions?.Contains(transactionId) == true) continue;
            var inputs = new List<SilentPaymentOutputModel?>();
            foreach (var input in transaction.Inputs)
                inputs.Add(await uow.SilentPaymentDbRepository.GetOutputAsync(new TxId(input.PrevOut.Hash.ToBytes()),
                    input.PrevOut.N, cancellationToken));
            var recoveredOrdinary = false;
            foreach (var input in transaction.Inputs)
            {
                var receiptKey = AccountingEventKeys.WalletReceived(new TxId(input.PrevOut.Hash.ToBytes()), input.PrevOut.N);
                var receipts = await uow.AccountingEventDbRepository.GetByKeyPrefixAsync(receiptKey, cancellationToken);
                var receipt = AccountingConfirmations.FindStanding(receiptKey, receipts);
                if (receipt?.Details.GetValueOrDefault(WalletRecoveryAccounting.RecoveredCustody) == "true") recoveredOrdinary = true;
            }
            if (!recoveredOrdinary && !inputs.Any(input => input is { Ignored: false } && input.SpentByTransactionId == transactionId))
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
            // Finalization batches outputs, but financial settlement is transaction-wide. Stage all known input
            // custody debits in this same commit even when a spender crosses the batch boundary.
            foreach (var owned in inputs.OfType<SilentPaymentOutputModel>().Where(input => !input.Ignored))
            {
                if (owned.SpentByTransactionId is { } priorSpender && priorSpender != transactionId)
                    throw new InvalidOperationException("Recovered input has conflicting confirmed spend evidence.");
                await uow.SilentPaymentDbRepository.SetSpentAsync(owned.TransactionId, owned.Index, transactionId,
                    height, cancellationToken);
                await StageFactAsync(uow, owned, transactionId, block, height, labels, network, cancellationToken);
            }
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
                    Kind = AccountingEventKind.Reversal,
                    OccurredAt = time.GetUtcNow(),
                    BlockHeight = standing.BlockHeight,
                    TxId = transactionId,
                    AmountMsat = -standing.AmountMsat,
                    FeeMsat = -standing.FeeMsat,
                    Finality = AccountingFinality.Confirmed,
                    Details = AccountingDetailsCodec.Create((AccountingConfirmations.ReversesDetail, standing.EventKey),
                        (AccountingConfirmations.OriginalKindDetail, standing.Kind.ToString()), ("reason", "recovered_custody_policy_changed"))
                };
                uow.AccountingEventDbRepository.Add(reversal);
                key = AccountingConfirmations.NextConfirmationKey(baseKey, prior.Append(reversal).ToArray());
            }
            uow.AccountingEventDbRepository.Add(new AccountingEventModel
            {
                EventKey = key!,
                Kind = AccountingEventKind.WalletSent,
                OccurredAt = block.Header.BlockTime,
                BlockHeight = height,
                TxId = transactionId,
                AmountMsat = amountMsat,
                FeeMsat = feeMsat,
                Finality = AccountingFinality.Confirmed,
                Details = AccountingDetailsCodec.Create(("recovered", "true"), ("purposeUnknown", broadcast is null ? "true" : null),
                    ("externalOutputs", externalOutputs.ToString(CultureInfo.InvariantCulture)))
            });
        }
    }

    public static async Task StageFactAsync(IUnitOfWork uow, SilentPaymentOutputModel output, TxId? spender,
        Block block, uint height, IReadOnlyList<SilentPaymentLabelModel> labels, Network network, CancellationToken cancellationToken)
    {
        var baseKey = spender is null ? AccountingEventKeys.WalletReceived(output.TransactionId, output.Index)
            : AccountingEventKeys.WalletOutputSpent(output.TransactionId, output.Index);
        var prior = await uow.AccountingEventDbRepository.GetByKeyPrefixAsync(baseKey, cancellationToken);
        var key = AccountingConfirmations.NextConfirmationKey(baseKey, prior);
        if (key is null) return;
        var txid = spender ?? output.TransactionId;
        var transaction = block.Transactions.First(transaction => new TxId(transaction.GetHash().ToBytes()) == txid);
        var source = await GetSourceAsync(uow, transaction, cancellationToken);
        var name = output.Label is { } label ? labels.FirstOrDefault(item => item.M == label)?.Name : null;
        uow.AccountingEventDbRepository.Add(new AccountingEventModel
        {
            EventKey = key,
            Kind = spender is null ? AccountingEventKind.WalletReceived : AccountingEventKind.WalletOutputSpent,
            OccurredAt = block.Header.BlockTime,
            BlockHeight = height,
            TxId = output.TransactionId,
            OutputIndex = output.Index,
            AmountMsat = checked(output.AmountSats * 1000) * (spender is null ? 1 : -1),
            Finality = AccountingFinality.Confirmed,
            Details = AccountingDetailsCodec.Create((AccountingDetailKeys.Source, source), ("addressType", "P2Tr"),
                ("address", new Script(new byte[] { 0x51, 0x20 }.Concat(output.OutputKey).ToArray())
                    .GetDestinationAddress(network)?.ToString()),
                ("receiptSource", "silent_payment"), ("silentPayment", "true"), (AccountingDetailKeys.Label, name),
                ("silentPaymentLabel", output.Label?.ToString(CultureInfo.InvariantCulture)), ("spentBy", spender?.ToString()))
        });
    }

    public static async Task<string> GetSourceAsync(IUnitOfWork uow, Transaction transaction, CancellationToken cancellationToken)
    {
        if (await uow.BroadcastTransactionDbRepository.GetByTransactionIdAsync(new TxId(transaction.GetHash().ToBytes())) is not null)
            return AccountingDetailKeys.BroadcastSource;
        foreach (var input in transaction.Inputs)
        {
            var point = new TxId(input.PrevOut.Hash.ToBytes());
            if (await uow.SilentPaymentDbRepository.GetOutputAsync(point, input.PrevOut.N, cancellationToken) is { Ignored: false } ||
                await uow.UtxoDbRepository.GetByIdAsync(point, input.PrevOut.N) is not null)
                return AccountingDetailKeys.WalletSource;
            var key = AccountingEventKeys.WalletReceived(point, input.PrevOut.N);
            var facts = await uow.AccountingEventDbRepository.GetByKeyPrefixAsync(key, cancellationToken);
            if (AccountingConfirmations.FindStanding(key, facts) is not null) return AccountingDetailKeys.WalletSource;
        }
        return AccountingDetailKeys.ExternalSource;
    }

}