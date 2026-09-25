namespace NLightning.Infrastructure.Protocol.Validators;

using Domain.Protocol.Payloads;

public static class TxRemoveOutputValidator
{
    /// <param name="isSenderInitiator">Whether the node that sent the message is the negotiation initiator.</param>
    public static void Validate(bool isSenderInitiator, TxRemoveOutputPayload output, Func<ulong, bool> isSerialIdPresent)
    {
        // BOLT 2: the initiator sends even serial_ids, the non-initiator sends odd ones
        if ((output.SerialId & 1) != (isSenderInitiator ? 0UL : 1UL))
        {
            throw new InvalidOperationException("SerialId has the wrong parity.");
        }

        if (!isSerialIdPresent(output.SerialId))
        {
            throw new InvalidOperationException("The serial_id does not correspond to a currently added output.");
        }
    }
}