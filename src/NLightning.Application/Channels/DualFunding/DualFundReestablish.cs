using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace NLightning.Application.Channels.DualFunding;

using Domain.Channels.Enums;
using Domain.Channels.Models;
using Domain.Persistence.Interfaces;
using Domain.Protocol.InteractiveTx.Enums;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Domain.Protocol.Tlv;
using InteractiveTx;
using InteractiveTx.Interfaces;

/// <summary>
/// <c>channel_reestablish</c> <c>next_funding</c> for an unsigned or unconfirmed dual-funded open (BOLT 2 "Message
/// Retransmission"; splicing plan wave DF, DF2), minimal: our own TLV when we sent <c>commitment_signed</c> for the
/// open's interactive transaction and did not receive <c>tx_signatures</c>, and the retransmissions a peer's TLV
/// asks for. Scoped; called by the reestablish code under the channel's lock.
/// </summary>
public sealed class DualFundReestablish
{
    /// <summary><c>retransmit_flags</c> bit 0: the <c>commitment_signed</c>.</summary>
    public const byte RetransmitCommitmentSigned = 1;

    private readonly ILogger<DualFundReestablish> _logger;
    private readonly DualFundedOpenService _service;
    private readonly IServiceProvider _serviceProvider;
    private readonly IUnitOfWork _unitOfWork;

    public DualFundReestablish(ILogger<DualFundReestablish> logger, DualFundedOpenService service,
                               IServiceProvider serviceProvider, IUnitOfWork unitOfWork)
    {
        _logger = logger;
        _service = service;
        _serviceProvider = serviceProvider;
        _unitOfWork = unitOfWork;
    }

    /// <summary>
    /// BOLT 2 (sender): "if it has sent <c>commitment_signed</c> for an interactive transaction construction but it
    /// has not received <c>tx_signatures</c>: MUST include the <c>next_funding</c> TLV ... if it has not received
    /// <c>commitment_signed</c> for this <c>next_funding_txid</c>: MUST set the <c>commitment_signed</c> bit". Null
    /// otherwise, and for every channel that is not a <see cref="ChannelVersion.V2"/> open waiting for its funding.
    /// </summary>
    public async Task<NextFundingTlv?> GetOwnNextFundingAsync(ChannelModel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        if (channel is not { Version: ChannelVersion.V2, State: ChannelState.V1FundingSigned })
            return null;

        var session = (await _unitOfWork.InteractiveTxSessionDbRepository.GetByChannelIdAsync(channel.ChannelId))
                     .Where(s => s.State != InteractiveTxSessionState.Aborted && s.ConstructedTx is not null)
                     .MaxBy(s => s.CreatedAt);
        if (session is not { CommitmentSignedSent: true, TxSignaturesReceived: false })
            return null;

        var flags = session.CommitmentSignedReceived ? (byte)0 : RetransmitCommitmentSigned;
        return new NextFundingTlv(session.ConstructedTx!.TxId, flags);
    }

    /// <summary>
    /// BOLT 2 (receiver of <c>next_funding</c>): for the latest interactive funding transaction, our
    /// <c>commitment_signed</c> again when the flag asks for it and, when we already sent it, our <c>tx_signatures</c>
    /// (we send it only once the peer's <c>commitment_signed</c> arrived and either we sign first or the peer's
    /// <c>tx_signatures</c> arrived, which are the cases where the reestablish MUST send it); any other txid gets
    /// <c>tx_abort</c>. Null for a channel that is not a dual-funded open waiting for its funding (the v1 rules apply).
    /// </summary>
    public async Task<IReadOnlyList<IChannelMessage>?> RespondAsync(ChannelModel channel,
                                                                    ChannelReestablishMessage message)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(message);
        if (channel is not { Version: ChannelVersion.V2, State: ChannelState.V1FundingSigned }
         || message.NextFundingTlv is not { } nextFunding)
            return null;

        var negotiation = await _service.GetOrLoadAsync(channel.ChannelId, _unitOfWork, CancellationToken.None);
        var requested = new Domain.Bitcoin.ValueObjects.TxId(nextFunding.NextFundingTxId);
        var isPending = negotiation?.PendingTxId is { } pending && pending.Equals(requested);
        var isSigned = negotiation?.CompletedTxIds.Count > 0 && negotiation.CompletedTxIds[^1].Equals(requested);
        if (negotiation is null || (!isPending && !isSigned))
        {
            _logger.LogInformation("next_funding {TxId} of channel {ChannelId} is not our latest funding; tx_abort",
                                   requested, channel.ChannelId);
            return [InteractiveTxDriver.CreateTxAbort(channel.ChannelId, "unknown next_funding_txid")];
        }

        var replies = new List<IChannelMessage>();
        if (isPending && (nextFunding.RetransmitFlags & RetransmitCommitmentSigned) != 0
                      && _service.CreateCommitmentSignedRetransmission(negotiation) is { } commitmentSigned)
            replies.Add(commitmentSigned);

        if (_serviceProvider.GetService<IInteractiveTxDriver>()?.CreateTxSignaturesRetransmission(
                channel.ChannelId, requested) is { } txSignatures)
            replies.Add(txSignatures);

        _logger.LogInformation("next_funding {TxId} of channel {ChannelId}: retransmitting {Count} message(s)",
                               requested, channel.ChannelId, replies.Count);
        return replies;
    }
}