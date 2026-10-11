namespace NLightning.Application.Payments.Keysend;

using Domain.Crypto.ValueObjects;
using Domain.Payments.Keysend;
using Domain.Protocol.Onion.Constants;
using Domain.Protocol.Tlv;

/// <summary>
/// What the payee's hop payload of a keysend payment carries instead of <c>payment_data</c>: our preimage in the
/// <c>keysend_preimage</c> record (5482373484) and the caller's custom records.
/// </summary>
/// <param name="Preimage">The preimage we picked; the payment hash is its SHA256.</param>
/// <param name="CustomRecords">Application records (types of 65536 or more, checked by
/// <see cref="CustomRecordCodec.Validate"/>).</param>
public sealed record KeysendFinalRecords(Secret Preimage, IReadOnlyList<CustomRecord> CustomRecords)
{
    /// <summary>
    /// The raw records for the final <c>HopPayload</c>: <c>keysend_preimage</c> and the custom records (the payload
    /// sorts them).
    /// </summary>
    public IEnumerable<BaseTlv> ToTlvs()
    {
        yield return new BaseTlv(OnionPayloadTlvTypes.KeysendPreimage, ((byte[])Preimage).ToArray());
        foreach (var record in CustomRecords)
            yield return new BaseTlv(record.Type, record.Value.ToArray());
    }
}