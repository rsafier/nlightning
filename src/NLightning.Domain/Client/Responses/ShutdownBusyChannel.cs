namespace NLightning.Domain.Client.Responses;

/// <summary>
/// One channel that kept a <c>shutdown --wait</c> from going idle, or was busy when <c>--force</c> stopped the node
/// (NL-592).
/// </summary>
/// <param name="ChannelId">The channel id, as <c>listchannels</c> prints it.</param>
/// <param name="HtlcsInFlight">Its HTLCs in flight.</param>
/// <param name="Negotiating">Whether a splice, RBF or dual-funded open negotiation runs on it, or it is quiescent
/// for one, or its open is not signed yet.</param>
public sealed record ShutdownBusyChannel(string ChannelId, int HtlcsInFlight, bool Negotiating);