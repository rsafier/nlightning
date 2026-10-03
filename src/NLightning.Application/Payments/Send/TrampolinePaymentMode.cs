namespace NLightning.Application.Payments.Send;

/// <summary>
/// When our payments go through a trampoline node (NL-875, <c>Node:Payments:Trampoline</c>; decision D-TR7).
/// </summary>
/// <remarks>
/// A trampoline node is a peer with a usable channel, else a node of the gossip graph, that advertises
/// <c>trampoline_routing</c> (bit 56 or 57); the recipient must support it too (its invoice sets bit 57). A call's own
/// <c>PayInvoiceOptions.TrampolineNode</c> always wins. An invoice that requires trampoline (bit 56) is refused when no
/// trampoline node is used.
/// </remarks>
public enum TrampolinePaymentMode
{
    /// <summary>Only when the call names a trampoline node (the default).</summary>
    Never = 0,

    /// <summary>When the planner finds no route of its own to the recipient.</summary>
    Auto = 1,

    /// <summary>Whenever a trampoline node is known and the recipient supports trampoline.</summary>
    Always = 2
}