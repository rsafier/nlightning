using System.Globalization;
using Microsoft.Extensions.Logging;
using NBitcoin;

namespace NLightning.Infrastructure.Bitcoin.Wallet;

using Domain.Accounting.Constants;
using Domain.Accounting.Enums;
using Domain.Accounting.Interfaces;
using Domain.Accounting.Labels;
using Domain.Accounting.Models;
using Domain.Accounting.Services;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.ValueObjects;
using Domain.Bitcoin.Wallet.Models;
using Domain.Channels.ValueObjects;
using Domain.Money;
using Domain.Onchain.Enums;
using Domain.Onchain.Models;
using Domain.Persistence.Interfaces;

/// <summary>
/// The chain monitor's writers of the accounting feed (NL-602, plan <c>docs/agents/ACCOUNTING_PLAN.md</c> §4; wallet
/// history NL-603), all staged in the unit of work of the block (or of the rewind) that commits the fact.
/// </summary>
/// <remarks>
/// <para>Per block: a <see cref="AccountingEventKind.WalletReceived"/> for every wallet output the block deposits and a
/// <see cref="AccountingEventKind.WalletOutputSpent"/> for every wallet output it spends (both at the moment the UTXO
/// table changes, so the wallet bucket of the books always equals the UTXO set; <c>source</c> tells an external deposit
/// from change, a close or a sweep), and for every stored broadcast the block confirms
/// <see cref="AccountingEventKind.AnchorCpfpFee"/> (anchor CPFP child), <see cref="AccountingEventKind.SweepFeeBump"/>
/// (a sweep, HTLC claim or penalty that replaced another) or <see cref="AccountingEventKind.WalletSent"/>
/// (<c>withdraw</c>). The guards are the existing ones: a deposit is recorded only when the UTXO is not known yet, a
/// spend only when the UTXO is held, a confirmation only when the stored row was still pending; on top of that a fact
/// whose confirmation is recorded and not reversed is never recorded again
/// (<see cref="AccountingConfirmations.NextConfirmationKey"/>).</para>
/// <para>Per rewind (same save as the rollback): the events of the facts the rollback undid are reversed with a
/// <see cref="AccountingEventKind.Reversal"/>: every broadcast confirmation above the fork (the rows are pending
/// again), the deposits the rollback removed, the spends of the outputs it restored, and a deposit and its spend that
/// both sat above the fork. A spend above the fork of an output deposited at or below it stays (the UTXO stays spent:
/// its spend is back in the mempool, NL-293). A fact that confirms again later is recorded under its next
/// confirmation key.</para>
/// <para>Nothing here may fail the block: every step catches and logs its own errors.</para>
/// </remarks>
public partial class BlockchainMonitorService
{
    /// <summary>The kinds this monitor writes and reverses (plus the reversals themselves).</summary>
    private static readonly AccountingEventKind[] s_reorgReversibleKinds =
    [
        AccountingEventKind.WalletReceived, AccountingEventKind.WalletOutputSpent, AccountingEventKind.AnchorCpfpFee,
        AccountingEventKind.SweepFeeBump, AccountingEventKind.WalletSent, AccountingEventKind.Reversal
    ];

    /// <summary>How many replaced rows are followed back to a sweep's original (a guard against a cycle).</summary>
    private const int MaxReplacementChainLength = 64;

    /// <summary>The source of a wallet movement whose transaction we neither broadcast nor watch nor signed.</summary>
    internal const string ExternalSource = AccountingDetailKeys.ExternalSource;

    /// <summary>The source of a wallet movement whose transaction is one of our stored broadcasts.</summary>
    internal const string BroadcastSource = AccountingDetailKeys.BroadcastSource;

    /// <summary>The source of a wallet movement whose transaction is a watched channel transaction or spends a watched
    /// channel output (a mutual close, a sweep or claim without a stored row).</summary>
    internal const string ChannelSource = AccountingDetailKeys.ChannelSource;

    /// <summary>The source of a wallet movement whose transaction spends our wallet outputs but is not a stored
    /// broadcast (an interactive transaction the peer published, a transaction from before the broadcast table).
    /// </summary>
    internal const string WalletSource = AccountingDetailKeys.WalletSource;

