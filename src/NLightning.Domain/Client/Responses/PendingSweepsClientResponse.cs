namespace NLightning.Domain.Client.Responses;

using Bitcoin.ValueObjects;
using Channels.Enums;
using Channels.ValueObjects;
using Onchain.Enums;

/// <summary>
/// The on-chain resolution of the node's closed channels (<c>ClientCommand.PendingSweeps</c>): per channel its funding
/// spend and every output being resolved.
/// </summary>
public sealed class PendingSweepsClientResponse
{
    public IReadOnlyList<PendingSweepChannelInfo> Channels { get; }

    public PendingSweepsClientResponse(IReadOnlyList<PendingSweepChannelInfo> channels)
    {
        Channels = channels;
    }
}

/// <summary>One closed channel: what spent its funding output and its outputs to resolve.</summary>
/// <param name="ChannelId">The channel.</param>
/// <param name="State">Its state (<c>OnchainResolving</c> until every output is irrevocable, then <c>Closed</c>).</param>
/// <param name="CloseKind">What spent the funding output.</param>
/// <param name="CommitmentTxId">The spending transaction.</param>
/// <param name="CommitmentNumber">Its commitment number, for a commitment.</param>
/// <param name="SpentAtHeight">The height of the block that holds it.</param>
/// <param name="Outputs">The outputs to resolve.</param>
public sealed record PendingSweepChannelInfo(
    ChannelId ChannelId,
    ChannelState State,
    ChannelCloseKind CloseKind,
    TxId CommitmentTxId,
    ulong? CommitmentNumber,
    uint SpentAtHeight,
    IReadOnlyList<PendingSweepOutputInfo> Outputs);

/// <summary>One output being resolved.</summary>
/// <param name="TransactionId">The transaction holding it.</param>
/// <param name="OutputIndex">Its index.</param>
/// <param name="Descriptor">What it is to us.</param>
/// <param name="State">Where its resolution stands.</param>
/// <param name="AmountSat">Its amount, when recorded.</param>
/// <param name="HtlcDirection">For an HTLC output, the HTLC's direction from our point of view.</param>
/// <param name="HtlcId">For an HTLC output, the HTLC's id.</param>
/// <param name="ResolvingTxId">Our transaction that resolves it, once broadcast.</param>
/// <param name="WaitUntilHeight">The height it waits for (CSV or CLTV), if any.</param>
/// <param name="DeadlineHeight">The height by which it must be resolved, if any.</param>
/// <param name="ResolvedHeight">The height of the spend that resolved it.</param>
public sealed record PendingSweepOutputInfo(
    TxId TransactionId,
    uint OutputIndex,
    OutputDescriptorKind Descriptor,
    OutputResolutionState State,
    ulong? AmountSat,
    HtlcDirection? HtlcDirection,
    ulong? HtlcId,
    TxId? ResolvingTxId,
    uint? WaitUntilHeight,
    uint? DeadlineHeight,
    uint? ResolvedHeight);