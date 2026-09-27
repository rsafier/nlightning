namespace NLightning.Domain.Channels.DualFunding.Models;

using Bitcoin.ValueObjects;
using ValueObjects;

/// <summary>
/// The outcome of <see cref="Interfaces.IDualFundedOpenService.OpenAsync"/> or <c>BumpAsync</c>.
/// </summary>
/// <param name="ChannelId">The v2 channel id (<see cref="ChannelIdV2.Derive"/>), or the temporary id when the open
/// ended before <c>accept_channel2</c>.</param>
/// <param name="FundingTxId">The funding transaction id, once constructed.</param>
/// <param name="FailureReason">Why the open did not go through, or null.</param>
public sealed record DualFundedOpenResult(ChannelId ChannelId, TxId? FundingTxId = null, string? FailureReason = null);