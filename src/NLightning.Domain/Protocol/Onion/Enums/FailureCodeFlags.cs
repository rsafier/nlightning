namespace NLightning.Domain.Protocol.Onion.Enums;

/// <summary>
/// Flag bits carried in the upper nibble of a BOLT 4 failure code.
/// </summary>
[Flags]
public enum FailureCodeFlags : ushort
{
    None = 0,

    /// <summary>
    /// Unparsable onion encrypted by the sending peer.
    /// </summary>
    BadOnion = 0x8000,

    /// <summary>
    /// Permanent failure (otherwise transient).
    /// </summary>
    Perm = 0x4000,

    /// <summary>
    /// Node failure (otherwise channel).
    /// </summary>
    Node = 0x2000,

    /// <summary>
    /// Channel forwarding parameter was violated (BOLT 4).
    /// </summary>
    /// <remarks>
    /// Failures with this flag carry a <c>u16 len || channel_update</c> field, but the <c>channel_update</c> is no
    /// longer mandatory: nodes not wishing to send one set <c>len</c> to zero, and receivers MUST accept an empty one.
    /// </remarks>
    Update = 0x1000
}