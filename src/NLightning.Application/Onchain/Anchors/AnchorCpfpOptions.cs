namespace NLightning.Application.Onchain.Anchors;

/// <summary>
/// The knobs of the anchor CPFP (BOLT 5 plan O7-T2), bound from <see cref="SectionName"/> by the host. The fee rules
/// themselves (targets, RBF interval and increments, caps) are the shared <c>SweepFeePolicy</c>'s
/// (<c>Node:Onchain:FeePolicy</c>).
/// </summary>
public sealed class AnchorCpfpOptions
{
    /// <summary>The configuration section (<c>Node:Onchain:Anchors</c>).</summary>
    public const string SectionName = "Node:Onchain:Anchors";

    /// <summary>Fee-bump our unconfirmed anchor commitments through their anchor (default true).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// The confirmation target of a commitment without HTLCs (default 36, the sweep target): nothing on it has a
    /// deadline, but the child still gets it mined when its own feerate is below the estimate.
    /// </summary>
    public uint NoDeadlineConfTarget { get; set; } = 36;

    /// <summary>
    /// Largest share of the value we have at stake on the commitment (our <c>to_local</c> plus every HTLC) a child may
    /// pay, in per-mille (default 500 = 50 %). The fee is capped there, never refused.
    /// </summary>
    public uint MaxFeePerMilleOfStake { get; set; } = 500;

    /// <summary>
    /// Floor of that cap in satoshis (default 20,000): a commitment carrying little of ours may still need a child to
    /// get its HTLCs resolved in time.
    /// </summary>
    public ulong MinFeeCapSat { get; set; } = 20_000;

    /// <summary>
    /// Sweep the anchors of our confirmed commitment once anyone may spend them (16 blocks), when that pays for itself
    /// (default true; see <see cref="AnchorCpfpPolicy.DecideAnchorSweep"/>).
    /// </summary>
    public bool SweepAnchors { get; set; } = true;
}