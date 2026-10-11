namespace NLightning.Domain.Gossip.Addresses;

/// <summary>
/// The outcome of <see cref="AddressDescriptorCodec.DecodeList"/>.
/// </summary>
/// <param name="Addresses">The usable descriptors, in wire order.</param>
/// <param name="IsMalformed">A known-type descriptor did not fit the remaining bytes (BOLT 7: SHOULD warn).</param>
/// <param name="StoppedAtUnknownType">An unknown type was met; it and the rest were ignored.</param>
/// <param name="HasMultipleDns">More than one DNS descriptor: the extras were dropped, and the announcement MUST
/// NOT be forwarded.</param>
/// <param name="IgnoredPortZero">Descriptors dropped for port 0.</param>
/// <param name="IgnoredTorV2">Tor v2 descriptors dropped.</param>
/// <param name="IgnoredInvalid">Descriptors dropped for invalid content (an empty hostname, or one with a character
/// other than ASCII letters, digits, '-', '_' and '.').</param>
public sealed record AddressListDecodeResult(
    IReadOnlyList<AddressDescriptor> Addresses,
    bool IsMalformed,
    bool StoppedAtUnknownType,
    bool HasMultipleDns,
    int IgnoredPortZero,
    int IgnoredTorV2,
    int IgnoredInvalid);