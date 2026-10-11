using MessagePack;

namespace NLightning.Transport.Ipc.Requests;

using Domain.Bitcoin.Enums;

/// <summary>
/// Request for Get Address command
/// </summary>
[MessagePackObject]
public sealed class GetAddressIpcRequest
{
    [Key(0)] public AddressType AddressType { get; set; } = AddressType.P2Tr;

    /// <summary>Choose a supported default address type rather than requiring an explicit type.</summary>
    [Key(1)] public bool UseDefaultAddressType { get; init; }
}