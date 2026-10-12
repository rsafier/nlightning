namespace NLightning.Domain.Bitcoin.Constants;

/// <summary>
/// What the node refuses while its chain processing is halted (NL-216): a block that keeps failing, or a reorg deeper
/// than the header ring, leaves the node blind to the chain (no confirmations, spends, deposits or deadlines), so it
/// takes on no new risk that only the chain can settle.
/// </summary>
/// <remarks>
/// Refused: opening a channel (ours over IPC, and the peer's <c>open_channel</c>, answered with an <c>error</c>),
/// offering an HTLC (our payments, and forwards, which fail back upstream with <c>temporary_channel_failure</c>) and
/// accepting a new HTLC as final hop (failed back with <c>temporary_node_failure</c> instead of revealing the preimage).
/// Still done: fulfilling a forwarded HTLC upstream and completing an HTLC set already committed to (both only claim
/// money we are owed), failing HTLCs back, fee updates, cooperative and forced closes, and every broadcast (the chain
/// monitor keeps resending pending transactions while halted).
/// </remarks>
public static class ChainProcessingHalt
{
    /// <summary>The ledger entry the gate implements, used as the requirement id of the refusals.</summary>
    public const string RequirementId = "NL-216";

    /// <summary>The operations refused while halted, as the <c>chainstatus</c> command lists them.</summary>
    public static IReadOnlyList<string> RefusedOperations { get; } =
    [
        "openchannel (ours)",
        "open_channel from peers",
        "update_add_htlc (payments and forwards)",
        "final-hop HTLC acceptance (new invoice payments are failed back)"
    ];

    /// <summary>The refusal text for <paramref name="operation"/>.</summary>
    public static string Refusal(string operation) =>
        $"{operation} refused: chain processing is halted (see chainstatus)";
}