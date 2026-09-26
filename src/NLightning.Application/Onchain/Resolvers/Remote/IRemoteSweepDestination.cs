namespace NLightning.Application.Onchain.Resolvers.Remote;

using Domain.Channels.ValueObjects;

/// <summary>
/// Where our sweeps and claims of a peer commitment pay to: a scriptPubKey of our wallet, so the monitor credits the
/// output as a deposit (D5).
/// </summary>
public interface IRemoteSweepDestination
{
    /// <summary>The destination scriptPubKey for a sweep of <paramref name="channelId"/>'s outputs.</summary>
    Task<byte[]> GetScriptAsync(ChannelId channelId, CancellationToken cancellationToken);
}