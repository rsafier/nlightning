namespace NLightning.Domain.Protocol.Onion.Models;

/// <summary>
/// The <c>payment_relay</c> record (type 10) of a route-blinding <c>encrypted_data_tlv</c>: the fee and CLTV delta the
/// recipient of a blinded path told a blinded hop to apply (BOLT 4 "Route Blinding").
/// </summary>
/// <param name="CltvExpiryDelta">The <c>cltv_expiry_delta</c> (u16).</param>
/// <param name="FeeProportionalMillionths">The <c>fee_proportional_millionths</c> (u32).</param>
/// <param name="FeeBaseMsat">The <c>fee_base_msat</c> (tu32; 0 when the record omits it).</param>
public sealed record BlindedPaymentRelay(ushort CltvExpiryDelta, uint FeeProportionalMillionths, uint FeeBaseMsat)
{
    private const ulong OneMillion = 1_000_000;

    /// <summary>
    /// BOLT 4 reader, non-final blinded node:
    /// <c>amt_to_forward = ((amount_msat - fee_base_msat) * 1000000 + 1000000 + fee_proportional_millionths - 1)
    /// / (1000000 + fee_proportional_millionths)</c>.
    /// </summary>
    /// <param name="incomingAmountMsat">The incoming HTLC's <c>amount_msat</c>.</param>
    /// <param name="amountToForwardMsat">The amount of the outgoing HTLC.</param>
    /// <returns>False when the incoming amount does not cover <c>fee_base_msat</c>.</returns>
    public bool TryComputeAmountToForward(ulong incomingAmountMsat, out ulong amountToForwardMsat)
    {
        amountToForwardMsat = 0;
        if (incomingAmountMsat < FeeBaseMsat)
            return false;

        var numerator = (UInt128)(incomingAmountMsat - FeeBaseMsat) * OneMillion + OneMillion
                      + FeeProportionalMillionths - 1;
        amountToForwardMsat = (ulong)(numerator / (OneMillion + FeeProportionalMillionths));
        return true;
    }

    /// <summary>
    /// BOLT 4 reader, non-final blinded node:
    /// <c>outgoing_cltv_value = cltv_expiry - payment_relay.cltv_expiry_delta</c>.
    /// </summary>
    /// <returns>False when <paramref name="incomingCltvExpiry"/> is below the delta.</returns>
    public bool TryComputeOutgoingCltvValue(uint incomingCltvExpiry, out uint outgoingCltvValue)
    {
        outgoingCltvValue = 0;
        if (incomingCltvExpiry < CltvExpiryDelta)
            return false;

        outgoingCltvValue = incomingCltvExpiry - CltvExpiryDelta;
        return true;
    }
}