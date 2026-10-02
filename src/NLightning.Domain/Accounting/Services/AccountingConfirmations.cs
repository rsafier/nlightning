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

    /// <summary>The infix of the on-chain resolution writers' former re-emission keys (<c>{key}:re:{height}</c>, written
    /// before NL-613): a feed may still hold them, and they count as confirmations of their fact.</summary>
    private const string LegacyReemittedInfix = ":re:";

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

        // NL-613: a re-emission written under the former height key that still stands is the fact's confirmation
        if (existing.Any(e => e.Kind != AccountingEventKind.Reversal && IsLegacyReemission(baseKey, e.EventKey)
                           && !IsReversed(e, keys)))
            return null;

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
    /// The latest confirmation of the fact named by <paramref name="baseKey"/> that still stands (not reversed), or null:
    /// what a reorg's rewind reverses.
    /// </summary>
    /// <param name="baseKey">The fact's key (its first confirmation's).</param>
    /// <param name="existing">Every event whose key starts with <paramref name="baseKey"/>, as for
    /// <see cref="NextConfirmationKey"/>.</param>
    public static AccountingEventModel? FindStanding(string baseKey, IReadOnlyCollection<AccountingEventModel> existing)
    {
        ArgumentException.ThrowIfNullOrEmpty(baseKey);
        ArgumentNullException.ThrowIfNull(existing);

        var keys = existing.Select(e => e.EventKey).ToHashSet(StringComparer.Ordinal);
        return existing.Where(e => e.Kind != AccountingEventKind.Reversal && IsConfirmationKey(baseKey, e.EventKey)
                                && !IsReversed(e, keys))
                       .OrderBy(e => e.BlockHeight)
                       .LastOrDefault();
    }

    /// <summary>
    /// The latest confirmation of the fact named by <paramref name="baseKey"/> at <paramref name="height"/> (reversed or
    /// not), else its latest standing one, else its first: the event a block at that height recorded.
    /// </summary>
    public static AccountingEventModel? FindAt(string baseKey, uint? height,
                                               IReadOnlyCollection<AccountingEventModel> existing)
    {
        ArgumentException.ThrowIfNullOrEmpty(baseKey);
        ArgumentNullException.ThrowIfNull(existing);

        var confirmations = existing.Where(e => e.Kind != AccountingEventKind.Reversal
                                             && IsConfirmationKey(baseKey, e.EventKey))
                                    .ToList();
        return (height is { } at ? confirmations.LastOrDefault(e => e.BlockHeight == at) : null)
            ?? FindStanding(baseKey, confirmations)
            ?? confirmations.FirstOrDefault(e => string.Equals(e.EventKey, baseKey, StringComparison.Ordinal));
    }

    /// <summary>Whether <paramref name="eventKey"/> names a confirmation of the fact <paramref name="baseKey"/>: the key
    /// itself, a <see cref="AccountingEventKeys.Reconfirmed"/> generation or a former re-emission (NL-613).</summary>
    public static bool IsConfirmationKey(string baseKey, string eventKey)
    {
        ArgumentNullException.ThrowIfNull(baseKey);
        ArgumentNullException.ThrowIfNull(eventKey);

        if (string.Equals(eventKey, baseKey, StringComparison.Ordinal))
            return true;
        if (!eventKey.StartsWith(baseKey, StringComparison.Ordinal))
            return false;

        var rest = eventKey.AsSpan(baseKey.Length);
        return (rest.Length > 2 && rest.StartsWith(":c") && IsDigits(rest[2..]))
            || IsLegacyReemission(baseKey, eventKey);
    }

    private static bool IsLegacyReemission(string baseKey, string eventKey)
    {
        if (!eventKey.StartsWith(baseKey, StringComparison.Ordinal))
            return false;

        var rest = eventKey.AsSpan(baseKey.Length);
        return rest.Length > LegacyReemittedInfix.Length && rest.StartsWith(LegacyReemittedInfix)
                                                         && IsDigits(rest[LegacyReemittedInfix.Length..]);
    }

    private static bool IsDigits(ReadOnlySpan<char> text)
    {
        foreach (var c in text)
            if (c is < '0' or > '9')
                return false;
        return text.Length > 0;
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