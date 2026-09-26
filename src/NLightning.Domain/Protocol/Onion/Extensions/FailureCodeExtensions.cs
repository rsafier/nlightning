namespace NLightning.Domain.Protocol.Onion.Extensions;

using Enums;

/// <summary>
/// Helpers to inspect the flag bits of a <see cref="FailureCode"/>.
/// </summary>
public static class FailureCodeExtensions
{
    /// <summary>
    /// Gets the flag bits (upper nibble) of the failure code.
    /// </summary>
    public static FailureCodeFlags GetFlags(this FailureCode code)
    {
        const ushort flagMask = (ushort)(FailureCodeFlags.BadOnion | FailureCodeFlags.Perm | FailureCodeFlags.Node
                                       | FailureCodeFlags.Update);
        return (FailureCodeFlags)((ushort)code & flagMask);
    }

    /// <summary>
    /// True if the BADONION flag (0x8000) is set.
    /// </summary>
    public static bool IsBadOnion(this FailureCode code) => code.HasFlagBit(FailureCodeFlags.BadOnion);

    /// <summary>
    /// True if the PERM flag (0x4000) is set.
    /// </summary>
    public static bool IsPerm(this FailureCode code) => code.HasFlagBit(FailureCodeFlags.Perm);

    /// <summary>
    /// True if the NODE flag (0x2000) is set.
    /// </summary>
    public static bool IsNode(this FailureCode code) => code.HasFlagBit(FailureCodeFlags.Node);

    /// <summary>
    /// True if the UPDATE flag (0x1000) is set.
    /// </summary>
    public static bool IsUpdate(this FailureCode code) => code.HasFlagBit(FailureCodeFlags.Update);

    private static bool HasFlagBit(this FailureCode code, FailureCodeFlags flag) => ((ushort)code & (ushort)flag) != 0;
}