using System.Globalization;

namespace NLightning.Application.Accounting.Backfill;

using Domain.Accounting.Constants;
using Domain.Accounting.Enums;
using Domain.Accounting.Models;
using Domain.Accounting.Services;
using Domain.Bitcoin.Wallet.Models;
using Domain.Channels.Enums;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Onchain.Enums;
using Domain.Onchain.Models;
using Onchain.Accounting;

/// <summary>
/// The events of the accounting backfill's cutover (NL-602 A1-T6): opening balances per bucket, the synthetic close of a
/// force close being resolved, the marker, and the memo form of a live writer's event. Pure: the service reads the
/// database and stages what these return.
/// </summary>
/// <remarks>
/// <para>Buckets: <c>channel:{channelId}</c> (our gross local balance of a channel past its funding confirmation),
/// <c>wallet</c> (every wallet output row) and <c>pending:{channelId}</c> (the counted, unresolved outputs of a force
/// close, the <see cref="OnchainAccounting.PendingBucket"/> of that close). Every opening balance is
/// <see cref="AccountingEventKind.OpeningBalance"/>, flagged <see cref="AccountingEventFlags.Backfilled"/>,
/// <see cref="AccountingFinality.Final"/>, under <see cref="AccountingEventKeys.OpeningBalance"/>.</para>
/// <para>A channel resolving on chain gets, besides its pending opening balance, a
/// <see cref="AccountingEventKind.ChannelForceClosed"/> of 0 under the live close key, whose
/// <see cref="OnchainAccounting.CountedVoutsKey"/> lists the unresolved counted outputs of the commitment: the resolution
/// writer (<c>OnchainResolutionExecutor</c>) reads that key to decide whether an output counts, so every later
/// resolution takes its value out of the pending bucket the opening balance filled.</para>
/// </remarks>
internal static class AccountingCutoverEvents
{
    public const string ChannelBucketPrefix = "channel:";
    public const string WalletBucket = "wallet";
    public const string PendingBucketPrefix = "pending:";

    public const string BucketKey = "bucket";
    public const string MemoKey = "memo";
    public const string CutoverAtKey = "cutoverAt";
    public const string BlockHeightKey = "blockHeight";
    public const string SkippedOpeningKey = "skippedOpening";
    public const string OpeningBalanceKeyKey = "openingBalanceKey";

    /// <summary>The bucket of a channel's off-chain balance.</summary>
    public static string ChannelBucket(ChannelId channelId) => ChannelBucketPrefix + channelId;

    /// <summary>The bucket of a force close's pending on-chain outputs.</summary>
    public static string PendingBucket(ChannelId channelId) => PendingBucketPrefix + channelId;

    /// <summary>
    /// Whether a channel holds an off-chain balance of ours at the cutover: its funding confirmed for us (the short
    /// channel id is set together with that transition) and it is neither closed nor resolving on chain.
    /// </summary>
    public static bool HoldsChannelBalance(ChannelModel channel) =>
        channel.State is ChannelState.ReadyForUs or ChannelState.Open or ChannelState.ShuttingDown
            or ChannelState.Negotiating or ChannelState.Closing or ChannelState.Failed
     && IsSet(channel.ShortChannelId);

    /// <summary>The opening balance of a channel: our gross local balance (pending offered HTLCs included, NL-062).
    /// </summary>
    public static AccountingEventModel ChannelOpening(ChannelModel channel, DateTimeOffset cutoverAt, uint height)
    {
        var shortChannelId = IsSet(channel.ShortChannelId) ? channel.ShortChannelId : (ShortChannelId?)null;
        return new AccountingEventModel
        {
            EventKey = AccountingEventKeys.OpeningBalance(ChannelBucket(channel.ChannelId)),
            Kind = AccountingEventKind.OpeningBalance,
            OccurredAt = cutoverAt,
            BlockHeight = height > 0 ? height : null,
            ChannelId = channel.ChannelId,
            ShortChannelId = shortChannelId,
            Counterparty = channel.RemoteNodeId,
            AmountMsat = checked((long)channel.LocalBalance.MilliSatoshi),
            FeeMsat = 0,
            Finality = AccountingFinality.Final,
            Flags = AccountingEventFlags.Backfilled,
            Details = AccountingDetailsCodec.Create(
                (BucketKey, OnchainAccounting.ChannelBucket),
                ("capacitySat", channel.FundingOutput is { } funding ? Text(funding.Amount.Satoshi) : null),
                ("state", channel.State.ToString()),
                ("scid", shortChannelId?.ToString()),
                ("remoteBalanceMsat", Text(checked((long)channel.RemoteBalance.MilliSatoshi))),
                ("isInitiator", channel.IsInitiator ? "true" : "false"))
        };
    }

