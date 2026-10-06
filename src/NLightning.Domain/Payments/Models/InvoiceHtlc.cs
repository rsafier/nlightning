namespace NLightning.Domain.Payments.Models;

using Channels.ValueObjects;

/// <summary>Where an HTLC that paid one of our invoices stands (LND's <c>InvoiceHTLCState</c>, NL-1167).</summary>
public enum InvoiceHtlcState : byte
{
    /// <summary>Locked in and held (a hold invoice waiting for its settle).</summary>
    Accepted = 0,

    /// <summary>Fulfilled: the invoice settled.</summary>
    Settled = 1,

    /// <summary>Failed back: the invoice was canceled.</summary>
    Canceled = 2
}

/// <summary>
/// One HTLC of the set that paid (or is held for) one of our invoices (NL-1167), recorded on the invoice when the set
/// is held or settles: the HTLC rows themselves are pruned once settled.
/// </summary>
/// <param name="ShortChannelId">The channel it arrived on, as its short channel id then (default when unknown).</param>
/// <param name="HtlcId">Its <c>update_add_htlc</c> id.</param>
/// <param name="AmountMsat">Its amount.</param>
/// <param name="AcceptHeight">The block height when the set completed.</param>
/// <param name="AcceptTime">When the set completed.</param>
/// <param name="ResolveTime">When it was settled or canceled; null while held.</param>
/// <param name="ExpiryHeight">Its <c>cltv_expiry</c> (0 when it was not known any more).</param>
/// <param name="State">Where it stands.</param>
/// <param name="MppTotalMsat">The set's <c>total_msat</c>.</param>
public sealed record InvoiceHtlc(ShortChannelId ShortChannelId, ulong HtlcId, ulong AmountMsat, uint AcceptHeight,
                                 DateTimeOffset AcceptTime, DateTimeOffset? ResolveTime, uint ExpiryHeight,
                                 InvoiceHtlcState State, ulong MppTotalMsat);