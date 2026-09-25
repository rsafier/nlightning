using System.Buffers.Binary;

namespace NLightning.Domain.Protocol.Onion.Models;

using Enums;

/// <summary>
/// The result of decrypting a failure onion at the origin node: which hop sent it and what it says.
/// </summary>
public sealed class DecryptedFailure
{
    /// <summary>
    /// The index of the erring hop in the route (0 = first hop), i.e. the hop whose <c>um</c> key authenticated the
    /// return packet.
    /// </summary>
    public int ErringHopIndex { get; }

    /// <summary>
    /// The raw <c>failuremsg</c> bytes as framed by the erring hop (empty when its <c>failure_len</c> framing is
    /// invalid).
    /// </summary>
    public ReadOnlyMemory<byte> RawMessage { get; }

    /// <summary>
    /// The parsed failure message, or <c>null</c> when <see cref="RawMessage"/> could not be parsed (e.g. the data of
    /// a known code is truncated). The erring hop is still attributable in that case.
    /// </summary>
    public FailureMessage? Message { get; }

    /// <summary>
    /// The failure code: that of <see cref="Message"/>, else the first two bytes of <see cref="RawMessage"/>, else
    /// <c>null</c>.
    /// </summary>
    public FailureCode? Code =>
        Message?.Code ?? (RawMessage.Length >= FailureMessage.CodeLength
                              ? (FailureCode)BinaryPrimitives.ReadUInt16BigEndian(RawMessage.Span)
                              : null);

    public DecryptedFailure(int erringHopIndex, ReadOnlyMemory<byte> rawMessage, FailureMessage? message)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(erringHopIndex);

        ErringHopIndex = erringHopIndex;
        RawMessage = rawMessage;
        Message = message;
    }
}