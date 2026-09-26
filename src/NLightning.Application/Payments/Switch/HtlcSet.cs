namespace NLightning.Application.Payments.Switch;

using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Money;

/// <summary>
/// One HTLC of an <see cref="HtlcSet"/>: an incoming HTLC that pays us, locked in and held until the set is complete.
/// </summary>
/// <param name="ChannelId">The incoming channel.</param>
/// <param name="HtlcId">The incoming HTLC id.</param>
/// <param name="HtlcAmount">The HTLC's <c>amount_msat</c>.</param>
/// <param name="PartAmount">The onion's <c>amt_to_forward</c>, what counts towards <c>total_msat</c>.</param>
/// <param name="SharedSecret">The HTLC's onion shared secret, to encrypt a failure.</param>
internal sealed record HtlcSetPart(ChannelId ChannelId, ulong HtlcId, LightningMoney HtlcAmount,
                                   LightningMoney PartAmount, Secret SharedSecret)
{
    public (ChannelId, ulong) Key => (ChannelId, HtlcId);
}

/// <summary>
/// The HTLC set of a payment hash (BOLT 4 "Basic Multi-Part Payments"): the incoming HTLCs we hold for one of our
/// invoices until their <c>amt_to_forward</c> reach <see cref="TotalMsat"/>. Memory only: the parts are the persisted
/// incoming HTLCs, so after a restart the replayed lock-ins build the set again (and its timeout starts again).
/// </summary>
/// <remarks>Not thread-safe: <see cref="HtlcSwitch"/> uses it only under the payment hash lock.</remarks>
internal sealed class HtlcSet
{
    private readonly List<HtlcSetPart> _parts = [];

    public Hash PaymentHash { get; }

    /// <summary>The <c>total_msat</c> of the first part; every other part must carry the same.</summary>
    public LightningMoney TotalMsat { get; }

    /// <summary>When the first part arrived (in this process).</summary>
    public DateTimeOffset FirstPartAt { get; }

    /// <summary>The <c>mpp_timeout</c> timer, while the set is incomplete.</summary>
    public ITimer? Timer { get; set; }

    public IReadOnlyList<HtlcSetPart> Parts => _parts;

    /// <summary>The sum of the parts' <c>amt_to_forward</c>.</summary>
    public LightningMoney PartsSum => _parts.Aggregate(LightningMoney.Zero, (sum, p) => sum + p.PartAmount);

    /// <summary>The sum of the parts' <c>amount_msat</c> (what the invoice receives).</summary>
    public LightningMoney HtlcSum => _parts.Aggregate(LightningMoney.Zero, (sum, p) => sum + p.HtlcAmount);

    /// <summary>BOLT 4: the set may be fulfilled once the total <c>amt_to_forward</c> reaches <c>total_msat</c>.</summary>
    public bool IsComplete => _parts.Count > 0 && PartsSum >= TotalMsat;

    public HtlcSet(Hash paymentHash, LightningMoney totalMsat, DateTimeOffset firstPartAt)
    {
        PaymentHash = paymentHash;
        TotalMsat = totalMsat;
        FirstPartAt = firstPartAt;
    }

    /// <summary>Adds the part, or replaces the one with the same HTLC (a replayed lock-in).</summary>
    public void Add(HtlcSetPart part)
    {
        _parts.RemoveAll(p => p.Key == part.Key);
        _parts.Add(part);
    }

    public void Remove(HtlcSetPart part) => _parts.RemoveAll(p => p.Key == part.Key);

    /// <summary>Drops the parts for which <paramref name="keep"/> is false (failed or resolved elsewhere).</summary>
    public void Prune(Func<HtlcSetPart, bool> keep) => _parts.RemoveAll(p => !keep(p));
}