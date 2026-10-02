namespace NLightning.Domain.Accounting.Models;

using Bitcoin.ValueObjects;
using Channels.ValueObjects;
using Crypto.ValueObjects;
using Enums;

/// <summary>
/// One fact about our money, written in the same save as the state change it records (the accounting feed, plan
/// <c>docs/agents/ACCOUNTING_PLAN.md</c> §3-§4).
/// </summary>
/// <remarks>
/// <para>Amounts are signed millisatoshi from our point of view (<see cref="AmountMsat"/> &gt; 0 means more of our
/// money), which is why they are <c>long</c> and not <c>LightningMoney</c>; <see cref="FeeMsat"/> is never negative.
/// </para>
/// <para><see cref="EventKey"/> names the fact: every writer derives it from the fact alone
/// (<see cref="Constants.AccountingEventKeys"/>), so a fact written twice carries the same key and the sealer marks the
/// second row <see cref="AccountingEventFlags.Duplicate"/>.</para>
/// <para><see cref="LedgerSeq"/> and <see cref="Hash"/> are null until the sealer assigns them in commit order;
/// readers only ever read sealed rows.</para>
/// </remarks>
public sealed class AccountingEventModel
{
    /// <summary>The storage id (0 until saved). Insert order, not commit order: never a cursor.</summary>
    public long Id { get; init; }

    public required string EventKey { get; init; }
    public required AccountingEventKind Kind { get; init; }

    /// <summary>When the fact happened (UTC, sub-second).</summary>
    public required DateTimeOffset OccurredAt { get; init; }

    /// <summary>The block of an on-chain fact.</summary>
    public uint? BlockHeight { get; init; }

    public ChannelId? ChannelId { get; init; }
    public ShortChannelId? ShortChannelId { get; init; }
    public Hash? PaymentHash { get; init; }
    public TxId? TxId { get; init; }
    public uint? OutputIndex { get; init; }

    /// <summary>The other node of the fact (the channel peer, the payee or the payer when known).</summary>
    public CompactPubKey? Counterparty { get; init; }

    /// <summary>Our balance change in msat (signed).</summary>
    public long AmountMsat { get; init; }

    /// <summary>The fee we paid in msat (never negative).</summary>
    public long FeeMsat { get; init; }

    public AccountingFinality Finality { get; init; }
    public AccountingEventFlags Flags { get; init; }

    /// <summary>Kind-specific details (string pairs, kept sorted by key).</summary>
    public IReadOnlyDictionary<string, string> Details { get; init; } = s_emptyDetails;

    /// <summary>The dense position in commit order, assigned by the sealer.</summary>
    public long? LedgerSeq { get; init; }

    /// <summary>SHA-256 over the previous sealed event's hash and this event's canonical bytes.</summary>
    public byte[]? Hash { get; init; }

    public bool IsSealed => LedgerSeq is not null;

    private static readonly IReadOnlyDictionary<string, string> s_emptyDetails =
        new SortedDictionary<string, string>(StringComparer.Ordinal);
}