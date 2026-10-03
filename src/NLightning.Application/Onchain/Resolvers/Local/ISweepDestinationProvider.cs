namespace NLightning.Application.Onchain.Resolvers.Local;

using Domain.Channels.ValueObjects;

/// <summary>
/// Where a sweep of an output that pays us sends the funds (BOLT 5: "a convenient address"): a script of our wallet,
/// so the chain monitor credits the output as a deposit.
/// </summary>
public interface ISweepDestinationProvider
{
    /// <summary>
    /// The scriptPubKey of the sweep's single output, a fresh wallet address per call. Only for callers without a
    /// channel (the anchors fee-input change); everything of a channel uses
    /// <see cref="GetDestinationScriptAsync(ChannelId, CancellationToken)"/>.
    /// </summary>
    Task<byte[]> GetDestinationScriptAsync(CancellationToken cancellationToken);

    /// <summary>
    /// The scriptPubKey of the sweep's single output, one wallet address per channel for the process (NL-463, like the
    /// penalties): a channel's sweeps, its anchor sweep and its CPFP change reuse it across rounds and RBF rebuilds,
    /// so closing a channel costs one address, not one per attempt.
    /// </summary>
    Task<byte[]> GetDestinationScriptAsync(ChannelId channelId, CancellationToken cancellationToken);
}