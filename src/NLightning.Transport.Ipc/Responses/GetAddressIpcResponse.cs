using MessagePack;

namespace NLightning.Transport.Ipc.Responses;

/// <summary>
/// Response for Get Address command
/// </summary>
[MessagePackObject]
public sealed class GetAddressIpcResponse
{
    [Key(0)] public string? AddressP2Tr { get; set; }

    /// <summary>
    /// The P2WPKH address. Wire key 1 is unchanged from the former, misnamed <c>AddressP2Wsh</c>.
    /// </summary>
    [Key(1)] public string? AddressP2Wpkh { get; set; }
}