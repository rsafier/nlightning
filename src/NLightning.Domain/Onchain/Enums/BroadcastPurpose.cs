namespace NLightning.Domain.Onchain.Enums;

/// <summary>
/// What a transaction we broadcast is for (persisted as a byte: never renumber).
/// </summary>
public enum BroadcastPurpose : byte
{
    /// <summary>Not recorded (a caller that predates the purpose, such as <c>PublishAndWatchTransactionAsync</c>).</summary>
    Unspecified = 0,

    /// <summary>A channel funding transaction we funded (and, before <see cref="Splice"/> existed, a splice, NL-626).</summary>
    Funding = 1,

    /// <summary>A mutual close transaction.</summary>
    MutualClose = 2,

    /// <summary>Our own commitment transaction (fail the channel).</summary>
    LocalCommitment = 3,

    /// <summary>An HTLC-timeout or HTLC-success transaction of our commitment.</summary>
    HtlcTransaction = 4,

    /// <summary>A sweep of an output that pays us (to_local after its delay, to_remote, a second-level output).</summary>
    Sweep = 5,

    /// <summary>A claim of an HTLC output of the peer's commitment (timeout or preimage).</summary>
    HtlcClaim = 6,

    /// <summary>A penalty (justice) transaction spending a revoked commitment or its second-level outputs.</summary>
    Penalty = 7,

    /// <summary>
    /// A CPFP child spending our anchor of a commitment plus wallet inputs (BOLT 5 plan O7-T2, B5-FAIL-06): it pays for
    /// the commitment's package, and is replaced (RBF) until the commitment confirms.
    /// </summary>
    AnchorCpfp = 8,

    /// <summary>
    /// A payment from the on-chain wallet to an external address (<c>withdraw</c>, ClientCommand 25): no channel, and
    /// never bumped; rebroadcast until it confirms.
    /// </summary>
    WalletSend = 9,

    /// <summary>
    /// A peer's commitment transaction the mempool reactor saw in our bitcoind (the NL-381 hand-over): kept with its
    /// bytes so a restart does not lose the bump of our anchor child (NL-390). Not our broadcast (we did not sign
    /// it), but the monitor sends it again after every block until it confirms, which helps it propagate. Only the
    /// anchor CPFP service abandons one (evicted everywhere, or replaced by ours): the refusal rules never do.
    /// </summary>
    PeerCommitment = 10,

    /// <summary>
    /// A splice transaction (or one of its RBF attempts) we signed: it spends the channel's current funding output plus
    /// wallet inputs and creates the new funding output (NL-626). Rows saved before this value existed carry
    /// <see cref="Funding"/>; every rule that keys off <see cref="Funding"/> treats both the same (rebroadcast, the
    /// abandonment rule NL-294, the discard of a splice a commitment conflicts with), so only the label differs.
    /// </summary>
    Splice = 11,

    /// <summary>
    /// Our sweep of a confirmed commitment's anchors after 16 blocks (NL-611): stored with its fee so the books see the
    /// resolution of our anchor as ours. Anyone may take anchors first, so it is never bumped and the chain monitor may
    /// abandon it after permanent refusals, like a wallet spend (nothing of the channel's safety depends on it).
    /// </summary>
    AnchorSweep = 12,

    /// <summary>
    /// A transaction that spends leased wallet outputs together with outputs of others, signed by the wallet through
    /// LND's walletrpc PSBT methods (<c>FinalizePsbt</c> of a PSBT with foreign inputs) and published with
    /// <c>PublishTransaction</c> (NL-1186). Rebroadcast until it confirms and abandoned after permanent refusals like a
    /// <see cref="WalletSend"/>, but booked only through its wallet movements: its outputs to others are not all paid by
    /// us, so it is no withdrawal.
    /// </summary>
    WalletCollaborative = 13
}