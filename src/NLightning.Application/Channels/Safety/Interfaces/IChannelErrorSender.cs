namespace NLightning.Application.Channels.Safety.Interfaces;

using Domain.Crypto.ValueObjects;
using Domain.Protocol.Messages;

/// <summary>
/// Sends a channel's <c>error</c> to its peer outside a message handler (the fail-the-channel path of
/// <see cref="IChannelFailureService"/>), without disconnecting.
/// </summary>
public interface IChannelErrorSender
{
    /// <summary>Sends <paramref name="error"/> if <paramref name="peer"/> is connected; never throws.</summary>
    /// <returns>True when it was handed to the connection.</returns>
    Task<bool> TrySendAsync(CompactPubKey peer, ErrorMessage error);
}