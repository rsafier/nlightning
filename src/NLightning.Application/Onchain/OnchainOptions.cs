namespace NLightning.Application.Onchain;

using Domain.Onchain.Models;

/// <summary>
/// The knobs of the on-chain resolution (BOLT 5 plan D9), bound from <see cref="SectionName"/> by the host.
/// </summary>
public sealed class OnchainOptions
{
    /// <summary>The configuration section the daemon binds (<c>Node:Onchain</c>).</summary>
    public const string SectionName = "Node:Onchain";

    /// <summary>
    /// The depth at which an upstream HTLC is failed after its on-chain resolution (<c>Onchain:ReasonableDepth</c>,
    /// D9). For the resolvers; the executor does not use it.
    /// </summary>
    public uint ReasonableDepth { get; set; } = OutputResolutionFacts.DefaultReasonableDepth;

    /// <summary>
    /// The depth at which a resolution is irrevocable (BOLT 5: 100 blocks, B5-GEN-02, O6-T2). The channel is
    /// <c>Closed</c> once the funding spend and every output's resolution are this deep. Only lower it on regtest.
    /// </summary>
    public uint IrrevocableDepth { get; set; } = OutputResolutionFacts.DefaultIrrevocableDepth;
}