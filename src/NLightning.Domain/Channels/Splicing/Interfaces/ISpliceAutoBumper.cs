namespace NLightning.Domain.Channels.Splicing.Interfaces;

using Models;

/// <summary>
/// The splice fee-bump target of wave SPR (SPR-T3, lane SPR-B): on each block, RBFs every pending splice of ours that
/// has waited <c>Splice:AutoBumpAfterBlocks</c> blocks since its latest attempt, through
/// <see cref="ISpliceService.BumpAsync(SpliceBumpRequest, CancellationToken)"/> at the fee service's estimate (at least
/// the IT-RBF-01 minimum). Unlike the <c>SweepScheduler</c>'s sweeps, a splice cannot be re-signed alone: every bump is
/// a new interactive negotiation with the peer (quiescence, <c>tx_init_rbf</c>), so a peer that is offline or refuses
/// leaves the splice as it is until the next round.
/// </summary>
/// <remarks>
/// Implemented by an Application singleton that the host starts after the chain monitor and the peer manager. Never
/// called under a channel's lock (the bump takes it). Off when <c>Splice:AutoBumpAfterBlocks</c> is null.
/// </remarks>
public interface ISpliceAutoBumper
{
    /// <summary>
    /// One round at the chain tip <paramref name="height"/>: the bumps started, with where each got. Idempotent per
    /// height; a channel whose bump is already running is skipped.
    /// </summary>
    Task<IReadOnlyList<SpliceResult>> BumpStaleSplicesAsync(uint height, CancellationToken cancellationToken = default);
}