    /// <summary>The stored row of a pending broadcast before the block marks it confirmed; null when unreadable.</summary>
    private async Task<BroadcastTransactionModel?> TryGetBroadcastForAccountingAsync(IUnitOfWork uow, TxId txId)
    {
        try
        {
            return await uow.BroadcastTransactionDbRepository.GetByTransactionIdAsync(txId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Accounting: cannot read broadcast {TxId}; its confirmation is not recorded", txId);
            return null;
        }
    }

    /// <summary>
    /// Collects the accounting event of a stored broadcast that a block confirms (it was still pending): the fee of an
    /// anchor CPFP child, the extra fee of a bumped sweep, a withdrawal.
    /// </summary>
    private async Task CollectBroadcastConfirmedAsync(IUnitOfWork uow, BroadcastTransactionModel stored,
                                                      Transaction transaction, BlockEffects effects)
    {
        try
        {
            switch (stored.Purpose)
            {
                case BroadcastPurpose.AnchorCpfp:
                    CollectAnchorCpfpFee(stored, effects);
                    break;
                case BroadcastPurpose.Sweep or BroadcastPurpose.HtlcClaim or BroadcastPurpose.Penalty
                    when stored.ReplacesTransactionId is { } replaced:
                    await CollectSweepFeeBumpAsync(uow, stored, replaced, effects);
                    break;
                case BroadcastPurpose.WalletSend:
                    CollectWalletSent(stored, transaction, effects);
                    break;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Accounting: cannot record the confirmation of {Purpose} {TxId}",
                             Enum.GetName(stored.Purpose), stored.TransactionId);
        }
    }

    private void CollectAnchorCpfpFee(BroadcastTransactionModel stored, BlockEffects effects)
    {
        var details = AccountingDetailsCodec.Create(("purpose", nameof(BroadcastPurpose.AnchorCpfp)),
                                                    ("replaces", stored.ReplacesTransactionId?.ToString()),
                                                    ("feeUnknown", stored.Fee is null ? "true" : null));
        var feeMsat = ToMsat(stored.Fee);
        effects.Accounting.Add(new AccountingCandidate(
                                   AccountingEventKeys.AnchorCpfpFee(stored.TransactionId),
                                   key => NewOnchainEvent(key, AccountingEventKind.AnchorCpfpFee, effects.Height,
                                                          stored.TransactionId, null, stored.ChannelId, 0, feeMsat,
                                                          details)));
    }

    /// <summary>
    /// The extra fee a confirmed sweep replacement paid: its fee minus the fee of the first transaction of its
    /// replacement chain (what the sweep would have cost without the bumps; the replaced attempts never confirmed).
    /// </summary>
    private async Task CollectSweepFeeBumpAsync(IUnitOfWork uow, BroadcastTransactionModel stored, TxId replaced,
                                                BlockEffects effects)
    {
        var original = await uow.BroadcastTransactionDbRepository.GetByTransactionIdAsync(replaced);
        for (var steps = 0;
             original?.ReplacesTransactionId is { } earlier && steps < MaxReplacementChainLength;
             steps++)
            original = await uow.BroadcastTransactionDbRepository.GetByTransactionIdAsync(earlier) ?? original;

        long feeMsat = 0;
        var known = stored.Fee is not null && original?.Fee is not null;
        if (known)
            feeMsat = Math.Max(0, ToMsat(stored.Fee) - ToMsat(original!.Fee));

        var details = AccountingDetailsCodec.Create(("purpose", Enum.GetName(stored.Purpose)),
                                                    ("replaces", replaced.ToString()),
                                                    ("originalTxId", original?.TransactionId.ToString()),
                                                    ("feeSat", FormatSat(stored.Fee)),
                                                    ("originalFeeSat", FormatSat(original?.Fee)),
                                                    ("feeUnknown", known ? null : "true"));
        effects.Accounting.Add(new AccountingCandidate(
                                   AccountingEventKeys.SweepFeeBump(stored.TransactionId),
                                   key => NewOnchainEvent(key, AccountingEventKind.SweepFeeBump, effects.Height,
                                                          stored.TransactionId, null, stored.ChannelId, 0, feeMsat,
                                                          details)));
    }

    /// <summary>A withdrawal: what left to outputs that are not ours (the change comes back as a deposit).</summary>
    private void CollectWalletSent(BroadcastTransactionModel stored, Transaction transaction, BlockEffects effects)
    {
        long sentSat = 0;
        var externalOutputs = 0;
        string? destination = null;
        foreach (var output in transaction.Outputs)
        {
            var address = output.ScriptPubKey.GetDestinationAddress(_network)?.ToString();
            if (address is not null && _watchedAddresses.ContainsKey(address))
                continue;

            sentSat += output.Value.Satoshi;
            externalOutputs++;
            destination = address;
        }

        var details = AccountingDetailsCodec.Create(
        [
            ("purpose", nameof(BroadcastPurpose.WalletSend)),
            (AccountingDetailKeys.Destination, externalOutputs == 1 ? destination : null),
            ("externalOutputs", externalOutputs.ToString(CultureInfo.InvariantCulture)),
            ("feeUnknown", stored.Fee is null ? "true" : null),
            // NL-602 A3-T1: the operator's label and tags of the withdrawal
            .. SourceLabels.FromStored(stored.Label, stored.Tags).ToDetailPairs()
        ]);
        var feeMsat = ToMsat(stored.Fee);
        effects.Accounting.Add(new AccountingCandidate(
                                   AccountingEventKeys.WalletSent(stored.TransactionId),
                                   key => NewOnchainEvent(key, AccountingEventKind.WalletSent, effects.Height,
                                                          stored.TransactionId, null, stored.ChannelId,
                                                          -checked(sentSat * 1_000), feeMsat, details)));
    }

    /// <summary>Where a transaction that moves wallet funds comes from (the <c>source</c> detail).</summary>
    private WalletTransactionSource ClassifyWalletTransaction(Transaction transaction,
                                                             IUtxoMemoryRepository? utxoMemoryRepository,
                                                             BlockEffects effects)
    {
        var txId = transaction.GetHash();
        if (_pendingBroadcasts.TryGetValue(txId, out var broadcast)
         || effects.ConfirmedReplacedMembers.TryGetValue(txId, out broadcast))
            return new WalletTransactionSource(BroadcastSource, broadcast.Purpose, broadcast.ChannelId);

        if (_watchedTransactions.TryGetValue(txId, out var watched))
            return new WalletTransactionSource(ChannelSource, null, watched.ChannelId);

        foreach (var input in transaction.Inputs)
        {
            if (_watchedOutpoints.TryGetValue(input.PrevOut, out var channelId))
                return new WalletTransactionSource(ChannelSource, null, channelId);

            var added = effects.NewOutpoints.FirstOrDefault(o => o.OutputIndex == input.PrevOut.N
                                                              && new uint256(o.TransactionId) == input.PrevOut.Hash);
            if (added is not null)
                return new WalletTransactionSource(ChannelSource, null, added.ChannelId);
        }

        foreach (var input in transaction.Inputs)
        {
            if (effects.StagedDeposits.ContainsKey(input.PrevOut)
             || utxoMemoryRepository?.TryGetUtxo(new TxId(input.PrevOut.Hash.ToBytes()), input.PrevOut.N, out _)
             == true)
                return new WalletTransactionSource(WalletSource, null, null);
        }

        return new WalletTransactionSource(ExternalSource, null, null);
    }

    private void CollectWalletReceived(UtxoModel utxo, WalletAddressModel address, WalletTransactionSource source,
                                       BlockEffects effects)
    {
        var details = AccountingDetailsCodec.Create(("address", address.Address),
                                                    ("addressType", Enum.GetName(address.AddressType)),
                                                    ("change", address.IsChange ? "true" : "false"),
                                                    (AccountingDetailKeys.Source, source.Source),
                                                    (AccountingDetailKeys.Purpose,
                                                     source.Purpose is { } purpose ? Enum.GetName(purpose) : null));
        var amountMsat = checked((long)utxo.Amount.MilliSatoshi);
        effects.Accounting.Add(new AccountingCandidate(
                                   AccountingEventKeys.WalletReceived(utxo.TxId, utxo.Index),
                                   key => NewOnchainEvent(key, AccountingEventKind.WalletReceived, effects.Height,
                                                          utxo.TxId, utxo.Index, source.ChannelId, amountMsat, 0,
                                                          details)));
    }

    private void CollectWalletOutputSpent(UtxoModel spent, Transaction spender, WalletTransactionSource source,
                                          IUtxoMemoryRepository? utxoMemoryRepository, BlockEffects effects)
    {
        string? reservation = null;
        if (utxoMemoryRepository?.TryGetFeeReservation(spent.TxId, spent.Index, out var reservationId) == true)
            reservation = reservationId.ToString();

        var details = AccountingDetailsCodec.Create(("spentBy", new TxId(spender.GetHash().ToBytes()).ToString()),
                                                    ("address", spent.WalletAddress?.Address),
                                                    ("addressType", Enum.GetName(spent.AddressType)),
                                                    (AccountingDetailKeys.Source, source.Source),
                                                    (AccountingDetailKeys.Purpose,
                                                     source.Purpose is { } purpose ? Enum.GetName(purpose) : null),
                                                    ("reservation", reservation));
        var amountMsat = -checked((long)spent.Amount.MilliSatoshi);
        var channelId = spent.LockedToChannelId ?? source.ChannelId;
        effects.Accounting.Add(new AccountingCandidate(
                                   AccountingEventKeys.WalletOutputSpent(spent.TxId, spent.Index),
                                   key => NewOnchainEvent(key, AccountingEventKind.WalletOutputSpent, effects.Height,
                                                          spent.TxId, spent.Index, channelId, amountMsat, 0,
                                                          details)));
    }

    /// <summary>
    /// Keys and stages the block's accounting events in its unit of work; a fact whose confirmation is recorded and
    /// stands (a block processed again) is skipped.
    /// </summary>
    private async Task StageAccountingAsync(IUnitOfWork uow, BlockEffects effects)
    {
        foreach (var candidate in effects.Accounting)
        {
            try
            {
                var existing = await uow.AccountingEventDbRepository.GetByKeyPrefixAsync(candidate.BaseKey);
                var key = AccountingConfirmations.NextConfirmationKey(candidate.BaseKey, existing);
                if (key is null)
                {
                    if (_logger.IsEnabled(LogLevel.Debug))
                        _logger.LogDebug("Accounting: {Key} is already recorded at block {Height}", candidate.BaseKey,
                                         effects.Height);
                    continue;
                }

                uow.AccountingEventDbRepository.Add(candidate.Create(key));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Accounting: cannot record {Key} at block {Height}", candidate.BaseKey,
                                 effects.Height);
            }
        }
    }

