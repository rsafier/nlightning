namespace NLightning.Application.Payments.Send;

using Domain.Channels.ValueObjects;
using Domain.Gossip.Interfaces;

/// <summary>
/// The default <see cref="IGossipScidRefresher"/> until the gossip sync manager is registered (BOLT 7 plan G3-T5):
/// drops every request.
/// </summary>
public sealed class NullGossipScidRefresher : IGossipScidRefresher
{
    /// <inheritdoc />
    public bool RequestRefresh(ShortChannelId shortChannelId) => false;
}