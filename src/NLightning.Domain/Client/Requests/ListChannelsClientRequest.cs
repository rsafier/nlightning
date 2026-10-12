namespace NLightning.Domain.Client.Requests;

using Crypto.ValueObjects;

/// <summary>
/// Lists the node's channels.
/// </summary>
public sealed class ListChannelsClientRequest
{
    /// <summary>
    /// When set, only the channels with this peer are listed.
    /// </summary>
    public CompactPubKey? PeerId { get; init; }
}