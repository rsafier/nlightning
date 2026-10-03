namespace NLightning.Domain.Client.Responses;

using Bitcoin.ValueObjects;

/// <summary>
/// The outcome of <c>withdraw</c> (<c>ClientCommand.Withdraw</c>): the transaction that was signed and stored for
/// broadcast.
/// </summary>
public sealed class WithdrawClientResponse
{
    public WithdrawClientResponse(TxId txId, long amountSat, long feeSat, long changeSat, long feeRatePerKw,
                                  int weight, int inputCount, long anchorReserveSat, bool published)
    {
        TxId = txId;
        AmountSat = amountSat;
        FeeSat = feeSat;
        ChangeSat = changeSat;
        FeeRatePerKw = feeRatePerKw;
        Weight = weight;
        InputCount = inputCount;
        AnchorReserveSat = anchorReserveSat;
        Published = published;
    }

    /// <summary>The transaction id in internal byte order (print it reversed).</summary>
    public TxId TxId { get; }

    public long AmountSat { get; }
    public long FeeSat { get; }
    public long ChangeSat { get; }
    public long FeeRatePerKw { get; }
    public int Weight { get; }
    public int InputCount { get; }

    /// <summary>The anchors reserve the wallet keeps (0 without anchors channels).</summary>
    public long AnchorReserveSat { get; }

    /// <summary>False when bitcoind refused the send: the transaction stays stored and is sent again after every
    /// block.</summary>
    public bool Published { get; }
}