using System.Globalization;

namespace NLightning.Application.Accounting.Backfill;

using Domain.Accounting.Constants;
using Domain.Accounting.Enums;
using Domain.Accounting.Models;
using Domain.Accounting.Services;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Onchain.Enums;
using Domain.Onchain.Models;
using Infrastructure.Bitcoin.Onchain;
using Onchain.Accounting;

/// <summary>
/// The memo history of a force close recorded before the accounting cutover (NL-624, plan
/// <c>docs/agents/ACCOUNTING_PLAN.md</c> §4 "Backfill"): its <see cref="AccountingEventKind.ChannelForceClosed"/>, the
/// resolution of each of its outputs (<see cref="AccountingEventKind.OutputResolved"/>,
/// <see cref="AccountingEventKind.PenaltyClaimed"/>, <see cref="AccountingEventKind.BreachLoss"/>) and the fees of our
/// anchor CPFP children (<see cref="AccountingEventKind.AnchorCpfpFee"/>), rebuilt from the tables the node keeps
/// (<c>ChannelCloses</c>, <c>OutputResolutions</c>, <c>BroadcastTransactions</c>), never from bitcoind. Pure: the
/// backfill reads the rows and stages what these return through <see cref="AccountingCutoverEvents.AsMemo"/>.
/// </summary>
/// <remarks>
/// <para>Same keys and kinds as the live writers (<c>OnchainChannelWatcher</c>, <c>OnchainResolutionExecutor</c>, the
/// chain monitor's CPFP confirmation): <see cref="AccountingEventKeys.ChannelForceClosed"/> of the commitment,
/// <see cref="AccountingEventKeys.OutputResolved"/>/<see cref="AccountingEventKeys.PenaltyClaimed"/>/
/// <see cref="AccountingEventKeys.BreachLoss"/> or <see cref="AccountingEventKeys.OutputIgnored"/> per output, and
/// <see cref="AccountingEventKeys.AnchorCpfpFee"/> per child. A memo event never posts (plan §6.1), so what the tables
/// cannot tell is approximated and said so in the details.</para>
/// <para><b>The close.</b> The balance B is our latest local commitment's (the channel row's gross balance,
/// <see cref="OnchainAccounting.BalanceFromLatestLocal"/>): the commitment's spec is not rebuilt. <c>pendingMsat</c> is
/// the value of the commitment's counted rows (<see cref="OnchainAccounting.CountsAtClose(OutputDescriptorKind, Domain.Channels.Enums.HtlcDirection?, bool)"/>),
/// listed in <see cref="OnchainAccounting.CountedVoutsKey"/>. <c>FeeMsat</c> is the commitment transaction's fee when
/// we funded the channel and our commitment's broadcast row tells it (its stored fee, else its capacity less its
/// outputs), else 0 with <c>feeUnknown</c> (the peer's commitment is not stored); the peer's anchor and rounding stay
/// in <c>lostMsat</c> = B − pending − fee.</para>
/// <para><b>An output.</b> Resolved by us when one of our confirmed broadcasts spends it (the row's resolving
/// transaction first): the live flows (<see cref="OnchainAccounting.Ours"/>, its fee and wallet share from the
/// spender's outputs); otherwise the peer took it (<see cref="OnchainAccounting.Lost"/>). An output given up writes a
/// loss only when it was counted, as live. Outputs not final (pending, waiting, broadcast) get nothing.</para>
/// </remarks>
internal static class ForceCloseMemoEvents
{
    /// <summary>The detail that says a figure is approximated from the stored rows.</summary>
    public const string SourceKey = "memoSource";

    /// <summary>The value of <see cref="SourceKey"/>.</summary>
    public const string FromRecords = "records";

    /// <summary>The detail with the channel's capacity at the close, in satoshis (the report's fallback, NL-682).
    /// </summary>
    public const string CapacityKey = "capacitySat";

    /// <summary>The detail with the block of the channel's original funding (the report's open time when no
    /// <c>ChannelFunded</c> tells it, such as a spliced channel's, NL-682).</summary>
    public const string OpenedAtHeightKey = "openedAtHeight";

