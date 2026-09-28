using MessagePack;

namespace NLightning.Transport.Ipc.Requests;

using Domain.Channels.ValueObjects;
using Domain.Client.Requests;

/// <summary>
/// Request for SpliceIn (ClientCommand 33). DTO shell of the SP1 contracts; the handlers are lane SP1-E's.
/// </summary>
[MessagePackObject]
public sealed class SpliceInIpcRequest
{
    [Key(0)] public required ChannelId ChannelId { get; init; }

    /// <summary>The amount added to our channel balance, in satoshis.</summary>
    [Key(1)] public required ulong AmountSat { get; init; }

    /// <summary>The splice transaction's feerate, or null for the daemon's estimate.</summary>
    [Key(2)] public uint? FeeRatePerKw { get; init; }

    public SpliceInClientRequest ToClientRequest() => new(ChannelId, AmountSat) { FeeRatePerKw = FeeRatePerKw };
}

/// <summary>
/// Request for SpliceOut (ClientCommand 34). DTO shell of the SP1 contracts; the handlers are lane SP1-E's.
/// </summary>
[MessagePackObject]
public sealed class SpliceOutIpcRequest
{
    [Key(0)] public required ChannelId ChannelId { get; init; }

    /// <summary>The amount taken out of our channel balance, in satoshis.</summary>
    [Key(1)] public required ulong AmountSat { get; init; }

    /// <summary>The destination address, or null for a new address of the daemon's wallet.</summary>
    [Key(2)] public string? Address { get; init; }

    /// <summary>The splice transaction's feerate, or null for the daemon's estimate.</summary>
    [Key(3)] public uint? FeeRatePerKw { get; init; }

    public SpliceOutClientRequest ToClientRequest() =>
        new(ChannelId, AmountSat) { Address = Address, FeeRatePerKw = FeeRatePerKw };
}

/// <summary>
/// Request for BumpSplice (ClientCommand 37), answered with a <c>SpliceIpcResponse</c>. DTO shell of the SPR contracts;
/// the handlers are lane SPR-B's.
/// </summary>
[MessagePackObject]
public sealed class BumpSpliceIpcRequest
{
    [Key(0)] public required ChannelId ChannelId { get; init; }

    /// <summary>The new attempt's feerate, in sat/kw.</summary>
    [Key(1)] public required uint FeeRatePerKw { get; init; }

    /// <summary>The most our side may pay for the new attempt, in satoshis, or null for no cap.</summary>
    [Key(2)] public ulong? MaxFeeSat { get; init; }

    public BumpSpliceClientRequest ToClientRequest() => new(ChannelId, FeeRatePerKw) { MaxFeeSat = MaxFeeSat };
}