    /// <summary>
    /// Stages the reversals of a rewind to <paramref name="forkHeight"/> in its save (see the class remarks for which
    /// events are reversed).
    /// </summary>
    /// <param name="uow">The rewind's unit of work.</param>
    /// <param name="forkHeight">The fork point.</param>
    /// <param name="removedDeposits">The wallet outputs the rollback removed (deposits above the fork).</param>
    /// <param name="restored">The wallet outputs the rollback restored, with the height of the disconnected block that
    /// had spent them.</param>
    private async Task StageReorgReversalsAsync(IUnitOfWork uow, uint forkHeight,
                                                IReadOnlyList<UtxoModel> removedDeposits,
                                                IReadOnlyList<(UtxoModel Utxo, uint SpentHeight)> restored)
    {
        try
        {
            var repository = uow.AccountingEventDbRepository;
            var now = _timeProvider.GetUtcNow();
            var aboveFork = await repository.GetAtOrAboveHeightAsync(forkHeight + 1, s_reorgReversibleKinds);
            var aboveKeys = aboveFork.Select(e => e.EventKey).ToHashSet(StringComparer.Ordinal);
            var standingAbove = aboveFork.Where(e => e.Kind != AccountingEventKind.Reversal
                                                  && !AccountingConfirmations.IsReversed(e, aboveKeys))
                                         .ToList();

            var reversals = new Dictionary<string, AccountingEventModel>(StringComparer.Ordinal);
            void Reverse(AccountingEventModel original, bool unrecorded = false)
            {
                var reversal = AccountingConfirmations.CreateReversal(original, now, forkHeight, unrecorded);
                reversals.TryAdd(reversal.EventKey, reversal);
            }

            // The broadcasts confirmed in the disconnected blocks are pending again
            foreach (var confirmed in standingAbove.Where(e => e.Kind is AccountingEventKind.AnchorCpfpFee
                                                                 or AccountingEventKind.SweepFeeBump
                                                                 or AccountingEventKind.WalletSent))
                Reverse(confirmed);

            // The deposits the rollback removed (whatever height they were recorded at)
            var handled = new HashSet<(TxId, uint)>();
            foreach (var deposit in removedDeposits)
            {
                handled.Add((deposit.TxId, deposit.Index));
                var baseKey = AccountingEventKeys.WalletReceived(deposit.TxId, deposit.Index);
                var recorded = await FindStandingAsync(repository, baseKey);
                if (recorded is not null)
                    Reverse(recorded);
                else
                    Reverse(NewOnchainEvent(baseKey, AccountingEventKind.WalletReceived, deposit.BlockHeight,
                                            deposit.TxId, deposit.Index, deposit.LockedToChannelId,
                                            checked((long)deposit.Amount.MilliSatoshi), 0,
                                            AccountingDetailsCodec.Create()), unrecorded: true);
            }

            // The outputs the rollback restored: their spends are undone
            foreach (var (utxo, spentHeight) in restored)
            {
                handled.Add((utxo.TxId, utxo.Index));
                var baseKey = AccountingEventKeys.WalletOutputSpent(utxo.TxId, utxo.Index);
                var recorded = await FindStandingAsync(repository, baseKey);
                if (recorded is not null)
                    Reverse(recorded);
                else
                    Reverse(NewOnchainEvent(baseKey, AccountingEventKind.WalletOutputSpent, spentHeight, utxo.TxId,
                                            utxo.Index, null, -checked((long)utxo.Amount.MilliSatoshi), 0,
                                            AccountingDetailsCodec.Create()), unrecorded: true);
            }

            // An output deposited and spent in the disconnected blocks: its rows are already gone, both are undone
            foreach (var spend in standingAbove.Where(e => e.Kind == AccountingEventKind.WalletOutputSpent))
            {
                if (spend.TxId is not { } txId || spend.OutputIndex is not { } index || !handled.Add((txId, index)))
                    continue;

                var deposit = standingAbove.Where(e => e.Kind == AccountingEventKind.WalletReceived
                                                    && e.TxId is { } depositTxId && depositTxId.Equals(txId)
                                                    && e.OutputIndex == index && e.BlockHeight <= spend.BlockHeight)
                                           .MaxBy(e => e.BlockHeight);
                if (deposit is null)
                    continue; // deposited at or below the fork: the output stays spent

                Reverse(deposit);
                Reverse(spend);
            }

            foreach (var reversal in reversals.Values)
                repository.Add(reversal);

            if (reversals.Count > 0 && _logger.IsEnabled(LogLevel.Information))
                _logger.LogInformation("Accounting: reorg to {Fork} reverses {Count} events", forkHeight,
                                       reversals.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Accounting: cannot record the reversals of the reorg to {Fork}", forkHeight);
        }
    }

    /// <summary>The latest standing (not reversed) confirmation of the fact named by <paramref name="baseKey"/>.</summary>
    private static async Task<AccountingEventModel?> FindStandingAsync(
        IAccountingEventDbRepository repository, string baseKey)
    {
        var events = await repository.GetByKeyPrefixAsync(baseKey);
        var keys = events.Select(e => e.EventKey).ToHashSet(StringComparer.Ordinal);
        return events.Where(e => e.Kind != AccountingEventKind.Reversal && !AccountingConfirmations.IsReversed(e, keys)
                              && (e.EventKey == baseKey || e.EventKey.StartsWith(baseKey + ":c", StringComparison.Ordinal)))
                     .MaxBy(e => e.BlockHeight);
    }

    private AccountingEventModel NewOnchainEvent(string key, AccountingEventKind kind, uint height, TxId txId,
                                                 uint? outputIndex, ChannelId? channelId, long amountMsat,
                                                 long feeMsat, IReadOnlyDictionary<string, string> details) =>
        new()
        {
            EventKey = key,
            Kind = kind,
            OccurredAt = _timeProvider.GetUtcNow(),
            BlockHeight = height,
            ChannelId = channelId,
            TxId = txId,
            OutputIndex = outputIndex,
            AmountMsat = amountMsat,
            FeeMsat = feeMsat,
            Finality = AccountingFinality.Confirmed,
            Details = details
        };

    private static long ToMsat(LightningMoney? amount) =>
        amount is null ? 0 : checked((long)amount.MilliSatoshi);

    private static string? FormatSat(LightningMoney? amount) =>
        amount?.Satoshi.ToString(CultureInfo.InvariantCulture);

    /// <summary>An accounting event of a block, keyed when it is staged (<see cref="StageAccountingAsync"/>).</summary>
    /// <param name="BaseKey">The fact's key (its first confirmation's).</param>
    /// <param name="Create">Builds the event under the key it gets.</param>
    private sealed record AccountingCandidate(string BaseKey, Func<string, AccountingEventModel> Create);

    /// <summary>Where a wallet movement's transaction comes from.</summary>
    /// <param name="Source">One of the <c>*Source</c> constants.</param>
    /// <param name="Purpose">The stored broadcast's purpose, for <see cref="BroadcastSource"/>.</param>
    /// <param name="ChannelId">The channel the transaction belongs to, when known.</param>
    private sealed record WalletTransactionSource(string Source, BroadcastPurpose? Purpose, ChannelId? ChannelId);
}