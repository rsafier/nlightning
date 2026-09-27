namespace NLightning.Application.Channels.Splicing;

using Domain.Bitcoin.Transactions.Models;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Splicing;
using Domain.Channels.Splicing.Enums;
using Domain.Channels.Splicing.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Onchain.Models;
using Domain.Protocol.InteractiveTx;

/// <summary>
/// One splice negotiation of a channel, as <see cref="SpliceService"/> keeps it in memory (splicing plan §3.5): the
/// agreed terms (<see cref="Model"/>), what the interactive-tx host needs, and the waiter of
/// <see cref="SpliceService.StartAsync"/>. Mutated under the channel's lock, and by the end of the channel's quiescence
/// under <see cref="SpliceService"/>'s own lock.
/// </summary>
/// <remarks>
/// Memory only: BOLT 2 lets an unsigned negotiation be forgotten on disconnection, and from our
/// <c>commitment_signed</c> on the interactive-tx session row (lane IT-C) and the splice commitment staged by the
/// <see cref="Interfaces.ISpliceStatePort"/> (lane SP1-C) are what is remembered (SP-I7).
/// </remarks>
internal sealed class SpliceNegotiation
{
    public SpliceNegotiation(SpliceNegotiationModel model, CompactPubKey peerPubKey, ChannelFunding currentFunding)
    {
        Model = model;
        PeerPubKey = peerPubKey;
        CurrentFunding = currentFunding;
    }

    public ChannelId ChannelId => Model.ChannelId;
    public CompactPubKey PeerPubKey { get; }
    public bool IsInitiator => Model.IsInitiator;
    public SpliceNegotiationModel Model { get; set; }
    public SpliceNegotiationState State => Model.State;

    /// <summary>The channel's current funding, spent by the splice as its shared input.</summary>
    public ChannelFunding CurrentFunding { get; }

    /// <summary>Our and the peer's balances on the current funding (msat), for the shares of the shared funding: gross
    /// (<c>ChannelCommitments.LocalBalanceMsat</c>/<c>RemoteBalanceMsat</c>, which add up to the capacity).</summary>
    public ulong LocalGrossMsat { get; init; }

    public ulong RemoteGrossMsat { get; init; }

    /// <summary>Our and the peer's main balances on our current commitment (msat), for the rules (SP-TX-05).</summary>
    public ulong LocalMainMsat { get; init; }

    public ulong RemoteMainMsat { get; init; }

    /// <summary>The reserve the peer requires of us, and the one we require of it (sat).</summary>
    public ulong LocalReserveSatoshis { get; init; }

    public ulong RemoteReserveSatoshis { get; init; }

    /// <summary>A splice-out we initiate: the amount that leaves the channel, and where it goes.</summary>
    public LightningMoney? SpliceOutAmount { get; init; }

    /// <summary>Our wallet contribution chosen before quiescence (a splice-in we initiate), or null.</summary>
    public InteractiveTxContribution? WalletContribution { get; set; }

    /// <summary>The shared input and output (set once both funding keys are known).</summary>
    public SharedFundingSpec? SharedFunding { get; set; }

    /// <summary>The script of the new funding output (set once both funding keys are known).</summary>
    public BitcoinScript? NewFundingScript { get; set; }

    /// <summary>The interactive-tx host of this negotiation (set when the driver starts).</summary>
    public SpliceNegotiationHost? Host { get; set; }

    /// <summary>The new funding (set when the transaction is constructed, SP-CS-01).</summary>
    public ChannelFunding? NewFunding { get; set; }

    /// <summary>The peer's <c>commitment_signed</c> for the new funding was verified and saved (SP-CS-02).</summary>
    public bool CommitmentSignedReceived { get; set; }

    /// <summary>What the completion staged (both <c>tx_signatures</c> exchanged); applied to memory after the save.</summary>
    public StagedCompletion? Completion { get; set; }

    /// <summary>The waiter of <see cref="SpliceService.StartAsync"/> (ours) or of nobody (the peer's splice).</summary>
    public TaskCompletionSource<SpliceResult> Result { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>A negotiation that blocks another <c>splice_init</c> (SP-S-01, SP-R-01).</summary>
    public bool IsInProgress => State is SpliceNegotiationState.InitSent or SpliceNegotiationState.Negotiating
                                         or SpliceNegotiationState.CommitmentSigned;

    /// <summary>The negotiation's result now.</summary>
    public SpliceResult ToResult(string? failureReason = null) =>
        new(ChannelId, State, Model.SpliceTxId, NewFunding?.CapacitySatoshis, failureReason);

    /// <summary>The writes the completion staged, for the memory update after the save.</summary>
    internal sealed record StagedCompletion(
        FundingSet Fundings,
        BroadcastTransactionModel Broadcast,
        WatchedTransactionModel Watch);
}