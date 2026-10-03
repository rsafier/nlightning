namespace NLightning.Domain.Accounting.Enums;

/// <summary>
/// What an accounting event records (plan <c>docs/agents/ACCOUNTING_PLAN.md</c> §4). The values are persisted: never
/// renumber them.
/// </summary>
public enum AccountingEventKind
{
    /// <summary>One of our invoices was paid (BOLT 11, keysend or BOLT 12; one event per invoice, MPP included).</summary>
    InvoiceSettled = 1,

    /// <summary>One of our payments succeeded: amount and route fee left our channels.</summary>
    PaymentSucceeded = 2,

    /// <summary>One of our payments failed for good (informational: no money moved).</summary>
    PaymentFailed = 3,

    /// <summary>A forward we carried settled: the fee (incoming minus outgoing) is ours.</summary>
    ForwardSettled = 4,

    /// <summary>A forward whose two sides resolved differently on chain (a gain or a loss).</summary>
    ForwardLostOnchain = 5,

    /// <summary>
    /// One of our invoices was settled (its <see cref="InvoiceSettled"/> booked the HTLC's amount) but an incoming HTLC
    /// of it was then lost on chain: the peer took the output by its timeout, or we gave it up (NL-688). The HTLC's
    /// amount is a loss.
    /// </summary>
    InvoiceLostOnchain = 6,

    /// <summary>A channel's funding confirmed: our contribution moved from the wallet into the channel.</summary>
    ChannelFunded = 10,

    /// <summary>We pushed an amount to the peer at the open.</summary>
    PushSent = 11,

    /// <summary>The peer pushed an amount to us at the open.</summary>
    PushReceived = 12,

    /// <summary>A splice locked: our balance changed by the splice-in or splice-out.</summary>
    SpliceLocked = 13,

    /// <summary>A mutual close confirmed: our channel balance moved to the wallet, less our share of the fee.</summary>
    ChannelClosedMutual = 14,

    /// <summary>A unilateral or revoked close was classified: our channel balance moved to pending on-chain funds.</summary>
    ChannelForceClosed = 15,

    /// <summary>An output of a force close was resolved: pending funds reached the wallet (or were lost).</summary>
    OutputResolved = 16,

    /// <summary>We claimed a revoked commitment's outputs.</summary>
    PenaltyClaimed = 17,

    /// <summary>Funds lost to a breach.</summary>
    BreachLoss = 18,

    /// <summary>The fee of an anchor CPFP child that confirmed.</summary>
    AnchorCpfpFee = 19,

    /// <summary>The extra fee of a confirmed sweep replacement.</summary>
    SweepFeeBump = 20,

    /// <summary>
    /// We bought inbound liquidity (liquidity ads, NL-850): the seller's fee (its mining fee plus its service fee) moved
    /// from our channel balance to the peer's in the commitment of a dual-funded open, its RBF or a splice. An expense
    /// paid out of the channels; the funding's <see cref="ChannelFunded"/> or <see cref="SpliceLocked"/> books our
    /// contribution without it.
    /// </summary>
    LiquidityFeePaid = 21,

    /// <summary>
    /// We sold liquidity (liquidity ads, NL-850): the buyer's fee (our mining fee refund plus our service fee) moved
    /// from the peer's channel balance to ours in the commitment of a dual-funded open, its RBF or a splice. Income into
    /// the channels; the funding's <see cref="ChannelFunded"/> or <see cref="SpliceLocked"/> books our contribution
    /// without it.
    /// </summary>
    LiquidityFeeEarned = 22,

    /// <summary>An external deposit to our on-chain wallet confirmed.</summary>
    WalletReceived = 30,

    /// <summary>An on-chain withdrawal of ours confirmed.</summary>
    WalletSent = 31,

    /// <summary>A wallet output of ours was spent in a confirmed block (wallet history, NL-603).</summary>
    WalletOutputSpent = 32,

    /// <summary>The opening balances posted by the backfill (state that predates the feed).</summary>
    OpeningBalance = 50,

    /// <summary>The negation of an earlier event whose block was disconnected (reorg).</summary>
    Reversal = 60
}