namespace NLightning.Domain.Node.Interfaces;

using Bootstrap;

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
}