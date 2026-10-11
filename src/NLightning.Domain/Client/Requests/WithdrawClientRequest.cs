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

    /// <summary>
    /// The operator's label (NL-602 A3-T1, <c>--label</c>): stored on the row and copied into the accounting event's
    /// details; null for none. Checked by the daemon (<c>SourceLabelRules</c>).
    /// </summary>
    public string? Label { get; init; }

    /// <summary>
    /// The operator's tags as <c>key=value</c> (NL-602 A3-T1, <c>--tag</c>, repeatable); empty for none. Checked by the
    /// daemon (<c>SourceLabelRules</c>).
    /// </summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>
    /// The wallet outputs to spend, as <c>txid:vout</c> with the txid in display order (NL-1296, <c>--utxo</c>,
    /// repeatable); empty lets the wallet choose. Exactly these are spent, silent payment coins included. Checked by
    /// the daemon.
    /// </summary>
    public IReadOnlyList<string> Utxos { get; init; } = [];
}