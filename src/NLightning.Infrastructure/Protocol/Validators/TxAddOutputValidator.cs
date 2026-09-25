namespace NLightning.Infrastructure.Protocol.Validators;

using Domain.Money;
using Domain.Protocol.Constants;
using Domain.Protocol.Payloads;

public static class TxAddOutputValidator
{
    /// <param name="isSenderInitiator">Whether the node that sent the message is the negotiation initiator.</param>
    public static void Validate(bool isSenderInitiator, TxAddOutputPayload output, int currentOutputCount,
                                Func<ulong, bool> isSerialIdUnique, Func<byte[], bool> isStandardScript,
                                LightningMoney dustLimit)
    {
        // BOLT 2: the initiator sends even serial_ids, the non-initiator sends odd ones
        if ((output.SerialId & 1) != (isSenderInitiator ? 0UL : 1UL))
        {
            throw new InvalidOperationException("SerialId has the wrong parity.");
        }

        if (!isSerialIdUnique(output.SerialId))
        {
            throw new InvalidOperationException("SerialId is already included in the transaction.");
        }

        if (currentOutputCount >= InteractiveTransactionConstants.MaxOutputsAllowed)
        {
            throw new InvalidOperationException($"Cannot receive more than {InteractiveTransactionConstants.MaxOutputsAllowed} tx_add_output messages during this negotiation.");
        }

        if (output.Amount < dustLimit)
        {
            throw new InvalidOperationException("The sats amount is less than the dust_limit.");
        }

        if (output.Amount > InteractiveTransactionConstants.MaxMoney)
        {
            throw new InvalidOperationException("The sats amount is greater than the maximum allowed (MAX_MONEY).");
        }

        if (!isStandardScript(output.Script))
        {
            throw new InvalidOperationException("The script is non-standard.");
        }
    }
}