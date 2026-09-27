using System.Buffers.Binary;

namespace NLightning.Domain.Protocol.Tlv;

using Constants;

/// <summary>
/// Start Batch Message Type TLV.
/// </summary>
/// <remarks>
/// BOLT 2 <c>start_batch_tlvs</c> type 1: [<c>u16</c>:<c>message_type</c>], big-endian. A sender MUST set it to 132
/// (<c>commitment_signed</c>); a receiver ignores a <c>start_batch</c> without it or with another type and processes
/// the following messages one by one (SP-OP-04). The converter and the strict known set {1} are lane SP1-A's.
/// </remarks>
public sealed class StartBatchMessageTypeTlv : BaseTlv
{
    /// <summary>The size of the TLV value: a u16.</summary>
    public const int ValueLength = sizeof(ushort);

    /// <summary>The type of the batched messages.</summary>
    public ushort MessageType { get; }

    public StartBatchMessageTypeTlv(ushort messageType) : base(TlvConstants.StartBatchMessageType)
    {
        MessageType = messageType;

        var value = new byte[ValueLength];
        BinaryPrimitives.WriteUInt16BigEndian(value, messageType);
        Value = value;
        Length = Value.Length;
    }

    /// <summary>The batch of splice <c>commitment_signed</c> messages (type 132).</summary>
    public static StartBatchMessageTypeTlv CommitmentSigned() => new((ushort)MessageTypes.CommitmentSigned);
}