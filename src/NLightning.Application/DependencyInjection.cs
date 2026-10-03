using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NLightning.Application;

using Accounting;
using Channels.Close;
using Channels.Close.Handlers;
using Channels.DualFunding;
using Channels.Fees;
using Channels.Handlers;
using Channels.Handlers.Interfaces;
using Channels.Interfaces;
using Channels.Managers;
using Channels.Quiescence;
using Channels.Reestablish;
using Channels.Safety;
using Channels.Services;
using Channels.Splicing;
using Channels.Splicing.Handlers;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.Transactions.Factories;
using Domain.Bitcoin.Transactions.Interfaces;
using Domain.Channels.Factories;
using Domain.Channels.Interfaces;
using Domain.Channels.Validators;
using Domain.Crypto.Hashes;
using Domain.Gossip.Interfaces;
using Domain.Node.Interfaces;
using Domain.Node.Options;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Gossip;
using Infrastructure.Bitcoin.Wallet.Interfaces;
using InteractiveTx;
using Node.Bootstrap;
using Node.Managers;
using Node.Services;
using Offers;
using Offers.Send;
using Onchain;
using Onchain.Anchors;
using Onchain.Fees;
using Onchain.Resolvers.Local;
using Onchain.Resolvers.Remote;
using Onchain.Resolvers.Revoked;
using Onchain.Wallet;
using OnionMessages;
using Payments;
using Payments.Send;
using Payments.Switch;
using Protocol.Factories;

