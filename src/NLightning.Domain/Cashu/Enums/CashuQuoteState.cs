namespace NLightning.Domain.Cashu.Enums;

/// <summary>
/// Where a Cashu quote stands in the payment processor (NL-997). Values only grow, except that a reorg may take an
/// on-chain melt from <see cref="Paid"/> back to <see cref="Pending"/>.
/// </summary>
public enum CashuQuoteState : byte
{
    /// <summary>Quoted (a melt) or handed out (a mint quote's address); nothing was sent.</summary>
    Created = 1,

    /// <summary>
    /// A melt is being sent: the row is saved before the payment starts, so a crash in between leaves a trace (the
    /// mint is answered <c>PENDING</c>, never <c>UNPAID</c>, and must not pay the quote twice).
    /// </summary>
    Dispatching = 2,

    /// <summary>A melt was sent (a payment in flight, or a transaction waiting for its confirmations).</summary>
    Pending = 3,

    /// <summary>A melt was paid.</summary>
    Paid = 4,

    /// <summary>A melt failed for good; the mint may try the quote again.</summary>
    Failed = 5
}