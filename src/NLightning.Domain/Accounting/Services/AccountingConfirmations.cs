using System.Globalization;

namespace NLightning.Domain.Accounting.Services;

using Constants;
using Enums;
using Models;

/// <summary>
/// The rules of on-chain accounting events that a reorg can undo (plan <c>docs/agents/ACCOUNTING_PLAN.md</c> §3,
/// principle 4): a disconnected block's events are negated by a <see cref="AccountingEventKind.Reversal"/> keyed
/// <see cref="AccountingEventKeys.Reversal"/>(original key, original height), never deleted, and a fact that confirms
/// again afterwards is recorded under the next free confirmation key.
/// </summary>
public static class AccountingConfirmations
{
    /// <summary>The detail of a reversal that names the event it negates.</summary>
    public const string ReversesDetail = "reverses";

    /// <summary>The detail of a reversal that names the kind of the event it negates.</summary>
    public const string OriginalKindDetail = "originalKind";

    /// <summary>The detail of a reversal that holds the fork height of the reorg that undid the original.</summary>
    public const string ForkHeightDetail = "forkHeight";

    /// <summary>The detail of a reversal written for a fact the feed never recorded (it predates the feed).</summary>
    public const string UnrecordedDetail = "unrecorded";

    /// <summary>The key suffix of a reversal of a fact the feed never recorded.</summary>
    private const string UnrecordedKeySuffix = ":unrecorded";

    /// <summary>
    /// Whether <paramref name="accountingEvent"/> was reversed: <paramref name="eventKeys"/> (the keys of the feed
    /// around it) hold its reversal.
    /// </summary>
    public static bool IsReversed(AccountingEventModel accountingEvent, IReadOnlySet<string> eventKeys)
    {
        ArgumentNullException.ThrowIfNull(accountingEvent);
        ArgumentNullException.ThrowIfNull(eventKeys);

        return accountingEvent.BlockHeight is { } height
            && eventKeys.Contains(AccountingEventKeys.Reversal(accountingEvent.EventKey, height));
    }

    /// <summary>
    /// The key to record a confirmation of the fact named by <paramref name="baseKey"/> under, or null when one of its
    /// confirmations is recorded and still stands (not reversed): the block was processed again, nothing is recorded.
    /// </summary>
    /// <param name="baseKey">The fact's key (<see cref="AccountingEventKeys"/>), the first confirmation's key.</param>
    /// <param name="existing">Every event whose key starts with <paramref name="baseKey"/> (saved, staged, sealed or
    /// not; duplicates excluded): its confirmations and their reversals.</param>
    /// <returns><paramref name="baseKey"/> for the first confirmation; after a reversal
    /// <see cref="AccountingEventKeys.Reconfirmed"/>(<paramref name="baseKey"/>, 2), then 3 and so on.</returns>
    public static string? NextConfirmationKey(string baseKey, IReadOnlyCollection<AccountingEventModel> existing)
    {
        ArgumentException.ThrowIfNullOrEmpty(baseKey);
        ArgumentNullException.ThrowIfNull(existing);

        var keys = existing.Select(e => e.EventKey).ToHashSet(StringComparer.Ordinal);
        for (var generation = 1; ; generation++)
        {
            var key = generation == 1 ? baseKey : AccountingEventKeys.Reconfirmed(baseKey, generation);
            var recorded = existing.Where(e => e.Kind != AccountingEventKind.Reversal
                                            && string.Equals(e.EventKey, key, StringComparison.Ordinal))
                                   .ToList();
            if (recorded.Count == 0)
                return key;

            if (recorded.Any(e => !IsReversed(e, keys)))
                return null;
        }
    }

    /// <summary>
    /// The reversal of <paramref name="original"/>, whose block (at its <see cref="AccountingEventModel.BlockHeight"/>)
    /// a reorg down to <paramref name="forkHeight"/> disconnected: the same references, the amount and fee negated.
    /// </summary>
    /// <param name="original">A recorded on-chain event, or for <paramref name="unrecorded"/> a stand-in carrying the
    /// key the fact would have had.</param>
    /// <param name="occurredAt">When the reorg was processed.</param>
    /// <param name="forkHeight">The reorg's fork height.</param>
    /// <param name="unrecorded">The fact predates the feed: the reversal keeps the books in line with the state the
    /// reorg changed, under a key of its own (never taken for the reversal of a later, recorded confirmation).</param>
    public static AccountingEventModel CreateReversal(AccountingEventModel original, DateTimeOffset occurredAt,
                                                      uint forkHeight, bool unrecorded = false)
    {
        ArgumentNullException.ThrowIfNull(original);
        if (original.BlockHeight is not { } height)
            throw new ArgumentException("Only an on-chain event (with a block height) can be reversed",
                                        nameof(original));

        var key = AccountingEventKeys.Reversal(original.EventKey, height);
        return new AccountingEventModel
        {
            EventKey = unrecorded ? key + UnrecordedKeySuffix : key,
            Kind = AccountingEventKind.Reversal,
            OccurredAt = occurredAt,
            BlockHeight = height,
            ChannelId = original.ChannelId,
            ShortChannelId = original.ShortChannelId,
            PaymentHash = original.PaymentHash,
            TxId = original.TxId,
            OutputIndex = original.OutputIndex,
            Counterparty = original.Counterparty,
            AmountMsat = -original.AmountMsat,
            FeeMsat = -original.FeeMsat,
            Finality = AccountingFinality.Confirmed,
            Details = AccountingDetailsCodec.Create((ReversesDetail, original.EventKey),
                                                   (OriginalKindDetail, original.Kind.ToString()),
                                                   (ForkHeightDetail,
                                                    forkHeight.ToString(CultureInfo.InvariantCulture)),
                                                   (UnrecordedDetail, unrecorded ? "true" : null))
        };
    }
}