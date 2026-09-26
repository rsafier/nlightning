namespace NLightning.Application.Onchain.Anchors;

using Domain.Channels.ValueObjects;

/// <summary>
/// Gets our anchor commitments mined (BOLT 5 plan O7-T2, B5-FAIL-06): a CPFP child through our anchor when the
/// commitment alone pays less than the estimate for its deadline, replaced (RBF) while it stays unconfirmed, and the
/// wallet inputs released once the commitment (or a child) confirmed or can no longer confirm; the anchors of our
/// confirmed commitment are swept once anyone may spend them, when that pays for itself.
/// </summary>
public interface IAnchorCpfpService
{
    /// <summary>Subscribes to new blocks (one round per block). <c>ChannelFailureService.Start</c> calls it.</summary>
    void Start();

    /// <summary>Unsubscribes and stops the background rounds.</summary>
    void Stop();

    /// <summary>
    /// Runs the round of one channel now: called by the fail-the-channel path right after it published (or tried to
    /// publish) the channel's commitment, outside every lock. A channel without anchors is skipped. Never throws for a
    /// failed round (it is logged and retried at the next block).
    /// </summary>
    Task OnCommitmentBroadcastAsync(ChannelId channelId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs one round over every loaded anchor channel that is <c>Failed</c> or <c>OnchainResolving</c> at tip
    /// <paramref name="height"/> (what a new block triggers after <see cref="Start"/>).
    /// </summary>
    Task RunOnceAsync(uint height, CancellationToken cancellationToken = default);
}