/// <summary>
/// Extension methods for setting up application services in an IServiceCollection.
/// </summary>
public static class DependencyInjection
{
    /// <summary>
    /// Adds application layer services to the specified IServiceCollection.
    /// </summary>
    /// <param name="services">The IServiceCollection to add services to.</param>
    /// <returns>The same service collection so that multiple calls can be chained.</returns>
    /// <remarks>
    /// Also registers the Domain channel and transaction factories and the open-channel validator, because Domain has
    /// no DI of its own. They need <see cref="IFeeService"/> (registered by the host) and <see cref="ILightningSigner"/>
    /// (registered by <c>AddBitcoinInfrastructure</c>).
    /// </remarks>
    public static IServiceCollection AddApplicationServices(this IServiceCollection services)
    {
        // Domain services that have no DI of their own
        services.AddSingleton<IChannelOpenValidator>(sp =>
        {
            var nodeOptions = sp.GetRequiredService<IOptions<NodeOptions>>().Value;
            return new ChannelOpenValidator(nodeOptions);
        });
        services.AddSingleton<IChannelFactory>(sp =>
        {
            var channelIdFactory = sp.GetRequiredService<IChannelIdFactory>();
            var channelOpenValidator = sp.GetRequiredService<IChannelOpenValidator>();
            var feeService = sp.GetRequiredService<IFeeService>();
            var lightningSigner = sp.GetRequiredService<ILightningSigner>();
            var nodeOptions = sp.GetRequiredService<IOptions<NodeOptions>>().Value;
            var sha256 = sp.GetRequiredService<ISha256>();
            return new ChannelFactory(channelIdFactory, channelOpenValidator, feeService, lightningSigner, nodeOptions,
                                      sha256);
        });
        services.AddSingleton<ICommitmentTransactionModelFactory, CommitmentTransactionModelFactory>();
        services.AddSingleton<IFundingTransactionModelFactory, FundingTransactionModelFactory>();

        // Singleton services (one instance throughout the application)
        services.AddSingleton<IChannelLockProvider, ChannelLockProvider>();
        // The graceful shutdown's drain flag (NL-591), read by every gate that refuses new activity while it is set
        services.AddSingleton<INodeDrainState, NodeDrainState>();
        // What a `shutdown --wait` waits for (NL-592): HTLCs in flight and mid-flight negotiations
        services.TryAddSingleton<NodeBusyStateMonitor>();
        services.TryAddSingleton<INodeBusyStateMonitor>(sp => sp.GetRequiredService<NodeBusyStateMonitor>());
        services.AddSingleton(sp =>
        {
            var blockchainMonitor = sp.GetRequiredService<IBlockchainMonitor>();
            var channelLockProvider = sp.GetRequiredService<IChannelLockProvider>();
            var channelMemoryRepository = sp.GetRequiredService<IChannelMemoryRepository>();
            var lightningSigner = sp.GetRequiredService<ILightningSigner>();
            var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
            return new ChannelManager(blockchainMonitor, channelLockProvider, channelMemoryRepository,
                                      loggerFactory.CreateLogger<ChannelManager>(), lightningSigner, sp);
        });
        services.AddSingleton<IChannelManager>(sp => sp.GetRequiredService<ChannelManager>());
        services.AddSingleton<IChannelMessagePublisher>(sp => sp.GetRequiredService<ChannelManager>());
        services.AddSingleton<IMessageFactory, MessageFactory>();
        // Liquidity ads (NL-771): the seller and buyer rules shared by the dual-funded open and the splice
        services.TryAddSingleton<LiquidityAds.LiquidityAdsService>();
        services.AddCommitmentEngineServices();
        services.AddChannelStateTransitionServices();
        services.AddReestablishServices();
        services.AddChannelOperationsServices();
        services.AddChannelCloseServices();
        services.AddGossipServices();
        services.AddPaymentsServices();
        services.AddHtlcSwitchServices();
        services.AddPaymentSendServices();
        services.AddChannelSafetyServices();
        services.AddOnchainServices();
        // O7: the wallet's fee inputs (O7-T1) for anchors HTLC transactions (O7-T3) and CPFP children (O7-T2)
        services.AddAnchorWalletServices();
        // BOLT 5 resolvers the on-chain executor dispatches to by close kind (ABCD W5-B/C/D)
        services.AddLocalCommitResolutionServices();
        services.AddRemoteCommitResolutionServices();
        services.AddRevokedCommitResolver();
        services.AddAnchorCpfpServices();
        // BOLT 4 onion messages (wave M6): off until option_onion_messages is advertised
        services.AddOnionMessageServices();
        // BOLT 12 offers (wave B12): the receive side (type-64 invoice_request handler, over the payments'
        // BlindedPathBuilder) and the payer side (over the onion messages and PaymentService.PayBlindedAsync); both
        // stay unavailable without an IBolt12Signer, onion messages and route blinding
        services.AddOffersServices();
        services.AddOfferSendServices();
        // BOLT 2 channel quiescence (splicing plan wave Q): stfu handling, owed-stfu release and the timeout monitor;
        // option_quiesce stays experimental, so it is only used when the feature is negotiated
        services.AddQuiescenceServices();
        // BOLT 2 interactive transaction construction (splicing plan wave IT): the wallet contributor over the
        // Infrastructure.Bitcoin builder and prevtx inspector, and the driver the tx_* handlers go through
        services.AddInteractiveTxContributorServices();
        services.AddInteractiveTxServices();
        // BOLT 2 channel splicing (splicing plan wave SP1, lane SP1-D): splice_init/ack/locked over quiescence and the
        // interactive-tx driver; option_splice stays experimental until Proof SP2 (D13)
        services.AddSpliceServices();
        // Splice RBF auto-bump (wave SPR, SPR-T3): off unless Splice:AutoBumpAfterBlocks is set; the host starts it
        services.AddSpliceAutoBumper();
        // BOLT 2 dual-funded opens (splicing plan wave DF): open_channel2/accept_channel2 over the interactive-tx driver;
        // option_dual_fund stays experimental
        services.AddDualFundingServices();
        services.AddSingleton<IPeerManager>(sp =>
        {
            var peerManager = ActivatorUtilities.CreateInstance<PeerManager>(sp);
            // M6 OM2-T1: the per-connection onion message outbox cap (OnionMessages:MaxOutboxPerPeer)
            var onionMessageOptions = sp.GetService<IOptions<OnionMessageOptions>>()?.Value;
            if (onionMessageOptions is { MaxOutboxPerPeer: > 0 })
                peerManager.MaxOutboxOnionMessagesPerPeer = onionMessageOptions.MaxOutboxPerPeer;
            return peerManager;
        });
        // BOLT 10 DNS seed bootstrap (NL-113): off unless Node:Bootstrap:Enabled; the host starts it after the peers
        services.AddSingleton<IPeerBootstrapService, PeerBootstrapService>();
        // NL-351: own and relayed gossip goes through the peer's outbox (PeerGossipSender resolves it lazily)
        services.AddSingleton<IPeerGossipOutbox>(sp => (IPeerGossipOutbox)sp.GetRequiredService<IPeerManager>());
        // M6 OM2-T1: onion messages go through the peer's outbox as a bounded low-priority class
        services.AddSingleton<IPeerOnionMessageOutbox>(sp =>
                                                           (IPeerOnionMessageOutbox)sp
                                                              .GetRequiredService<IPeerManager>());

        // Register every channel message handler explicitly (NL-055): trim/AOT-safe, see
        // AddChannelMessageHandlers and ChannelMessageHandlerRegistrationTests (the guard test)
        services.AddChannelMessageHandlers();

        // Add scoped services
        services.AddScoped<FundingConfirmedMessageHandler>();

        // The accounting feed's sealer and balance snapshots (NL-602); the host starts the sealer after the chain monitor
        services.AddAccountingServices();

        // Last: decorates the IHtlcSwitch registered above with the dust exposure check (N9-T3)
        services.AddChannelFeeServices();

        return services;
    }

