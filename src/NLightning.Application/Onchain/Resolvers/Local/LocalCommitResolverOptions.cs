namespace NLightning.Application.Onchain.Resolvers.Local;

using Domain.Onchain.Models;

/// <summary>
/// Depths used by <see cref="LocalCommitResolver"/> (BOLT 5 plan D9). The defaults are the plan's; the host binds
/// <c>Onchain</c> when it wants others (regtest proofs).
/// </summary>
public sealed class LocalCommitResolverOptions
{
    /// <summary>
    /// How deep the transaction that settles one of our offered HTLCs without a preimage (our HTLC-timeout, or our
    /// commitment when the HTLC has no output in it) must be before the upstream HTLC is failed
    /// (<c>Onchain:ReasonableDepth</c>, default 6, B5-LCL-LO-03/04).
    /// </summary>
    public uint ReasonableDepth { get; set; } = OutputResolutionFacts.DefaultReasonableDepth;

    /// <summary>The depth at which a resolution is irrevocable (100, B5-GEN-02).</summary>
    public uint IrrevocableDepth { get; set; } = OutputResolutionFacts.DefaultIrrevocableDepth;
}