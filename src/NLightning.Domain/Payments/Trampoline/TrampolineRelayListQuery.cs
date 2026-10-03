namespace NLightning.Domain.Payments.Trampoline;

using Channels.ValueObjects;

/// <summary>
/// One page of trampoline relays, newest first (NL-875), filtered like <c>ForwardCircuitListQuery</c>: by creation
/// time (inclusive bounds), status, and an incoming channel (a relay matches when one of its parts came in on it).
/// </summary>
/// <param name="Skip">Relays to skip.</param>
/// <param name="Take">Relays to return at most.</param>
/// <param name="Since">Created at or after.</param>
/// <param name="Until">Created at or before.</param>
/// <param name="Status">Only relays in this status.</param>
/// <param name="IncomingChannelId">Only relays with a part on this channel.</param>
public sealed record TrampolineRelayListQuery(int Skip, int Take, DateTimeOffset? Since = null,
                                              DateTimeOffset? Until = null, TrampolineRelayStatus? Status = null,
                                              ChannelId? IncomingChannelId = null);