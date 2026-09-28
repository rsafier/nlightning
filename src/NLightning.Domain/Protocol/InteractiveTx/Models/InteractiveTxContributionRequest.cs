namespace NLightning.Domain.Protocol.InteractiveTx.Models;

using Channels.ValueObjects;
using Enums;
using Money;

/// <summary>
/// What <see cref="Interfaces.IInteractiveTxContributor.ContributeAsync"/> must fund.
/// </summary>
/// <param name="ChannelId">The channel (for the reservation's purpose and logs).</param>
/// <param name="Purpose">The protocol the negotiation serves.</param>
/// <param name="WalletAmount">What our wallet must add to the shared output (a splice-in or our dual-funding share);
/// zero when we add no wallet input for it.</param>
/// <param name="Outputs">Outputs we must add besides change (a splice-out destination), empty for none.</param>
/// <param name="FeeratePerKw">The agreed feerate (sat/kw).</param>
/// <param name="ExtraWeight">Weight we pay for besides our own inputs and outputs: as initiator the common fields and,
/// for a splice, the shared input and output (IT-S-03); zero as non-initiator.</param>
/// <param name="RequireConfirmedInputs">The peer sent <c>require_confirmed_inputs</c>: pick only confirmed
/// outputs.</param>
/// <param name="FundWeightWithoutAmount">With a zero <paramref name="WalletAmount"/>, still add wallet inputs that pay
/// for <paramref name="ExtraWeight"/> and our own weight (the change goes back to the wallet): an accepter that
/// contributed nothing to a dual-funded open and starts its RBF becomes the interactive-tx initiator and pays the common
/// fields and the funding output (BOLT 2 "Fee bumping", NL-530). False: no wallet input without a wallet amount.</param>
public sealed record InteractiveTxContributionRequest(
    ChannelId ChannelId,
    InteractiveTxPurpose Purpose,
    LightningMoney WalletAmount,
    IReadOnlyList<ContributedOutput> Outputs,
    uint FeeratePerKw,
    int ExtraWeight,
    bool RequireConfirmedInputs,
    bool FundWeightWithoutAmount = false);