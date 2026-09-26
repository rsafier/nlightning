namespace NLightning.Application.Onchain;

using Domain.Onchain.Fees;
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

    /// <summary>
    /// The fee rules of our sweeps, claims and penalties (<c>Node:Onchain:FeePolicy</c>, BOLT 5 plan §3.7): the
    /// <see cref="SweepFeePolicy"/> singleton the resolvers and the <c>SweepScheduler</c> share.
    /// </summary>
    public SweepFeePolicyOptions FeePolicy { get; set; } = new();

    /// <summary>
    /// Blocks to wait for the peer's commitment to confirm again after a reorg took it out of the chain before we
    /// broadcast our own latest commitment (<c>Node:Onchain:ReorgGraceBlocks</c>, BOLT 5 plan §3.8, O6-T3).
    /// </summary>
    public uint ReorgGraceBlocks { get; set; } = 6;

    /// <summary>
    /// The reaction to unconfirmed spends of our channels' outputs (<c>Node:Onchain:Mempool</c>, BOLT 5 plan O8,
    /// NL-098).
    /// </summary>
    public OnchainMempoolOptions Mempool { get; set; } = new();
}

/// <summary>
/// The knobs of the mempool reaction (BOLT 5 plan O8): preimages read from unconfirmed transactions and penalties
/// prepared for revoked commitments before they confirm.
/// </summary>
public sealed class OnchainMempoolOptions
{
    /// <summary>
    /// React to unconfirmed spends (default true). Off, the node waits for blocks as before; the chain monitor's own
    /// switch is <c>Bitcoin:WatchMempool</c>.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Blocks in a row a revoked commitment we prepared penalties for may be unknown to bitcoind (neither in its mempool
    /// nor recorded as the channel's close) before those penalties are abandoned (default 3): it was evicted or
    /// replaced. A penalty stays valid only while its commitment can still confirm.
    /// </summary>
    public uint EvictionGraceBlocks { get; set; } = 3;
}