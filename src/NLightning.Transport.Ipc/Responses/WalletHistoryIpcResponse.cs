using MessagePack;

namespace NLightning.Transport.Ipc.Responses;

using Domain.Client.Responses;

[MessagePackObject]
public sealed class WalletHistoryIpcResponse
{
    [Key(0)] public bool HasJob { get; init; }
    [Key(1)] public uint RequestedFromHeight { get; init; }
    [Key(2)] public uint AvailableFromHeight { get; init; }
    [Key(3)] public uint TargetHeight { get; init; }
    [Key(4)] public uint? CursorHeight { get; init; }
    [Key(5)] public bool IsActive { get; init; }
    [Key(6)] public bool IsPartial { get; init; }
    [Key(7)] public string? Error { get; init; }
    [Key(8)] public uint AddressCount { get; init; }
    public static WalletHistoryIpcResponse FromClientResponse(WalletHistoryClientResponse response) =>
        response.State is { } state ? new()
        {
            HasJob = true,
            RequestedFromHeight = state.RequestedFromHeight,
            AvailableFromHeight = state.AvailableFromHeight,
            TargetHeight = state.TargetHeight,
            CursorHeight = state.CursorHeight,
            IsActive = state.IsActive,
            IsPartial = state.IsPartial,
            Error = state.Error,
            AddressCount = state.AddressCount
        } : new();
}