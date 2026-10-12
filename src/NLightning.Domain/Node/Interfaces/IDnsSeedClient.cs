namespace NLightning.Domain.Node.Interfaces;

using Bootstrap;
using Crypto.ValueObjects;

/// <summary>
/// Queries one BOLT 10 DNS seed for peers to bootstrap from.
/// </summary>
public interface IDnsSeedClient
{
    /// <summary>
    /// Asks <paramref name="seedRoot"/> for SRV records, decodes each target's node id, finds its addresses and returns
    /// the validated candidates. A seed that fails (NXDOMAIN, SERVFAIL, timeout) is reported in the result's outcome,
    /// never thrown.
    /// </summary>
    /// <param name="seedRoot">The seed's domain.</param>
    /// <param name="families">The address families wanted.</param>
    /// <param name="maxResults">The most candidates to return.</param>
    /// <param name="ct">The caller's token; its cancellation is the only exception thrown.</param>
    Task<DnsSeedResult> QuerySeedAsync(string seedRoot, DnsSeedAddressTypes families, int maxResults,
                                       CancellationToken ct);

    /// <summary>
    /// Asks <paramref name="seedRoot"/> where <paramref name="nodeId"/> listens (BOLT 10's assisted location,
    /// NL-541): the node query <c>l&lt;bech32 node id&gt;</c>, whose A/AAAA answers carry the node's addresses on
    /// the default port and whose SRV answer carries its non-default ports. A seed that does not know the node
    /// answers <see cref="DnsSeedOutcome.Empty"/> with no candidate; failures are reported in the outcome, never
    /// thrown.
    /// </summary>
    /// <param name="seedRoot">The seed's domain.</param>
    /// <param name="nodeId">The node id to locate.</param>
    /// <param name="families">The address families wanted.</param>
    /// <param name="ct">The caller's token; its cancellation is the only exception thrown.</param>
    Task<DnsSeedNodeLocation> LocateNodeAsync(string seedRoot, CompactPubKey nodeId, DnsSeedAddressTypes families,
                                              CancellationToken ct);
}