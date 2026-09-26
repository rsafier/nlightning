namespace NLightning.Application.Onchain.Resolvers.Remote;

using Domain.Onchain.Models;

/// <summary>
/// Settings of <see cref="RemoteCommitResolver"/> (the host binds them from the <c>Onchain</c> section when it wires
/// the on-chain watcher).
/// </summary>
public sealed class RemoteResolutionOptions
{
    /// <summary>D9: the depth at which an HTLC we offered that was resolved on chain without a preimage is failed
    /// upstream (<c>Onchain:ReasonableDepth</c>).</summary>
    public uint ReasonableDepth { get; set; } = OutputResolutionFacts.DefaultReasonableDepth;

    /// <summary>B5-GEN-02: the depth at which a resolution is irrevocable.</summary>
    public uint IrrevocableDepth { get; set; } = OutputResolutionFacts.DefaultIrrevocableDepth;
}