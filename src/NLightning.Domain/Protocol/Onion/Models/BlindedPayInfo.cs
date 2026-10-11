namespace NLightning.Domain.Protocol.Onion.Models;

/// <summary>
/// What a blinded path costs its sender: the aggregated fees and CLTV delta of the hops of the path and the HTLC limits
/// at its introduction node (BOLT 12 <c>blinded_payinfo</c>; BOLT 4 "Route Blinding", the recipient's aggregated
/// <c>payment_relay</c>).
/// </summary>
/// <param name="FeeBaseMsat">The aggregated <c>fee_base_msat</c>.</param>
/// <param name="FeeProportionalMillionths">The aggregated <c>fee_proportional_millionths</c>.</param>
/// <param name="CltvExpiryDelta">The aggregated <c>cltv_expiry_delta</c>, the recipient's own final delta included:
/// the introduction node's incoming <c>cltv_expiry</c> is the final hop's <c>outgoing_cltv_value</c> plus this.</param>
/// <param name="HtlcMinimumMsat">The smallest amount the introduction node takes into the path.</param>
/// <param name="HtlcMaximumMsat">The largest amount the introduction node takes into the path.</param>
/// <param name="Features">The path's feature bits (none is defined yet).</param>
public sealed record BlindedPayInfo(
    uint FeeBaseMsat,
    uint FeeProportionalMillionths,
    ushort CltvExpiryDelta,
    ulong HtlcMinimumMsat,
    ulong HtlcMaximumMsat,
    ReadOnlyMemory<byte> Features = default)
{
    private const ulong OneMillion = 1_000_000;

    /// <summary>
    /// The fee the path charges to deliver <paramref name="amountMsat"/> to the recipient (BOLT 12:
    /// <c>fee_base_msat + amount_msat * fee_proportional_millionths / 1000000</c>, rounded down; the recipient rounded
    /// its aggregation up so that this covers every hop).
    /// </summary>
    /// <exception cref="OverflowException">If the fee does not fit in 64 bits.</exception>
    public ulong ComputeFeeMsat(ulong amountMsat) =>
        checked(FeeBaseMsat + (ulong)((UInt128)amountMsat * FeeProportionalMillionths / OneMillion));

    /// <summary>
    /// BOLT 4 "Route Blinding", the recipient aggregating its path: the fee and CLTV delta a sender pays for a path
    /// whose relaying hops (introduction node first, the recipient's own hop excluded) use <paramref name="relays"/>,
    /// plus the recipient's <paramref name="finalCltvDelta"/>.
    /// </summary>
    /// <remarks>
    /// From the recipient back to the introduction node:
    /// <c>total_fee_base_msat(n+1) = (fee_base_msat(n+1) * 1000000 + total_fee_base_msat(n) * (1000000 +
    /// fee_proportional_millionths(n+1)) + 1000000 - 1) / 1000000</c> and
    /// <c>total_fee_proportional_millionths(n+1) = ((total_fee_proportional_millionths(n) +
    /// fee_proportional_millionths(n+1)) * 1000000 + total_fee_proportional_millionths(n) *
    /// fee_proportional_millionths(n+1) + 1000000 - 1) / 1000000</c>.
    /// </remarks>
    /// <exception cref="OverflowException">If a total does not fit its field.</exception>
    public static (uint FeeBaseMsat, uint FeeProportionalMillionths, ushort CltvExpiryDelta) Aggregate(
        IReadOnlyList<BlindedPaymentRelay> relays, ushort finalCltvDelta)
    {
        ArgumentNullException.ThrowIfNull(relays);

        UInt128 totalBase = 0;
        UInt128 totalProportional = 0;
        var totalCltv = (uint)finalCltvDelta;
        for (var i = relays.Count - 1; i >= 0; i--)
        {
            var relay = relays[i];
            totalBase = (relay.FeeBaseMsat * (UInt128)OneMillion
                       + totalBase * (OneMillion + relay.FeeProportionalMillionths) + OneMillion - 1) / OneMillion;
            totalProportional = ((totalProportional + relay.FeeProportionalMillionths) * OneMillion
                               + totalProportional * relay.FeeProportionalMillionths + OneMillion - 1) / OneMillion;
            totalCltv += relay.CltvExpiryDelta;
        }

        return (checked((uint)totalBase), checked((uint)totalProportional), checked((ushort)totalCltv));
    }
}