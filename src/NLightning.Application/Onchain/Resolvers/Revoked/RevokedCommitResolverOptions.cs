namespace NLightning.Application.Onchain.Resolvers.Revoked;

using Domain.Onchain.Fees;
using Domain.Onchain.Models;

/// <summary>
/// Settings of <see cref="RevokedCommitResolver"/> (BOLT 5 plan §3.7, D9, D10). Settable, not init-only: the
/// configuration binding generator skips init-only members (NL-338).
/// </summary>
public sealed record RevokedCommitResolverOptions
{
    /// <summary>The depth at which an upstream HTLC is failed (<c>Onchain:ReasonableDepth</c>, D9).</summary>
    public uint ReasonableDepth { get; set; } = OutputResolutionFacts.DefaultReasonableDepth;

    /// <summary>The depth at which a resolution is irrevocable (100, B5-GEN-02).</summary>
    public uint IrrevocableDepth { get; set; } = OutputResolutionFacts.DefaultIrrevocableDepth;

    /// <summary>The fee rules (floor, caps, <c>security_delay</c> = 18 for the split, B5-REV-08).</summary>
    public SweepFeePolicyOptions FeePolicy { get; set; } = new();
}