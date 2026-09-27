using MessagePack;

namespace NLightning.Transport.Ipc.Requests;

using Domain.Client.Requests;

/// <summary>
/// Request for Withdraw (ClientCommand 25).
/// </summary>
[MessagePackObject]
public sealed class WithdrawIpcRequest
{
    /// <summary>The destination address.</summary>
    [Key(0)] public required string Address { get; init; }

    /// <summary>The amount in satoshis; null sends everything the wallet may spend ("all").</summary>
    [Key(1)] public ulong? AmountSat { get; init; }

    /// <summary>The fee rate in sat/vB; null for the node's estimate.</summary>
    [Key(2)] public ulong? SatPerVbyte { get; init; }

    public WithdrawClientRequest ToClientRequest() => new(Address, AmountSat, SatPerVbyte);
}