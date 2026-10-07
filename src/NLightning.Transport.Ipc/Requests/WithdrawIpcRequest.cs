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

    /// <summary>
    /// The operator's label (NL-602 A3-T1, <c>--label</c>), or null; an older client sends none.
    /// </summary>
    [Key(3)] public string? Label { get; init; }

    /// <summary>
    /// The operator's tags as <c>key=value</c> (NL-602 A3-T1, <c>--tag</c>), or null for none.
    /// </summary>
    [Key(4)] public List<string>? Tags { get; init; }

    /// <summary>
    /// The wallet outputs to spend as <c>txid:vout</c> (NL-1296, <c>--utxo</c>), or null to let the wallet choose; an
    /// older client sends none.
    /// </summary>
    [Key(5)] public List<string>? Utxos { get; init; }

    public WithdrawClientRequest ToClientRequest() => new(Address, AmountSat, SatPerVbyte)
    {
        Label = Label,
        Tags = Tags ?? [],
        Utxos = Utxos ?? []
    };
}