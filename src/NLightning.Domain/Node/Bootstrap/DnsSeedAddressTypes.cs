namespace NLightning.Domain.Node.Bootstrap;

/// <summary>
/// The address families asked of a BOLT 10 DNS seed. Each bit is the BOLT 7 address descriptor type it stands for,
/// so the value is also the seed's <c>a</c> condition (<c>a2</c>, <c>a4</c>, <c>a6</c>).
/// </summary>
[Flags]
public enum DnsSeedAddressTypes : byte
{
    /// <summary>IPv4 (BOLT 7 address type 1, bit 1).</summary>
    IPv4 = 2,

    /// <summary>IPv6 (BOLT 7 address type 2, bit 2).</summary>
    IPv6 = 4,

    /// <summary>Both IPv4 and IPv6.</summary>
    Both = IPv4 | IPv6
}