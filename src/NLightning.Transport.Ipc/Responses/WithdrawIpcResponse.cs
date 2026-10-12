using MessagePack;

namespace NLightning.Transport.Ipc.Responses;

using Domain.Bitcoin.ValueObjects;
using Domain.Client.Responses;

/// <summary>
/// Response for Withdraw (ClientCommand 25).
/// </summary>
[MessagePackObject]
public sealed class WithdrawIpcResponse
{
    /// <summary>The transaction id, internal byte order.</summary>
    [Key(0)] public required TxId TxId { get; init; }

    [Key(1)] public long AmountSat { get; init; }
    [Key(2)] public long FeeSat { get; init; }
    [Key(3)] public long ChangeSat { get; init; }
    [Key(4)] public long FeeRatePerKw { get; init; }
    [Key(5)] public int Weight { get; init; }
    [Key(6)] public int InputCount { get; init; }

    /// <summary>The anchors reserve the wallet keeps.</summary>
    [Key(7)] public long AnchorReserveSat { get; init; }

    /// <summary>False when bitcoind refused the send (it is sent again after every block).</summary>
    [Key(8)] public bool Published { get; init; }

    public static WithdrawIpcResponse FromClientResponse(WithdrawClientResponse clientResponse)
    {
        ArgumentNullException.ThrowIfNull(clientResponse);
        return new WithdrawIpcResponse
        {
            TxId = clientResponse.TxId,
            AmountSat = clientResponse.AmountSat,
            FeeSat = clientResponse.FeeSat,
            ChangeSat = clientResponse.ChangeSat,
            FeeRatePerKw = clientResponse.FeeRatePerKw,
            Weight = clientResponse.Weight,
            InputCount = clientResponse.InputCount,
            AnchorReserveSat = clientResponse.AnchorReserveSat,
            Published = clientResponse.Published
        };
    }
}