    /// <summary>
    /// The <see cref="AccountingEventKind.ChannelForceClosed"/> of a close recorded before the cutover (see the
    /// remarks), not yet in memo form. <paramref name="openedAtHeight"/> is the block of the channel's original funding
    /// when known.
    /// </summary>
    public static AccountingEventModel ForceClosed(ChannelModel channel, ChannelCloseModel close,
                                                   IReadOnlyList<OutputResolutionModel> rows,
                                                   IReadOnlyList<BroadcastTransactionModel> broadcasts,
                                                   uint? openedAtHeight = null)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(close);
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(broadcasts);

        var weFund = channel.IsInitiator;
        var balance = checked((long)channel.LocalBalance.MilliSatoshi);
        var commitmentRows = rows.Where(r => r.TransactionId == close.CommitmentTransactionId)
                                 .OrderBy(r => r.OutputIndex)
                                 .ToList();
        var counted = commitmentRows.Where(r => OnchainAccounting.CountsAtClose(r.Descriptor, r.HtlcDirection, weFund))
                                    .ToList();
        var pending = counted.Sum(ValueMsat);
        var ours = commitmentRows.Sum(r => (long)(OutputDescriptorData.TryDecode(r)?.AmountSat ?? 0));

        var commitmentFeeSat = CommitmentFeeSat(channel, close, broadcasts);
        var fee = weFund && commitmentFeeSat is { } feeSat ? checked(feeSat * 1_000) : 0;
        var lost = balance - pending - fee;