    /// <summary>The wallet's opening balance: every wallet output row (confirmed or not, locked or not).</summary>
    public static AccountingEventModel WalletOpening(IReadOnlyCollection<UtxoModel> utxos, DateTimeOffset cutoverAt,
                                                     uint height)
    {
        long total = 0;
        long locked = 0;
        foreach (var utxo in utxos)
        {
            var msat = checked((long)utxo.Amount.MilliSatoshi);
            total = checked(total + msat);
            if (utxo.LockedToChannelId is not null || utxo.UsedInTransactionId is not null)
                locked = checked(locked + msat);
        }

        return new AccountingEventModel
        {
            EventKey = AccountingEventKeys.OpeningBalance(WalletBucket),
            Kind = AccountingEventKind.OpeningBalance,
            OccurredAt = cutoverAt,
            BlockHeight = height > 0 ? height : null,
            AmountMsat = total,
            FeeMsat = 0,
            Finality = AccountingFinality.Final,
            Flags = AccountingEventFlags.Backfilled,
            Details = AccountingDetailsCodec.Create((BucketKey, WalletBucket),
                                                    ("utxoCount", Text(utxos.Count)),
                                                    ("lockedMsat", Text(locked)))
        };
    }

    /// <summary>The cutover events of a channel resolving a force close.</summary>
    /// <param name="Opening">The pending bucket's opening balance.</param>
    /// <param name="Close">The synthetic close event (amount 0) whose counted vouts the resolution writer reads.</param>
    /// <param name="PendingMsat">The opening balance's amount.</param>
    public sealed record ResolvingChannelEvents(AccountingEventModel Opening, AccountingEventModel Close,
                                                long PendingMsat);

    /// <summary>
    /// The pending opening balance and the synthetic close of a channel resolving <paramref name="close"/>: the pending
    /// amount is the value of every unresolved (Pending, Waiting, Broadcast) output that counts as the resolution writer
    /// counts it: an output of the commitment that counts at the close
    /// (<see cref="OnchainAccounting.CountsAtClose(OutputDescriptorKind, HtlcDirection?, bool)"/>), listed in the close's
    /// counted vouts, and any other output (a second-level output of our HTLC transaction) that is
    /// <see cref="OutputDescriptorKind.DelayedToLocal"/>.
    /// </summary>
    public static ResolvingChannelEvents ResolvingChannel(ChannelModel channel, ChannelCloseModel close,
                                                          IReadOnlyList<OutputResolutionModel> outputs,
                                                          DateTimeOffset cutoverAt, uint height)
    {
        var weFund = channel.IsInitiator;
        var countedVouts = new SortedSet<uint>();
        long pending = 0;
        long pendingHtlcs = 0;
        var counted = 0;
        foreach (var row in outputs)
        {
            if (row.State is not (OutputResolutionState.Pending or OutputResolutionState.Waiting
                                                               or OutputResolutionState.Broadcast))
                continue;

            var data = OutputDescriptorData.TryDecode(row);
            bool counts;
            if (row.TransactionId == close.CommitmentTransactionId)
            {
                counts = OnchainAccounting.CountsAtClose(row.Descriptor, row.HtlcDirection ?? data?.Htlc?.Direction,
                                                         weFund);
                if (counts)
                    countedVouts.Add(row.OutputIndex);
            }
            else
            {
                counts = row.Descriptor == OutputDescriptorKind.DelayedToLocal;
            }

            if (!counts)
                continue;

            counted++;
            var valueMsat = data is null ? 0 : checked((long)data.AmountSat * 1_000);
            pending = checked(pending + valueMsat);
            if (row.HtlcDirection is not null || data?.Htlc is not null)
                pendingHtlcs = checked(pendingHtlcs + valueMsat);
        }

        var bucket = PendingBucket(channel.ChannelId);
        var shortChannelId = IsSet(channel.ShortChannelId) ? channel.ShortChannelId : (ShortChannelId?)null;
        var opening = new AccountingEventModel
        {
            EventKey = AccountingEventKeys.OpeningBalance(bucket),
            Kind = AccountingEventKind.OpeningBalance,
            OccurredAt = cutoverAt,
            BlockHeight = height > 0 ? height : null,
            AmountMsat = pending,
            FeeMsat = 0,
            Finality = AccountingFinality.Final,
            Flags = AccountingEventFlags.Backfilled,
            ChannelId = channel.ChannelId,
            ShortChannelId = shortChannelId,
            TxId = close.CommitmentTransactionId,
            Counterparty = channel.RemoteNodeId,
            Details = AccountingDetailsCodec.Create(
                (BucketKey, OnchainAccounting.PendingBucket),
                (OnchainAccounting.CloseKindKey, close.Kind.ToString()),
                (OnchainAccounting.CloseTxIdKey, close.CommitmentTransactionId.ToString()),
                ("outputCount", Text(counted)),
                ("htlcMsat", Text(pendingHtlcs)))
        };

        var closeEvent = new AccountingEventModel
        {
            EventKey = AccountingEventKeys.ChannelForceClosed(channel.ChannelId, close.CommitmentTransactionId),
            Kind = AccountingEventKind.ChannelForceClosed,
            OccurredAt = cutoverAt,
            BlockHeight = close.SpentAtHeight > 0 ? close.SpentAtHeight : null,
            ChannelId = channel.ChannelId,
            ShortChannelId = shortChannelId,
            TxId = close.CommitmentTransactionId,
            Counterparty = channel.RemoteNodeId,
            AmountMsat = 0,
            FeeMsat = 0,
            Finality = AccountingFinality.Confirmed,
            Flags = AccountingEventFlags.Backfilled,
            Details = AccountingDetailsCodec.Create(
                (OnchainAccounting.CloseKindKey, close.Kind.ToString()),
                (OnchainAccounting.CommitmentNumberKey, close.CommitmentNumber?.ToString(CultureInfo.InvariantCulture)),
                (OnchainAccounting.FunderKey, weFund ? "true" : "false"),
                (OnchainAccounting.BalanceSourceKey, "opening-balance"),
                (OnchainAccounting.PendingKey, "0"),
                (OnchainAccounting.LostKey, "0"),
                (OnchainAccounting.CountedVoutsKey,
                 string.Join(",", countedVouts.Select(v => v.ToString(CultureInfo.InvariantCulture)))),
                (OnchainAccounting.OpeningBalanceKey, "true"),
                (OpeningBalanceKeyKey, AccountingEventKeys.OpeningBalance(bucket)))
        };

        return new ResolvingChannelEvents(opening, closeEvent, pending);
    }

