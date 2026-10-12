namespace NLightning.Application.Gossip.Announcements.Interfaces;

using Domain.Protocol.Payloads;

/// <summary>
/// Our <c>node_announcement</c> (BOLT 7; plan §3.2 step 6, G1-T6): alias and color from <c>Node:Alias</c>/
/// <c>Node:Color</c>, the addresses of <c>Gossip:AnnounceAddresses</c> (none by default), our node features. It is made
/// only once we have at least one announced channel (other nodes ignore a <c>node_announcement</c> of a node they know
/// no channel of), signed again when a field changed or it got older than
/// <c>Gossip:NodeAnnouncementRefreshInterval</c>, and handed to the graph and the relay.
/// </summary>
/// <remarks>
/// Its <c>timestamp</c> is strictly greater than that of any announcement we made before, across restarts: the last
/// one is stored in the graph's node table under our own node id, and saved before the new one goes out.
/// </remarks>
public interface INodeAnnouncementService
{
    /// <summary>The announcement made last in this process, or null.</summary>
    NodeAnnouncementPayload? Current { get; }

    /// <summary>
    /// Makes (or refreshes) and publishes our announcement when we have an announced channel. Without one nothing is
    /// made and null is returned; with a current one whose fields did not change, that one is published again.
    /// </summary>
    Task<NodeAnnouncementPayload?> AnnounceAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// <see cref="AnnounceAsync"/> on another task (callers may hold a channel's lock). Failures are logged.
    /// </summary>
    void RequestAnnouncement();
}