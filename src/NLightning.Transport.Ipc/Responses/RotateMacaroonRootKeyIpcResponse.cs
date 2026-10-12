using MessagePack;

namespace NLightning.Transport.Ipc.Responses;

using Domain.Client.Responses;

/// <summary>Response for RotateMacaroonRootKey (ClientCommand 58, NL-1389).</summary>
[MessagePackObject]
public sealed class RotateMacaroonRootKeyIpcResponse
{
    [Key(0)] public required string DataDirectory { get; init; }
    [Key(1)] public bool InDatabase { get; init; }
    [Key(2)] public bool RebakedDefaults { get; init; }

    public static RotateMacaroonRootKeyIpcResponse FromClientResponse(RotateMacaroonRootKeyClientResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        return new RotateMacaroonRootKeyIpcResponse
        {
            DataDirectory = response.DataDirectory,
            InDatabase = response.InDatabase,
            RebakedDefaults = response.RebakedDefaults
        };
    }
}