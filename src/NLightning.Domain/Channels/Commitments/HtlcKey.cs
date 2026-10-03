namespace NLightning.Domain.Channels.Commitments;

using Enums;

/// <summary>
/// Identifies an HTLC: ids are unique per direction only (both sides start at 0).
/// </summary>
public readonly record struct HtlcKey(HtlcDirection Direction, ulong Id) : IComparable<HtlcKey>
{
    public int CompareTo(HtlcKey other)
    {
        var byDirection = Direction.CompareTo(other.Direction);
        return byDirection != 0 ? byDirection : Id.CompareTo(other.Id);
    }
}