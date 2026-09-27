namespace NLightning.Domain.Client.Requests;

/// <summary>
/// Pays an external address from the on-chain wallet (<c>ClientCommand.Withdraw</c>, 25).
/// </summary>
public sealed class WithdrawClientRequest
{
    public WithdrawClientRequest(string address, ulong? amountSat, ulong? satPerVbyte)
    {
        Address = address;
        AmountSat = amountSat;
        SatPerVbyte = satPerVbyte;
    }

    /// <summary>The destination address, of the node's network.</summary>
    public string Address { get; }

    /// <summary>What the destination receives, in satoshis; null sends everything the wallet may spend ("all"), keeping
    /// the anchors reserve.</summary>
    public ulong? AmountSat { get; }

    /// <summary>The fee rate in sat/vB; null for the node's fee estimate.</summary>
    public ulong? SatPerVbyte { get; }
}