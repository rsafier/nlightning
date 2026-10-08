using Microsoft.Extensions.Logging;

namespace NLightning.Application.Channels.Handlers;

using Accounting;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.Transactions.Enums;
using Domain.Bitcoin.Transactions.Interfaces;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Crypto.ValueObjects;
using Domain.Exceptions;
using Domain.Node.Options;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Domain.Signing.Vls;
using Infrastructure.Bitcoin.Builders.Interfaces;
using Infrastructure.Bitcoin.Wallet.Interfaces;
using Interfaces;
using Services;

/// <summary>
/// Handles the funder's <c>funding_created</c> (BOLT 2): the fundee derives the real channel id from the funding
/// outpoint, checks the funder's signature of its initial commitment, signs the funder's initial commitment, persists
/// the channel as V1FundingSigned and answers with <c>funding_signed</c>.
/// </summary>
/// <remarks>
/// The non-initiator flow against BOLT 2, as reviewed (NL-053): a <c>funding_created</c> for a channel we did not
/// negotiate (or not in V1Opening) fails the channel with an `error` naming the temporary channel id; a channel id
/// already derived from the same funding outpoint fails it too, before anything is registered or signed. The funder's
/// signature is validated over our initial commitment (number 0, NL-188) before we sign theirs, and a bad signature
/// surfaces as a <see cref="SignerException"/> (a <see cref="ChannelErrorException"/>): the channel is failed with the
/// signer's peer message and never persisted, so it is forgotten (BOLT 2: the fundee SHOULD forget it). On success the
/// channel is persisted (one save) before the reply, the funding transaction is watched at the channel's minimum depth
/// (the watch row is persisted with it), and <see cref="ChannelModel.FundingCreatedAtBlockHeight"/> starts the 2016
/// blocks after which a never-confirming funding is forgotten (BOLT 2).
/// </remarks>
public class FundingCreatedMessageHandler : IChannelMessageHandler<FundingCreatedMessage>
{
    private readonly IBlockchainMonitor _blockchainMonitor;
    private readonly IChannelIdFactory _channelIdFactory;
    private readonly IChannelMemoryRepository _channelMemoryRepository;
    private readonly ICommitmentTransactionBuilder _commitmentTransactionBuilder;
    private readonly ICommitmentTransactionModelFactory _commitmentTransactionModelFactory;
    private readonly ILightningSigner _lightningSigner;
    private readonly ChannelStateTransitionService? _transitions;
    private readonly NativeV1ChannelOpening? _nativeOpening;
    private readonly NativeV1FundedInboundOpening? _nativeFundedOpening;
    private readonly ILogger<FundingCreatedMessageHandler> _logger;
    private readonly IMessageFactory _messageFactory;
    private readonly IUnitOfWork _unitOfWork;

    public FundingCreatedMessageHandler(IBlockchainMonitor blockchainMonitor, IChannelIdFactory channelIdFactory,
                                        IChannelMemoryRepository channelMemoryRepository,
                                        ICommitmentTransactionBuilder commitmentTransactionBuilder,
                                        ICommitmentTransactionModelFactory commitmentTransactionModelFactory,
                                        ILightningSigner lightningSigner, ILogger<FundingCreatedMessageHandler> logger,
                                        IMessageFactory messageFactory, IUnitOfWork unitOfWork,
                                        ChannelStateTransitionService? transitions = null,
                                        NativeV1ChannelOpening? nativeOpening = null,
                                        NativeV1FundedInboundOpening? nativeFundedOpening = null)
    {
        _nativeFundedOpening = nativeFundedOpening;
        _nativeOpening = nativeOpening;
        _blockchainMonitor = blockchainMonitor;
        _channelIdFactory = channelIdFactory;
        _channelMemoryRepository = channelMemoryRepository;
        _commitmentTransactionBuilder = commitmentTransactionBuilder;
        _commitmentTransactionModelFactory = commitmentTransactionModelFactory;
        _lightningSigner = lightningSigner;
        _transitions = transitions;
        _logger = logger;
        _messageFactory = messageFactory;
        _unitOfWork = unitOfWork;
    }

