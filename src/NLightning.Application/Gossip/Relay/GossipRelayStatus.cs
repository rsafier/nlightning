namespace NLightning.Application.Gossip.Relay;

/// <summary>
/// The gossip relay's state (NL-360, NL-375), for <c>describegraph</c>.
/// </summary>
/// <param name="IsRelayingOthers">Other nodes' gossip is relayed (<c>Gossip:RelayEnabled</c>, graph and sync).</param>
/// <param name="PendingMessages">Messages waiting for the connections' relay flushes.</param>
/// <param name="PausedConnections">Connections paused because their outbox is at its gossip cap.</param>
/// <param name="OutboxMessages">Gossip waiting in the connected peers' outboxes.</param>
/// <param name="OutboxBytes">Its size in bytes.</param>
public sealed record GossipRelayStatus(bool IsRelayingOthers, long PendingMessages, int PausedConnections,
                                       long OutboxMessages, long OutboxBytes);