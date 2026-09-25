namespace NLightning.Infrastructure.Protocol.Validators;

using Domain.Protocol.Payloads;

public static class TxRemoveInputValidator
{
    /// <param name="isSenderInitiator">Whether the node that sent the message is the negotiation initiator.</param>
    public static void Validate(bool isSenderInitiator, TxRemoveInputPayload input, Func<ulong, bool> isSerialIdPresent)
    {
        // BOLT 2: the initiator sends even serial_ids, the non-initiator sends odd ones
        if ((input.SerialId & 1) != (isSenderInitiator ? 0UL : 1UL))
        {
            throw new InvalidOperationException("SerialId has the wrong parity.");
        }

        if (!isSerialIdPresent(input.SerialId))
        {
            throw new InvalidOperationException("The serial_id does not correspond to a currently added input.");
        }
    }
}