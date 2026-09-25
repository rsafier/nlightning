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
    /// New channel update enclosed.
    /// </summary>
    Update = 0x1000
}