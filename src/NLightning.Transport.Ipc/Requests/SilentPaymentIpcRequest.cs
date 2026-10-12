using MessagePack;

namespace NLightning.Transport.Ipc.Requests;

using Domain.Client.Enums;
using Domain.Client.Requests;

[MessagePackObject]
public sealed class SilentPaymentIpcRequest
{
    [Key(0)] public string? Label { get; init; }
    [Key(1)] public uint? FromHeight { get; init; }
    [Key(2)] public uint? RecoveryLabels { get; init; }
    [Key(3)] public bool Cancel { get; init; }

    public SilentPaymentClientRequest ToClientRequest(ClientCommand command) =>
        new(command, Label, FromHeight, RecoveryLabels, Cancel);
}