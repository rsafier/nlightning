namespace NLightning.Domain.Protocol.Onion.Tlv;

using Constants;
using Protocol.Tlv;

/// <summary>
/// Onion hop payload TLV 10 (<c>encrypted_recipient_data</c>), opaque route-blinding data.
/// </summary>
public class EncryptedRecipientDataTlv : BaseTlv
{
    /// <summary>
    /// The encrypted recipient data.
    /// </summary>
    public ReadOnlyMemory<byte> EncryptedRecipientData { get; }

    public EncryptedRecipientDataTlv(ReadOnlySpan<byte> encryptedRecipientData)
        : base(OnionPayloadTlvTypes.EncryptedRecipientData)
    {
        var value = encryptedRecipientData.ToArray();

        Value = value;
        Length = value.Length;
        EncryptedRecipientData = value;
    }
}