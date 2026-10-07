using MessagePack;

namespace NLightning.Transport.Ipc.Requests;

using Domain.Client.Requests;

[MessagePackObject]
public sealed class WalletHistoryIpcRequest
{
    [Key(0)] public uint? FromHeight { get; init; }
    [Key(1)] public uint? ToHeight { get; init; }
    [Key(2)] public bool AllowPartial { get; init; }
    [Key(3)] public uint AddressCount { get; set; } = 30;
    [Key(4)] public bool Cancel { get; init; }
    public WalletHistoryClientRequest ToClientRequest() => new(FromHeight, ToHeight, AllowPartial, AddressCount, Cancel);
}