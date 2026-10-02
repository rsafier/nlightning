namespace NLightning.Domain.Payments.Models;

using Channels.ValueObjects;
using Money;

/// <summary>
/// Per-call limits of one <c>IPaymentService.PayInvoiceAsync</c> (NL-270).
/// </summary>
/// <remarks>
/// A null limit means the node's default (<c>Node:Payments</c>): the fee limit max(0.5 %, 5000 msat) of the amount and
/// up to 16 parts. <see cref="Timeout"/> bounds both the wait and the retries: no new HTLC is offered after it.
/// </remarks>
public sealed record PayInvoiceOptions
{
    /// <summary>
    /// How long to wait for the outcome and to keep retrying. When it ends first, the payment is returned as it is
    /// stored; HTLCs still in flight resolve later.
    /// </summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// The most the payment may pay in routing fees, every part in flight together; null for the node's default.
    /// </summary>
    public LightningMoney? MaxFee { get; init; }

    /// <summary>
    /// The most HTLCs (parts) the payment may have in flight at once; 1 never splits. Null for the node's default.
    /// A split also needs an invoice with <c>basic_mpp</c>.
    /// </summary>
    public int? MaxParts { get; init; }

    /// <summary>
    /// The only channel of ours the payment may leave through (every part); null lets the route planner choose
    /// (NL-609, <c>payinvoice --out</c>).
    /// </summary>
    public ChannelId? OutgoingChannelId { get; init; }

    /// <summary>
    /// For a payment of one of our own invoices (a circular rebalance, NL-609): the only channel of ours the payment may
    /// come back in through; null lets the planner choose. Refused for an invoice of another node
    /// (<c>payinvoice --in</c>).
    /// </summary>
    public ChannelId? IncomingChannelId { get; init; }
}