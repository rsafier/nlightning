namespace NLightning.Domain.Protocol.Messages;

using Constants;
using Models;
using Payloads;
using Payments.Keysend;
using Tlv;
using ValueObjects;

/// <summary>
/// Represents a update_add_htlc message.
/// </summary>
/// <remarks>
/// The update_add_htlc message offers a new htlc to the peer.
/// The message type is 128.
/// </remarks>
public sealed class UpdateAddHtlcMessage : BaseChannelMessage
{
    /// <summary>
    /// The payload of the message.
    /// </summary>
    public new UpdateAddHtlcPayload Payload { get => (UpdateAddHtlcPayload)base.Payload; }

    public BlindedPathTlv? BlindedPathTlv { get; }

    /// <summary>
    /// The extension's custom records (types of 65536 or more, ascending; LND's wire custom records, NL-1182). On a
    /// received message only the odd types are kept (an unknown even type fails the message, BOLT 1).
    /// </summary>
    public IReadOnlyList<CustomRecord> CustomRecords { get; }

    /// <exception cref="ArgumentException">A custom record's type is below 65536 or appears twice.</exception>
    public UpdateAddHtlcMessage(UpdateAddHtlcPayload payload, BlindedPathTlv? blindedPathTlv = null,
                                IEnumerable<CustomRecord>? customRecords = null)
        : base(MessageTypes.UpdateAddHtlc, payload)
    {
        BlindedPathTlv = blindedPathTlv;
        CustomRecords = WireCustomRecordCodec.Validate(customRecords);

        if (BlindedPathTlv is null && CustomRecords.Count == 0)
            return;

        // Strictly increasing types: blinded_path (0), then the custom records (65536 and up)
        Extension = new TlvStream();
        if (BlindedPathTlv is not null)
            Extension.Add(BlindedPathTlv);
        foreach (var record in CustomRecords)
            Extension.Add(new BaseTlv(new BigSize(record.Type), record.Value.ToArray()));
    }
}