namespace NLightning.Domain.Payments.Models;

using Channels.ValueObjects;
using Enums;

/// <summary>
/// The filters and page of a <c>listforwards</c> query (NL-597); every member is optional except the page, which the
/// caller guards (1 to <c>ClientRequestGuards.MaxPageSize</c>).
/// </summary>
/// <param name="Skip">How many of the newest forwards to skip.</param>
/// <param name="Take">The most forwards to return.</param>
/// <param name="Since">Only forwards created at or after this time, or null.</param>
/// <param name="Until">Only forwards created at or before this time, or null.</param>
/// <param name="Status">Only forwards in this state, or null.</param>
/// <param name="ChannelId">Only forwards whose incoming or outgoing channel is this one, or null. Matched together
/// with <paramref name="ChannelScid"/>.</param>
/// <param name="ChannelScid">The requested <c>short_channel_id</c> of the channel filter, matched against the outgoing
/// side besides <paramref name="ChannelId"/>, or null.</param>
public sealed record ForwardCircuitListQuery(int Skip, int Take, DateTimeOffset? Since = null,
                                             DateTimeOffset? Until = null, ForwardCircuitStatus? Status = null,
                                             ChannelId? ChannelId = null, ShortChannelId? ChannelScid = null);

/// <summary>
/// The aggregates of one <see cref="ForwardCircuitListQuery"/> over the whole filtered set (not just the page).
/// </summary>
/// <param name="Pending">Circuits not offered yet.</param>
/// <param name="Offered">Circuits whose outgoing HTLC is live.</param>
/// <param name="Fulfilled">Circuits whose outgoing HTLC was fulfilled.</param>
/// <param name="Failed">Circuits that failed (or whose offer was refused).</param>
/// <param name="FulfilledFeesMsat">The fees earned (incoming − outgoing) of the <paramref name="Fulfilled"/>
/// circuits, in msat.</param>
public readonly record struct ForwardCircuitTotals(int Pending, int Offered, int Fulfilled, int Failed,
                                                   long FulfilledFeesMsat)
{
    public int Total => Pending + Offered + Fulfilled + Failed;
}