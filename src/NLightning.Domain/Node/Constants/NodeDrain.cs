namespace NLightning.Domain.Node.Constants;

/// <summary>
/// What the node refuses while it drains for a graceful shutdown (<c>shutdown</c>, NL-591).
/// </summary>
/// <remarks>
/// Refused: the operator commands that start something (opens, payments, splices and their bumps, withdrawals,
/// connections, restores, new invoices and offers), the peer's <c>open_channel</c>/<c>open_channel2</c> (answered with
/// an <c>error</c>), the peer's <c>splice_init</c>/<c>tx_init_rbf</c> (answered with <c>tx_abort</c>), offering an
/// HTLC (forwards fail back upstream with <c>temporary_channel_failure</c>) and accepting a new HTLC as final hop
/// (failed back with <c>temporary_node_failure</c>). Still done: fulfilling and failing HTLCs, completing a committed
/// HTLC set, fee updates, closes and broadcasts.
/// </remarks>
public static class NodeDrain
{
    /// <summary>The ledger entry of the drain, used as the requirement id of the refusals.</summary>
    public const string RequirementId = "NL-591";

    /// <summary>The refusal text for <paramref name="operation"/>.</summary>
    public static string Refusal(string operation) => $"{operation} refused: the node is shutting down";
}