namespace NLightning.Application.Channels.DualFunding;

/// <summary>
/// The dual-funded open policy (BOLT 2 "Channel Establishment v2", splicing plan wave DF), bound from
/// <c>Node:DualFund</c> by the host. <c>option_dual_fund</c> itself is <c>Node:Features:DualFund</c>.
/// </summary>
public sealed class DualFundingOptions
{
    /// <summary>The configuration section.</summary>
    public const string SectionName = "Node:DualFund";

    /// <summary>
    /// What we contribute, in satoshis, as the accepter of a peer's <c>open_channel2</c>: 0 (the default) accepts
    /// without contributing (BOLT 2: "MAY respond with a funding_satoshis value of zero"). A contribution is capped at
    /// the opener's own contribution (<see cref="MatchOpenerContribution"/>), and the open goes on without ours when
    /// the wallet cannot fund it.
    /// </summary>
    public long AcceptContributionSat { get; set; }

    /// <summary>Cap our accepter contribution at the opener's <c>funding_satoshis</c> (default true).</summary>
    public bool MatchOpenerContribution { get; set; } = true;

    /// <summary>How long <c>OpenAsync</c>/<c>BumpAsync</c> wait for the negotiation to end (default 2 minutes).</summary>
    public TimeSpan OpenTimeout { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Whether an unconfirmed dual-funded open may be replaced by RBF (BOLT 2 "Fee bumping": our <c>BumpAsync</c> as
    /// the opener and the peer's <c>tx_init_rbf</c> in either role); default true (lane dfrbf, owner decision
    /// 2026-09-28). False refuses both: <c>BumpAsync</c> throws and the peer's <c>tx_init_rbf</c> gets <c>tx_abort</c>
    /// (BOLT 2: "MAY send tx_abort for any reason"). Each fully signed attempt is stored with the peer's signature of
    /// our first commitment and our share, so whichever attempt confirms, the channel follows it (NL-528). An RBF is
    /// refused anyway once an attempt has a confirmation or <c>channel_ready</c> was sent or received.
    /// </summary>
    public bool AllowRbf { get; set; } = true;

    /// <summary>The configuration problems, empty when valid.</summary>
    public IReadOnlyList<string> GetValidationErrors()
    {
        var errors = new List<string>();
        if (AcceptContributionSat < 0)
            errors.Add($"{SectionName}:AcceptContributionSat must not be negative");
        if (OpenTimeout <= TimeSpan.Zero)
            errors.Add($"{SectionName}:OpenTimeout must be positive");
        return errors;
    }
}