    /// <summary>
    /// Registers every channel message handler of this assembly, each as a scoped
    /// <c>IChannelMessageHandler&lt;TMessage&gt;</c> (one closed interface per handled message type).
    /// </summary>
    /// <remarks>
    /// The list is explicit (NL-055): the previous <c>Assembly.GetTypes()</c> scan could lose handler types to the IL
    /// trimmer under trimming/AOT, while <see cref="ServiceCollectionServiceExtensions.AddScoped{TService, TImplementation}"/>
    /// registrations keep every handler alive. <c>ChannelMessageHandlerRegistrationTests</c> fails when a handler
    /// implementation exists in this assembly without being on this list (or the other way around), so the list cannot
    /// rot. New handlers are added here, with the message's <c>case</c> in <c>ChannelManager.HandleChannelMessageAsync</c>.
    /// </remarks>
    private static void AddChannelMessageHandlers(this IServiceCollection services)
    {
        // BOLT 2 v1 channel opening (open_channel -> accept_channel -> funding_created/signed -> channel_ready)
        services.AddScoped<IChannelMessageHandler<OpenChannel1Message>, OpenChannel1MessageHandler>();
        services.AddScoped<IChannelMessageHandler<AcceptChannel1Message>, AcceptChannel1MessageHandler>();
        services.AddScoped<IChannelMessageHandler<FundingCreatedMessage>, FundingCreatedMessageHandler>();
        services.AddScoped<IChannelMessageHandler<FundingSignedMessage>, FundingSignedMessageHandler>();
        services.AddScoped<IChannelMessageHandler<ChannelReadyMessage>, ChannelReadyMessageHandler>();
        // BOLT 2 normal operation (N6) and quiescence (stfu)
        services.AddScoped<IChannelMessageHandler<CommitmentSignedMessage>, CommitmentSignedMessageHandler>();
        services.AddScoped<IChannelMessageHandler<RevokeAndAckMessage>, RevokeAndAckMessageHandler>();
        services.AddScoped<IChannelMessageHandler<UpdateAddHtlcMessage>, UpdateAddHtlcMessageHandler>();
        services.AddScoped<IChannelMessageHandler<UpdateFulfillHtlcMessage>, UpdateFulfillHtlcMessageHandler>();
        services.AddScoped<IChannelMessageHandler<UpdateFailHtlcMessage>, UpdateFailHtlcMessageHandler>();
        services.AddScoped<IChannelMessageHandler<UpdateFailMalformedHtlcMessage>, UpdateFailMalformedHtlcMessageHandler>();
        services.AddScoped<IChannelMessageHandler<UpdateFeeMessage>, UpdateFeeMessageHandler>();
        services.AddScoped<IChannelMessageHandler<StfuMessage>, StfuMessageHandler>();
        // BOLT 2 channel_reestablish (N7) and announcement_signatures (the channel's gossip flow, NL-342)
        services.AddScoped<IChannelMessageHandler<ChannelReestablishMessage>, ChannelReestablishMessageHandler>();
        services.AddScoped<IChannelMessageHandler<AnnouncementSignaturesMessage>, AnnouncementSignaturesMessageHandler>();
        // BOLT 2 cooperative close (N10: shutdown/closing_signed) and option_simple_close (N11: closing_complete/sig)
        services.AddScoped<IChannelMessageHandler<ShutdownMessage>, ShutdownMessageHandler>();
        services.AddScoped<IChannelMessageHandler<ClosingSignedMessage>, ClosingSignedMessageHandler>();
        services.AddScoped<IChannelMessageHandler<ClosingCompleteMessage>, ClosingCompleteMessageHandler>();
        services.AddScoped<IChannelMessageHandler<ClosingSigMessage>, ClosingSigMessageHandler>();
        // BOLT 2 interactive transaction construction (types 66-74; the handlers share InteractiveTxMessageHandler<T>)
        services.AddScoped<IChannelMessageHandler<TxAddInputMessage>, TxAddInputMessageHandler>();
        services.AddScoped<IChannelMessageHandler<TxRemoveInputMessage>, TxRemoveInputMessageHandler>();
        services.AddScoped<IChannelMessageHandler<TxAddOutputMessage>, TxAddOutputMessageHandler>();
        services.AddScoped<IChannelMessageHandler<TxRemoveOutputMessage>, TxRemoveOutputMessageHandler>();
        services.AddScoped<IChannelMessageHandler<TxCompleteMessage>, TxCompleteMessageHandler>();
        services.AddScoped<IChannelMessageHandler<TxSignaturesMessage>, TxSignaturesMessageHandler>();
        services.AddScoped<IChannelMessageHandler<TxAbortMessage>, TxAbortMessageHandler>();
        services.AddScoped<IChannelMessageHandler<TxInitRbfMessage>, TxInitRbfMessageHandler>();
        services.AddScoped<IChannelMessageHandler<TxAckRbfMessage>, TxAckRbfMessageHandler>();
        // BOLT 2 splicing (splice_init/ack/locked) and v2 dual-funded opening (open_channel2/accept_channel2)
        services.AddScoped<IChannelMessageHandler<SpliceInitMessage>, SpliceInitMessageHandler>();
        services.AddScoped<IChannelMessageHandler<SpliceAckMessage>, SpliceAckMessageHandler>();
        services.AddScoped<IChannelMessageHandler<SpliceLockedMessage>, SpliceLockedMessageHandler>();
        services.AddScoped<IChannelMessageHandler<OpenChannel2Message>, OpenChannel2MessageHandler>();
        services.AddScoped<IChannelMessageHandler<AcceptChannel2Message>, AcceptChannel2MessageHandler>();
    }
}