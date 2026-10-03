namespace NLightning.Domain.Client.Responses;

using Crypto.ValueObjects;
using Money;
using Payments.Trampoline;

/// <summary>
/// One hop of the trampoline route of one of our payments sent through a trampoline node (NL-899), as
/// <c>listpayments</c> shows it: the trampoline nodes first, then the payee when the route names it.
/// </summary>
/// <param name="NodeId">The trampoline node (or the payee, last).</param>
/// <param name="Amount">The amount the hop forwards (its <c>amt_to_forward</c>).</param>
/// <param name="CltvExpiry">The hop's <c>outgoing_cltv_value</c>.</param>
public sealed record PaymentTrampolineHopClientResponse(CompactPubKey NodeId, LightningMoney Amount, uint CltvExpiry)
{
    public static PaymentTrampolineHopClientResponse FromModel(PaymentTrampolineHopModel hop)
    {
        ArgumentNullException.ThrowIfNull(hop);
        return new PaymentTrampolineHopClientResponse(hop.NodeId, hop.Amount, hop.CltvExpiry);
    }
}