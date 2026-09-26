namespace NLightning.Domain.Routing.Pathfinding;

/// <summary>
/// The live state of one of our channels.
/// </summary>
/// <param name="IsUsable">Open, reestablished and not shutting down.</param>
/// <param name="SpendableMsat">The most a new outgoing HTLC may carry now.</param>
/// <param name="HtlcMinimumMsat">The peer's <c>htlc_minimum_msat</c>.</param>
public sealed record LocalChannelState(bool IsUsable, ulong SpendableMsat, ulong HtlcMinimumMsat = 0);