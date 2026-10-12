namespace NLightning.Domain.Client.Responses;

using Gossip.Graph;

/// <summary>
/// The channels of the gossip graph, ordered by short channel id (<c>ClientCommand.ListGraphChannels</c>): both
/// policies, the verification and the spend height when the funding output was spent.
/// </summary>
public sealed record ListGraphChannelsClientResponse(IReadOnlyList<GraphChannel> Channels);