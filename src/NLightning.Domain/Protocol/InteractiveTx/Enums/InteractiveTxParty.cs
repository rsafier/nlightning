namespace NLightning.Domain.Protocol.InteractiveTx.Enums;

/// <summary>
/// Which side of an interactive-tx negotiation added an input or output (BOLT 2 "Interactive Transaction
/// Construction"). The initiator uses even <c>serial_id</c>s and the non-initiator odd ones (IT-S-01).
/// </summary>
public enum InteractiveTxParty : byte
{
    /// <summary>We added it.</summary>
    Local = 1,

    /// <summary>The peer added it.</summary>
    Remote = 2
}