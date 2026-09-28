namespace NLightning.Application.Channels.Splicing;

/// <summary>
/// Splice policy (splicing plan §3.5, D5, D8, D10), bound from the <c>Splice</c> configuration section by the host.
/// </summary>
public sealed class SpliceOptions
{
    /// <summary>The configuration section.</summary>
    public const string SectionName = "Splice";

    /// <summary>
    /// D5: use a new funding key for each splice (BOLT 2 SHOULD), derived from the channel's keys and the funding's key
    /// index (<c>ILightningSigner.GetFundingPubKey</c>). False keeps the current funding key (allowed; for interop
    /// debugging).
    /// </summary>
    public bool RotateFundingKey { get; set; } = true;

    /// <summary>
    /// SP-R-01: the lowest <c>funding_feerate_perkw</c> of a peer's <c>splice_init</c> we accept (below it:
    /// <c>tx_abort</c>). BOLT 3's floor.
    /// </summary>
    public uint MinFeeratePerKw { get; set; } = 253;

    /// <summary>
    /// SP-R-01: the highest <c>funding_feerate_perkw</c> of a peer's <c>splice_init</c> we accept (above it:
    /// <c>tx_abort</c>). As acceptor we pay nothing (D10), so this only guards against a feerate the initiator could
    /// not mean.
    /// </summary>
    public uint MaxFeeratePerKw { get; set; } = 250_000;

    /// <summary>
    /// Set <c>require_confirmed_inputs</c> in our <c>splice_init</c>/<c>splice_ack</c> (SP-S-02, SP-TX-04): the peer's
    /// inputs must be confirmed. Off by default (as CLN and Eclair).
    /// </summary>
    public bool RequireConfirmedInputs { get; set; }

    /// <summary>
    /// SPR-T2: the most RBF attempts we start for one splice (<c>bumpsplice</c> and the auto-bump); keeps the
    /// <c>start_batch</c> small (BOLT 2 caps a batch at 20, i.e. 19 pending attempts). A peer's RBF is judged by the
    /// BOLT 2 rules only (<c>SpliceRules.CheckReceiveRbf</c>). Wave SPR contract; enforced by lane SPR-A.
    /// </summary>
    public int MaxRbfAttempts { get; set; } = 8;

    /// <summary>
    /// SPR-T1: an attempt younger than this is "created recently": a peer's <c>tx_init_rbf</c> then gets
    /// <c>tx_abort</c> (BOLT 2 SHOULD, <c>SpliceRbfConditions.LastAttemptIsRecent</c>). Wave SPR contract; the default
    /// may be tuned by lane SPR-A.
    /// </summary>
    public TimeSpan MinRbfInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// SPR-T3: RBF our unconfirmed splice automatically once it has waited this many blocks since its latest attempt
    /// was broadcast (at the fee service's estimate, at least the IT-RBF-01 minimum); null turns the auto-bump off (the
    /// default: operators bump with <c>bumpsplice</c>). Wave SPR contract; implemented by lane SPR-B
    /// (<c>ISpliceAutoBumper</c>).
    /// </summary>
    public uint? AutoBumpAfterBlocks { get; set; }
}