    public async Task<IReadOnlyList<IChannelMessage>> HandleAsync(
        FundingCreatedMessage message, ChannelState currentState, FeatureOptions negotiatedFeatures,
        CompactPubKey peerPubKey)
    {
        _logger.LogTrace("Processing FundingCreatedMessage with ChannelId: {ChannelId} from Peer: {PeerPubKey}",
                         message.Payload.ChannelId, peerPubKey);

        var payload = message.Payload;

        if (_nativeFundedOpening is { Enabled: true }
         && await _nativeFundedOpening.TryHandleRetainedAsync(message, peerPubKey, _unitOfWork) is { } retained)
            return [retained];

        if (currentState != ChannelState.None)
            throw new ChannelErrorException("A channel with this id already exists", payload.ChannelId);

        // Check if there's a temporary channel for this peer
        if (!_channelMemoryRepository.TryGetTemporaryChannelState(peerPubKey, payload.ChannelId, out currentState))
            throw new ChannelErrorException("This channel has never been negotiated", payload.ChannelId);

        if (currentState != ChannelState.V1Opening)
            throw new ChannelErrorException("Channel had the wrong state", payload.ChannelId,
                                            "This channel is already being negotiated with peer");

        // Get the channel and set missing props
        if (!_channelMemoryRepository.TryGetTemporaryChannel(peerPubKey, payload.ChannelId, out var channel))
            throw new ChannelErrorException("Temporary channel not found", payload.ChannelId);

        // The temporary channel is born with its funding output (ChannelFactory.CreateChannelV1AsNonInitiatorAsync)
        channel.FundingOutput!.TransactionId = payload.FundingTxId;
        channel.FundingOutput.Index = payload.FundingOutputIndex;

        // Create a new channelId
        var oldChannelId = channel.ChannelId;
        channel.UpdateChannelId(_channelIdFactory.CreateV1(payload.FundingTxId, payload.FundingOutputIndex));

        // The peer must not reuse a funding outpoint; fail the channel before anything is registered or signed
        if (await _unitOfWork.ChannelDbRepository.GetByIdAsync(channel.ChannelId) is not null)
            throw new ChannelErrorException("A channel with this funding outpoint already exists", channel.ChannelId,
                                            "This channel is already in our database");

        if (_nativeFundedOpening is { Enabled: true })
        {
            var reply = await _nativeFundedOpening.StartAsync(channel, oldChannelId, message,
                negotiatedFeatures, _unitOfWork);
            _channelMemoryRepository.AddChannel(channel);
            _channelMemoryRepository.TryRemoveTemporaryChannel(peerPubKey, oldChannelId);
            return [reply];
        }

        using var nativeInitial = _nativeOpening is { Enabled: true }
            ? await _nativeOpening.BeginInitialCommitAsync(channel, oldChannelId) : null;
        nativeInitial?.Activate();
        // Register the channel with the signer
        _lightningSigner.RegisterChannel(channel.ChannelId, channel.GetSigningInfo());
        using var openingWorkflow = _lightningSigner is IVlsChannelSigner
            ? await (_transitions ?? throw new InvalidOperationException("VLS opening requires transitions."))
                   .BeginOpeningAsync(channel)
            : null;
        openingWorkflow?.Activate();
        if (_lightningSigner is IVlsChannelSigner setupSigner) setupSigner.EnsureChannelSetup(channel);

        // Generate the base commitment transactions
        var localCommitmentTransaction =
            _commitmentTransactionModelFactory.CreateCommitmentTransactionModel(channel, CommitmentSide.Local,
                                                                                channel.LocalCommitmentNumber);
        var remoteCommitmentTransaction =
            _commitmentTransactionModelFactory.CreateCommitmentTransactionModel(channel, CommitmentSide.Remote,
                                                                                channel.RemoteCommitmentNumber);

        // Build the output and the transactions
        var localUnsignedCommitmentTransaction = _commitmentTransactionBuilder.Build(localCommitmentTransaction);
        var remoteUnsignedCommitmentTransaction = _commitmentTransactionBuilder.Build(remoteCommitmentTransaction);

        FundingSignedMessage fundingSignedMessage;
        if (channel.ChannelParams.OptionSimpleTaproot)
        {
            // Simple taproot channels (bolt-simple-taproot.md §funding_created, NL-877 T5): the funder's MuSig2 partial
            // signature of our commitment 0 is partial_signature_with_nonce (required), checked against our
            // verification nonce of accept_channel; ours of its commitment 0 is made against its open_channel nonce
            // with a fresh signing nonce
            if (message.PartialSignatureWithNonceTlv is not { } partialTlv)
            {
                _lightningSigner.UnregisterChannel(channel.ChannelId);
                throw new ChannelErrorException("funding_created of a simple taproot channel without "
                                              + "partial_signature_with_nonce", channel.ChannelId,
                                                "funding_created without partial_signature_with_nonce");
            }

            try
            {
                _lightningSigner.ValidateLocalCommitmentPartialSignature(
                    channel.ChannelId, null, channel.LocalCommitmentNumber, partialTlv.PartialSignatureWithNonce,
                    localUnsignedCommitmentTransaction);
                var ourPartial = _lightningSigner.SignRemoteCommitmentPartial(
                    channel.ChannelId, null, remoteUnsignedCommitmentTransaction,
                    channel.RemoteOpeningNonce ?? throw new InvalidOperationException(
                                                      $"No opening nonce of the peer for channel {channel.ChannelId}"));
                channel.UpdateLastReceivedPartialSignature(partialTlv.PartialSignatureWithNonce);
                fundingSignedMessage = _messageFactory.CreateFundingSignedMessage(channel.ChannelId, ourPartial);
            }
            catch
            {
                _lightningSigner.UnregisterChannel(channel.ChannelId);
                throw;
            }
        }
        else
        {
            // Validate remote signature for our local commitment transaction
            if (_lightningSigner is IVlsChannelSigner vls)
                vls.ValidateHolderCommitment(channel, localCommitmentTransaction, payload.Signature, []);
            else
                _lightningSigner.ValidateSignature(channel.ChannelId, payload.Signature,
                                                   localUnsignedCommitmentTransaction);

            // Sign our remote commitment transaction
            var ourSignature =
                _lightningSigner is IVlsChannelSigner remoteSigner
                    ? remoteSigner.SignCounterpartyCommitment(channel, remoteCommitmentTransaction).Signature
                    : _lightningSigner.SignChannelTransaction(channel.ChannelId, remoteUnsignedCommitmentTransaction);

            // Update the channel with the new signatures
            channel.UpdateLastReceivedSignature(payload.Signature);
            channel.UpdateLastSentSignature(ourSignature);
            fundingSignedMessage = _messageFactory.CreateFundingSignedMessage(channel.ChannelId, ourSignature);
        }

        channel.UpdateState(ChannelState.V1FundingSigned);

        // Remember when we started waiting for the funding transaction, so we can forget the channel if it never
        // confirms (BOLT 2: the fundee SHOULD forget the channel after 2016 blocks)
        channel.FundingCreatedAtBlockHeight = _blockchainMonitor.LastProcessedBlockHeight;

        // Save to the database, with the opener's push (NL-605): as fundee our balance at the open is exactly what the
        // opener pushed to us (ChannelFactory.CreateChannelV1AsNonInitiatorAsync), 0 for none
        await _unitOfWork.ChannelDbRepository.AddAsync(channel);
        if (_nativeOpening is { Enabled: true })
        {
            await nativeInitial!.StageConsumeAsync(_unitOfWork);
            nativeInitial.Dispose();
            await _nativeOpening.StageConsumeAsync(channel, oldChannelId, _unitOfWork);
        }
        await ChannelAccountingEvents.StagePushAmountAsync(_unitOfWork, channel.ChannelId, channel.LocalBalance,
                                                           _logger);
        if (openingWorkflow is not null)
            await _transitions!.StageOpeningCompletionAsync(channel, openingWorkflow, _unitOfWork, true);
        await _unitOfWork.SaveChangesAsync();
        openingWorkflow?.Dispose();
        if (_lightningSigner is IVlsChannelSigner) await _transitions!.ActivateOpeningAsync(channel);

        // Add the channel to the dictionary
        _channelMemoryRepository.AddChannel(channel);

        // Remove the temporary channel
        _channelMemoryRepository.TryRemoveTemporaryChannel(peerPubKey, oldChannelId);

        await _blockchainMonitor.WatchTransactionAsync(channel.ChannelId, payload.FundingTxId,
                                                       channel.ChannelParams.MinimumDepth);

        return [fundingSignedMessage];
    }
}