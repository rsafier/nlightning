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
    /// SP-R-01: the highest <c>funding_feerate_perkw</c> of a peer's <c>splice_init</c> we accept, and the highest
    /// <c>feerate</c> of a peer's <c>tx_init_rbf</c> (above it: <c>tx_abort</c>, BOLT 2 "MAY send tx_abort for any
    /// reason"). As acceptor of a splice we pay nothing (D10), but in a peer's RBF of a splice we contributed to we pay
    /// our part of the fee at the peer's feerate (from our channel balance for a splice-out, from our change for a
    /// splice-in), so the cap also bounds what the peer can make us pay (see also
    /// <see cref="MaxRbfFeeShareSatoshis"/>).
    /// </summary>
    public uint MaxFeeratePerKw { get; set; } = 250_000;

    /// <summary>
    /// Wave SPR review: the most our share of the fee of a peer's RBF attempt may be (the fee of our own inputs and
    /// outputs at the peer's feerate). Above it, or above half of what our contribution moves (our splice-out outputs,
    /// our splice-in amount), we contribute nothing to that attempt instead (BOLT 2 fee bumping: a node "sets their sats
    /// to zero" rather than fail the RBF). Null leaves only the half-of-what-it-moves rule. Our own bumps are bounded
    /// by <c>SpliceBumpRequest.MaxFeeSatoshis</c> instead.
    /// </summary>
    public ulong? MaxRbfFeeShareSatoshis { get; set; } = 50_000;

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
    /// SPR-T1 (NL-520): a peer's <c>tx_init_rbf</c> gets <c>tx_abort</c> while the latest attempt was "created
    /// recently" (BOLT 2 SHOULD, "and wait for the previous RBF attempt to confirm";
    /// <c>SpliceRules.IsLastAttemptRecent</c>): until at least this many new blocks were processed since that attempt
    /// was stored, so it had a chance to confirm (about 10 minutes on mainnet, 30 seconds on Mutinynet). Default 1;
    /// 0 turns the recency rule off. Not used while <see cref="MinRbfInterval"/> is set.
    /// </summary>
    public uint MinRbfBlocks { get; set; } = 1;

    /// <summary>
    /// SPR-T1 (NL-520): an optional wall-clock recency rule that replaces <see cref="MinRbfBlocks"/> when set: an attempt
    /// younger than this is "created recently". Null (the default) uses the block rule; <see cref="TimeSpan.Zero"/>
    /// turns the recency rule off. Before NL-520 this was the rule itself, with a default of one minute; a configured
    /// value keeps working (test networks may set a few seconds to rehearse RBF quickly).
    /// </summary>
    public TimeSpan? MinRbfInterval { get; set; }

    /// <summary>
    /// SPR-T3: RBF our unconfirmed splice automatically once it has waited this many blocks since its latest attempt
    /// was broadcast (at the fee service's estimate, at least the IT-RBF-01 minimum); null turns the auto-bump off (the
    /// default: operators bump with <c>bumpsplice</c>). Wave SPR contract; implemented by lane SPR-B
    /// (<c>ISpliceAutoBumper</c>).
    /// </summary>
    public uint? AutoBumpAfterBlocks { get; set; }

    /// <summary>
    /// SPR-T3: the highest feerate the auto-bump proposes on its own (an operator's spending limit, unlike
    /// <see cref="MaxFeeratePerKw"/>, which bounds a peer's <c>splice_init</c>). A fee estimate above it is clamped to
    /// it; a splice whose IT-RBF-01 minimum is already above it is left to <c>bumpsplice</c>. Default 25,000 sat/kw
    /// (100 sat/vB).
    /// </summary>
    public uint AutoBumpMaxFeeratePerKw { get; set; } = 25_000;

    /// <summary>
    /// SPR-T3: the most our side may pay for one auto-bump attempt (<see cref="Domain.Channels.Splicing.Models
    /// .SpliceBumpRequest.MaxFeeSatoshis"/>); the splice service refuses a bump above it before anything is sent.
    /// Default 100,000 sat; null = no cap beyond <see cref="AutoBumpMaxFeeratePerKw"/>.
    /// </summary>
    public ulong? AutoBumpMaxFeeSat { get; set; } = 100_000;

    /// <summary>
    /// SPR-T3: how long an auto-bump round waits for one bump's negotiation before it moves on (the negotiation is not
    /// cancelled; its channel is skipped until it ends), so a slow peer never delays another channel's bump. Default
    /// 120 s, as the <c>bumpsplice</c> command's wait.
    /// </summary>
    public TimeSpan AutoBumpMaxWait { get; set; } = TimeSpan.FromSeconds(120);
}