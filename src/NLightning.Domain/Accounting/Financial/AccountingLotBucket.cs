namespace NLightning.Domain.Accounting.Financial;

/// <summary>
/// The bucket that holds a cost-basis lot (<c>AccountingLots.Account</c>, NL-657, NL-674): the node's own asset buckets
/// (their values are the <c>AccountRole</c> numbers of the operational accounts), the rebalance in transit and the sats
/// held outside the node. A lot with no bucket (null) is a lot of the node-wide pool of a book projected before lots were
/// kept per bucket, or an imported lot (D-A9) not yet taken by its opening balance. The values are persisted: never
/// renumber them.
/// </summary>
public enum AccountingLotBucket
{
    /// <summary>Our local balances of the channels (<c>assets:lightning:channels</c>).</summary>
    Channels = 1,

    /// <summary>Force-closed funds not yet in the wallet (<c>assets:onchain:pending</c>).</summary>
    Pending = 2,

    /// <summary>Confirmed wallet outputs (<c>assets:onchain:wallet</c>).</summary>
    Wallet = 3,

    /// <summary>The on-chain clearing account (<c>assets:onchain:clearing</c>): value between the wallet's events and the
    /// events that spend or fill it.</summary>
    Clearing = 4,

    /// <summary>A rebalance in transit: the sats the paying half moved out of the channels and the receiving half brings
    /// back (the equity transfer account of a self-payment, D-A12).</summary>
    Rebalance = 22,

    /// <summary>
    /// Sats still ours but held outside the node (NL-674, D-A12 as amended 2026-10-02): a withdrawal classified to an
    /// equity transfer account moves its lots here at cost, and a deposit classified as a transfer back takes them out at
    /// their original cost and acquisition time. It never lends lots to another bucket.
    /// </summary>
    HeldOutside = 60
}