    /// <summary>The cutover's marker (amount 0), written last in the cutover's save.</summary>
    public static AccountingEventModel Marker(DateTimeOffset cutoverAt, uint height,
                                              params (string Key, string? Value)[] details) =>
        new()
        {
            EventKey = AccountingEventKeys.Cutover(),
            Kind = AccountingEventKind.OpeningBalance,
            OccurredAt = cutoverAt,
            BlockHeight = height > 0 ? height : null,
            AmountMsat = 0,
            FeeMsat = 0,
            Finality = AccountingFinality.Final,
            Flags = AccountingEventFlags.Backfilled,
            Details = AccountingDetailsCodec.Create(
            [
                (CutoverAtKey, cutoverAt.ToString("O", CultureInfo.InvariantCulture)),
                (BlockHeightKey, Text(height)),
                .. details
            ])
        };

    /// <summary>The marker of a finished memo pass (amount 0).</summary>
    public static AccountingEventModel MemoCompleteMarker(DateTimeOffset at, AccountingMemoResult totals) =>
        new()
        {
            EventKey = AccountingEventKeys.MemoComplete(),
            Kind = AccountingEventKind.OpeningBalance,
            OccurredAt = at,
            AmountMsat = 0,
            FeeMsat = 0,
            Finality = AccountingFinality.Final,
            Flags = AccountingEventFlags.Backfilled,
            Details = AccountingDetailsCodec.Create((MemoKey, "true"),
                                                    ("invoices", Text(totals.Invoices)),
                                                    ("payments", Text(totals.Payments)),
                                                    ("forwards", Text(totals.Forwards)),
                                                    ("channels", Text(totals.Channels)))
        };

    /// <summary>The cutover time stored in a marker, or null when it cannot be read.</summary>
    public static DateTimeOffset? ReadCutoverAt(AccountingEventModel marker) =>
        marker.Details.TryGetValue(CutoverAtKey, out var text)
     && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at)
            ? at
            : null;

    /// <summary>
    /// The memo form of a live writer's event: the same key, kind and amounts, flagged
    /// <see cref="AccountingEventFlags.Backfilled"/> with <c>memo=true</c> in its details, so the books count it in the
    /// P&amp;L statistics and never apply it to a bucket (the opening balances already hold its effect).
    /// </summary>
    /// <param name="live">The event the live writer builds for the fact.</param>
    /// <param name="removeDetails">Details the backfill cannot know (the live writer's value would be a guess).</param>
    public static AccountingEventModel AsMemo(AccountingEventModel live, params string[] removeDetails)
    {
        var details = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in live.Details)
            if (!removeDetails.Contains(key, StringComparer.Ordinal))
                details[key] = value;
        details[MemoKey] = "true";

        return new AccountingEventModel
        {
            EventKey = live.EventKey,
            Kind = live.Kind,
            OccurredAt = live.OccurredAt,
            BlockHeight = live.BlockHeight,
            ChannelId = live.ChannelId,
            ShortChannelId = live.ShortChannelId,
            PaymentHash = live.PaymentHash,
            TxId = live.TxId,
            OutputIndex = live.OutputIndex,
            Counterparty = live.Counterparty,
            AmountMsat = live.AmountMsat,
            FeeMsat = live.FeeMsat,
            Finality = live.Finality,
            Flags = live.Flags | AccountingEventFlags.Backfilled,
            Details = details
        };
    }

    private static bool IsSet(ShortChannelId shortChannelId) => ((byte[]?)shortChannelId)?.Length > 0;

    private static string Text(long value) => value.ToString(CultureInfo.InvariantCulture);
}