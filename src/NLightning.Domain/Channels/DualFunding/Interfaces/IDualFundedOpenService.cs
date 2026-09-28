namespace NLightning.Domain.Channels.DualFunding.Interfaces;

using Crypto.ValueObjects;
using Models;
using Money;
using Node.Options;
using Persistence.Interfaces;
using Protocol.Interfaces;
using Protocol.Messages;
using ValueObjects;

/// <summary>
/// Dual-funded (v2) channel opens (BOLT 2 "Channel Establishment v2", <c>open_channel2</c> 64 /
/// <c>accept_channel2</c> 65, <c>option_dual_fund</c> 28/29; splicing plan wave DF, lane SP1-F). Implemented by an
/// Application service over the interactive-tx driver, whose host is an Application <c>IInteractiveTxHost</c> with
/// purpose <c>InteractiveTxPurpose.DualFund</c> (<c>DualFundRbf</c> for an RBF): no shared input, the funding output
/// as the shared output, the first commitment (zero HTLCs) as the commitment step, the funding confirmation path at
/// completion.
/// </summary>
/// <remarks>
/// Lock discipline as <see cref="Splicing.Interfaces.ISpliceService"/>: <see cref="OpenAsync"/> and
/// <see cref="BumpAsync"/> take the (temporary) channel's lock themselves; the <c>Handle*</c>/<see cref="AcceptAsync"/>
/// members run under the lock <c>ChannelManager</c> holds for the message and return the replies in wire order.
/// <c>option_dual_fund</c> stays experimental until Proof DF (<c>FeatureOptions.DualFund</c>).
/// </remarks>
public interface IDualFundedOpenService
{
    /// <summary>
    /// Opens a channel as initiator with our contribution: sends <c>open_channel2</c> (temporary id from
    /// <see cref="ChannelIdV2.DeriveTemporary"/>) and drives the negotiation once <c>accept_channel2</c> arrives.
    /// </summary>
    /// <returns>Completes once both <c>tx_signatures</c> were exchanged or the open ended.</returns>
    /// <exception cref="InvalidOperationException"><c>option_dual_fund</c> not negotiated with the peer, the peer not
    /// connected, or the wallet cannot fund the contribution.</exception>
    Task<DualFundedOpenResult> OpenAsync(DualFundedOpenRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Under the lock: accepts the peer's <c>open_channel2</c> as non-initiator with
    /// <paramref name="localContribution"/> (zero for none; the handler's policy decides): <c>accept_channel2</c>
    /// with the temporary id, then the driver starts as non-initiator. A refused open is answered with an
    /// <c>error</c> for the temporary channel.
    /// </summary>
    Task<IReadOnlyList<IChannelMessage>> AcceptAsync(OpenChannel2Message message, FeatureOptions negotiatedFeatures,
                                                     CompactPubKey peerPubKey, LightningMoney localContribution,
                                                     IUnitOfWork unitOfWork,
                                                     CancellationToken cancellationToken = default);

    /// <summary>
    /// Under the lock: the peer's <c>accept_channel2</c> to our <c>open_channel2</c>: derives the v2 channel id and
    /// starts the driver as initiator.
    /// </summary>
    Task<IReadOnlyList<IChannelMessage>> HandleAcceptChannel2Async(AcceptChannel2Message message,
                                                                   FeatureOptions negotiatedFeatures,
                                                                   CompactPubKey peerPubKey, IUnitOfWork unitOfWork,
                                                                   CancellationToken cancellationToken = default);

    /// <summary>
    /// RBF of our unconfirmed dual-funded open (<c>tx_init_rbf</c>, IT-RBF-01 feerate floor): the peer's answer
    /// (<c>tx_ack_rbf</c> or <c>tx_abort</c>) goes through the interactive-tx handlers. A peer's <c>tx_init_rbf</c> for
    /// the open reaches the host's <c>OnRbfRequestedAsync</c>.
    /// </summary>
    /// <exception cref="InvalidOperationException">No unconfirmed dual-funded open on the channel, we are not its
    /// initiator, or the feerate is below the floor.</exception>
    Task<DualFundedOpenResult> BumpAsync(ChannelId channelId, uint feeratePerKw,
                                         CancellationToken cancellationToken = default);

    /// <summary>
    /// <see cref="BumpAsync(ChannelId, uint, CancellationToken)"/> with our <c>funding_output_contribution</c> changed
    /// to <paramref name="localContribution"/> (BOLT 2: the sender "MAY set <c>funding_output_contribution</c> to a
    /// different value", NL-521); null keeps it. The default serves implementations without that support.
    /// </summary>
    /// <exception cref="NotSupportedException">A new contribution and an implementation that cannot change it.</exception>
    Task<DualFundedOpenResult> BumpAsync(ChannelId channelId, uint feeratePerKw, LightningMoney? localContribution,
                                         CancellationToken cancellationToken = default) =>
        localContribution is null
            ? BumpAsync(channelId, feeratePerKw, cancellationToken)
            : throw new NotSupportedException("Changing our contribution in an RBF is not supported");
}