        return new AccountingEventModel
        {
            EventKey = AccountingEventKeys.ChannelForceClosed(channel.ChannelId, close.CommitmentTransactionId),
            Kind = AccountingEventKind.ChannelForceClosed,
            OccurredAt = close.CreatedAt,
            BlockHeight = close.SpentAtHeight > 0 ? close.SpentAtHeight : null,
            ChannelId = channel.ChannelId,
            ShortChannelId = ScidOf(channel),
            TxId = close.CommitmentTransactionId,
            Counterparty = channel.RemoteNodeId,
            AmountMsat = -balance,
            FeeMsat = fee,
            Finality = AccountingFinality.Confirmed,
            Details = AccountingDetailsCodec.Create(
                (OnchainAccounting.BucketFromKey, OnchainAccounting.ChannelBucket),
                (OnchainAccounting.BucketToKey, OnchainAccounting.PendingBucket),
                (OnchainAccounting.CloseKindKey, close.Kind.ToString()),
                (OnchainAccounting.CommitmentNumberKey,
                 close.CommitmentNumber?.ToString(CultureInfo.InvariantCulture)),
                (OnchainAccounting.FunderKey, weFund ? "true" : "false"),
                (OnchainAccounting.BalanceSourceKey, OnchainAccounting.BalanceFromLatestLocal),
                (OnchainAccounting.PendingKey, Text(pending)), (OnchainAccounting.LostKey, Text(lost)),
                (OnchainAccounting.OurOutputsKey, Text(ours)),
                (OnchainAccounting.CommitmentFeeKey, commitmentFeeSat is { } known ? Text(known) : null),
                (OnchainAccounting.LatestBalanceKey, Text(balance)),
                (OnchainAccounting.FundingTxIdKey, channel.FundingOutput?.TransactionId.ToString()),
                (OnchainAccounting.CountedVoutsKey,
                 string.Join(",", counted.Select(r => r.OutputIndex.ToString(CultureInfo.InvariantCulture)))),
                ("feeUnknown", weFund && commitmentFeeSat is null ? "true" : null),
                (CapacityKey,
                 channel.FundingOutput is { } funding ? Text(checked((long)funding.Amount.Satoshi)) : null),
                (OpenedAtHeightKey, openedAtHeight is > 0 ? Text(openedAtHeight.Value) : null),
                (SourceKey, FromRecords))
        };
    }

    /// <summary>
    /// The resolution events of the close's final outputs (see the remarks), not yet in memo form; with
    /// <paramref name="resolvedAtOrBelow"/>, only the outputs resolved in a block at or below that height (a close
    /// still resolving at the cutover: what came after is live).
    /// </summary>
    public static IReadOnlyList<AccountingEventModel> Resolutions(ChannelModel channel, ChannelCloseModel close,
                                                                  IReadOnlyList<OutputResolutionModel> rows,
                                                                  IReadOnlyList<BroadcastTransactionModel> broadcasts,
                                                                  uint? resolvedAtOrBelow = null)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(close);
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(broadcasts);

        var weFund = channel.IsInitiator;
        var byOutpoint = new Dictionary<(TxId, uint), OutputResolutionModel>();
        foreach (var row in rows)
            byOutpoint[(row.TransactionId, row.OutputIndex)] = row;

        // Our confirmed transactions that can have resolved an output (a peer's commitment we kept is not ours)
        var spenders = new List<(BroadcastTransactionModel Row, ChainTx Tx)>();
        foreach (var broadcast in broadcasts)
            if (broadcast is { State: BroadcastState.Confirmed, Purpose: not BroadcastPurpose.PeerCommitment }
             && ChainTxMapper.TryParse(broadcast.RawTransaction, out var tx) && tx is not null)
                spenders.Add((broadcast, tx));

        var events = new List<AccountingEventModel>();
        foreach (var row in rows.OrderBy(r => r.TransactionId.ToString(), StringComparer.Ordinal)
                                .ThenBy(r => r.OutputIndex))
        {
            var counted = row.TransactionId == close.CommitmentTransactionId
                              ? OnchainAccounting.CountsAtClose(row.Descriptor, row.HtlcDirection, weFund)
                              : row.Descriptor == OutputDescriptorKind.DelayedToLocal;
            var data = OutputDescriptorData.TryDecode(row);
            var valueMsat = data is null ? 0 : checked((long)data.AmountSat * 1_000);
            var revoked = OnchainAccounting.IsRevoked(row.Descriptor);

            if (row.State == OutputResolutionState.Ignored)
            {
                // Given up: a loss only when it was counted (live rule); the time is unknown, so never for a close
                // still resolving at the cutover
                if (!counted || resolvedAtOrBelow is not null)
                    continue;

                events.Add(OnchainAccounting.Resolution(
                               channel, close, row, data,
                               revoked ? AccountingEventKind.BreachLoss : AccountingEventKind.OutputResolved,
                               AccountingEventKeys.OutputIgnored(row.TransactionId, row.OutputIndex),
                               OnchainAccounting.Lost(valueMsat, true, AccountingDetailKeys.ResolvedByIgnored),
                               counted, null, row.ResolvedHeight ?? close.SpentAtHeight, close.CreatedAt,
                               extraDetails: [(SourceKey, FromRecords)]));
                continue;
            }

            if (row.State is not (OutputResolutionState.Resolved or OutputResolutionState.Irrevocable))
                continue;

            if (resolvedAtOrBelow is { } limit && (row.ResolvedHeight is not { } at || at > limit))
                continue;

            var spender = FindSpender(row, spenders);
            var flows = spender is { } ours
                            ? OnchainAccounting.Ours(row, valueMsat, counted, ours.Tx, byOutpoint,
                                                     ours.Row.Purpose == BroadcastPurpose.HtlcTransaction)
                            : OnchainAccounting.Lost(valueMsat, counted, AccountingDetailKeys.ResolvedByPeer);
            if (data is null)
                flows = flows with { Note = "the output's value is unknown" };

            var kind = revoked
                           ? spender is not null ? AccountingEventKind.PenaltyClaimed : AccountingEventKind.BreachLoss
                           : AccountingEventKind.OutputResolved;
            var key = kind switch
            {
                AccountingEventKind.PenaltyClaimed => AccountingEventKeys.PenaltyClaimed(row.TransactionId,
                                                                                         row.OutputIndex),
                AccountingEventKind.BreachLoss => AccountingEventKeys.BreachLoss(row.TransactionId, row.OutputIndex),
                _ => AccountingEventKeys.OutputResolved(row.TransactionId, row.OutputIndex)
            };
            var height = row.ResolvedHeight ?? spender?.Row.ConfirmedHeight ?? close.SpentAtHeight;
            events.Add(OnchainAccounting.Resolution(channel, close, row, data, kind, key, flows, counted,
                                                    spender?.Tx.TxId, height, spender?.Row.CreatedAt ?? close.CreatedAt,
                                                    spender?.Row.ReplacesTransactionId is not null,
                                                    [(SourceKey, FromRecords)]));
        }

        return events;
    }

    /// <summary>
    /// The <see cref="AccountingEventKind.AnchorCpfpFee"/> of each confirmed anchor CPFP child of the channel (with
    /// <paramref name="confirmedAtOrBelow"/>, only those confirmed at or below that height), not yet in memo form: the
    /// shape of the chain monitor's event.
    /// </summary>
    public static IReadOnlyList<AccountingEventModel> AnchorCpfpFees(ChannelId channelId,
                                                                     IReadOnlyList<BroadcastTransactionModel> broadcasts,
                                                                     uint? confirmedAtOrBelow = null)
    {
        ArgumentNullException.ThrowIfNull(broadcasts);

        return broadcasts.Where(b => b is { Purpose: BroadcastPurpose.AnchorCpfp, State: BroadcastState.Confirmed }
                                  && (confirmedAtOrBelow is not { } limit
                                   || b.ConfirmedHeight is { } height && height <= limit))
                         .OrderBy(b => b.ConfirmedHeight)
                         .ThenBy(b => b.TransactionId.ToString(), StringComparer.Ordinal)
                         .Select(b => new AccountingEventModel
                         {
                             EventKey = AccountingEventKeys.AnchorCpfpFee(b.TransactionId),
                             Kind = AccountingEventKind.AnchorCpfpFee,
                             OccurredAt = b.CreatedAt,
                             BlockHeight = b.ConfirmedHeight,
                             ChannelId = channelId,
                             TxId = b.TransactionId,
                             AmountMsat = 0,
                             FeeMsat = b.Fee is { } fee ? checked((long)fee.MilliSatoshi) : 0,
                             Finality = AccountingFinality.Confirmed,
                             Details = AccountingDetailsCodec.Create(
                                 ("purpose", nameof(BroadcastPurpose.AnchorCpfp)),
                                 ("replaces", b.ReplacesTransactionId?.ToString()),
                                 ("feeUnknown", b.Fee is null ? "true" : null),
                                 (SourceKey, FromRecords))
                         })
                         .ToList();
    }

    /// <summary>Our confirmed transaction that spent the row's outpoint: its resolving transaction first.</summary>
    private static (BroadcastTransactionModel Row, ChainTx Tx)? FindSpender(
        OutputResolutionModel row, IReadOnlyList<(BroadcastTransactionModel Row, ChainTx Tx)> spenders)
    {
        (BroadcastTransactionModel Row, ChainTx Tx)? found = null;
        foreach (var spender in spenders)
        {
            if (spender.Tx.IndexOfInputSpending(row.TransactionId, row.OutputIndex) < 0)
                continue;

            if (spender.Row.TransactionId == row.ResolvingTransactionId)
                return spender;

            found ??= spender;
        }

        return found;
    }

    /// <summary>The commitment transaction's fee in sat, when our broadcast row of it tells it.</summary>
    private static long? CommitmentFeeSat(ChannelModel channel, ChannelCloseModel close,
                                          IReadOnlyList<BroadcastTransactionModel> broadcasts)
    {
        var commitment = broadcasts.FirstOrDefault(b => b.TransactionId == close.CommitmentTransactionId);
        if (commitment is null)
            return null;

        if (commitment.Fee is { } fee)
            return checked((long)fee.Satoshi);

        if (channel.FundingOutput is not { } funding || !ChainTxMapper.TryParse(commitment.RawTransaction, out var tx)
                                                     || tx is null)
            return null;

        var outputs = tx.Outputs.Aggregate(0UL, (sum, o) => sum + o.AmountSat);
        var capacity = (ulong)funding.Amount.Satoshi;
        return capacity >= outputs ? (long)(capacity - outputs) : null;
    }

    private static long ValueMsat(OutputResolutionModel row) =>
        OutputDescriptorData.TryDecode(row) is { } data ? checked((long)data.AmountSat * 1_000) : 0;

    private static ShortChannelId? ScidOf(ChannelModel channel) =>
        ((byte[]?)channel.ShortChannelId)?.Length > 0 ? channel.ShortChannelId : (ShortChannelId?)null;

    private static string Text(long value) => value.ToString(CultureInfo.InvariantCulture);
}