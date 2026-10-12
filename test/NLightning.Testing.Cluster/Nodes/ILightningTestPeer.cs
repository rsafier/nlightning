namespace NLightning.Testing.Cluster.Nodes;

/// <summary>
/// The uniform Lightning facade over every implementation (plan R9), so a cross-implementation flow is written once
/// and run per implementation. Only the shape for now: the LND, CLN, Eclair, LDK and NLightning adapters implement it.
/// Amounts carry their unit in the name (<c>Sat</c>, <c>Msat</c>); node ids are lower-case hex.
/// </summary>
/// <remarks>
/// Implementation-specific clients stay reachable on the adapters for the tests that need them. To be added as the
/// lanes need them (plan R9): close and force close, keysend, offers, wait-until-routable (NL-319), graph queries.
/// </remarks>
public interface ILightningTestPeer
{
    /// <summary>The deployed node, or null for an in-process node.</summary>
    INodeHandle? Node { get; }

    NodeKind Kind { get; }

    /// <summary>The node's alias in its topology.</summary>
    string Alias { get; }

    /// <summary>The node id (33-byte compressed key, lower-case hex).</summary>
    Task<string> GetNodeIdAsync(CancellationToken cancellationToken);

    /// <summary>The address other nodes of the run dial (in-cluster DNS name and p2p port).</summary>
    Task<TestPeerAddress> GetAddressAsync(CancellationToken cancellationToken);

    /// <summary>Connects to <paramref name="peer"/> and returns once the connection is up (init exchanged).</summary>
    Task ConnectAsync(TestPeerAddress peer, CancellationToken cancellationToken);

    Task DisconnectAsync(string nodeId, CancellationToken cancellationToken);

    /// <summary>A new on-chain address of the node's wallet (to fund it).</summary>
    Task<string> GetNewAddressAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Opens a channel to a connected peer and returns once the funding transaction is published (it still needs
    /// blocks to become usable).
    /// </summary>
    Task<TestChannelOpen> OpenChannelAsync(TestOpenChannelRequest request, CancellationToken cancellationToken);

    Task<IReadOnlyList<TestChannel>> ListChannelsAsync(CancellationToken cancellationToken);

    /// <summary>Creates a BOLT 11 invoice; a null amount makes an amountless invoice.</summary>
    Task<TestInvoice> CreateInvoiceAsync(long? amountMsat, string description, CancellationToken cancellationToken);

    /// <summary>Pays a BOLT 11 invoice and returns the outcome (a failed payment is a result, not an exception).</summary>
    Task<TestPaymentResult> PayInvoiceAsync(string bolt11, CancellationToken cancellationToken);
}

/// <summary>A node's p2p address: <c>&lt;node id&gt;@&lt;host&gt;:&lt;port&gt;</c>.</summary>
public sealed record TestPeerAddress(string NodeId, string Host, int Port)
{
    public override string ToString() => $"{NodeId}@{Host}:{Port}";
}

/// <summary>A channel open request.</summary>
/// <param name="RemoteNodeId">The connected peer.</param>
/// <param name="CapacitySat">The funding amount.</param>
/// <param name="PushMsat">The amount pushed to the peer at open.</param>
/// <param name="Announce">Whether the channel is public.</param>
public sealed record TestOpenChannelRequest(string RemoteNodeId, long CapacitySat, long PushMsat = 0,
                                            bool Announce = false);

/// <summary>A published channel open.</summary>
/// <param name="FundingTxId">The funding transaction id (display hex).</param>
/// <param name="OutputIndex">The funding output index, when the implementation reports it.</param>
public sealed record TestChannelOpen(string FundingTxId, int? OutputIndex);

/// <summary>One channel as the facade sees it.</summary>
/// <param name="RemoteNodeId">The peer.</param>
/// <param name="FundingTxId">The funding transaction id (display hex).</param>
/// <param name="OutputIndex">The funding output index, when known.</param>
/// <param name="ShortChannelId">The SCID as <c>block x tx x output</c>, or null before confirmation.</param>
/// <param name="CapacitySat">The capacity.</param>
/// <param name="LocalBalanceMsat">Our side's balance.</param>
/// <param name="Active">Whether the implementation reports it usable for payments.</param>
public sealed record TestChannel(string RemoteNodeId, string FundingTxId, int? OutputIndex, string? ShortChannelId,
                                 long CapacitySat, long LocalBalanceMsat, bool Active);

/// <summary>A created invoice.</summary>
public sealed record TestInvoice(string Bolt11, string PaymentHashHex);

/// <summary>A payment's outcome.</summary>
/// <param name="Succeeded">Whether it was paid.</param>
/// <param name="PreimageHex">The preimage when it was.</param>
/// <param name="FailureReason">The implementation's failure text when it was not.</param>
public sealed record TestPaymentResult(bool Succeeded, string? PreimageHex, string? FailureReason);