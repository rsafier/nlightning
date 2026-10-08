using Microsoft.Extensions.Logging;
using NLightning.Domain.Persistence.Interfaces;

namespace NLightning.Application.Channels.Handlers;

using Domain.Bitcoin.Enums;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.Transactions.Enums;
using Domain.Bitcoin.Transactions.Interfaces;
using Domain.Bitcoin.Transactions.Outputs;
using Domain.Bitcoin.ValueObjects;
using Domain.Bitcoin.Wallet.Models;
using Domain.Channels.Closing;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.Validators.Parameters;
using Domain.Channels.ValueObjects;
using Domain.Crypto.Hashes;
using Domain.Crypto.Interfaces;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Exceptions;
using Domain.Node.Options;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Domain.Protocol.Models;
using Domain.Signing.Vls;
using Infrastructure.Bitcoin.Builders.Interfaces;
using Infrastructure.Bitcoin.Wallet.Interfaces;
using Interfaces;
using Services;
using Taproot;

public class AcceptChannel1MessageHandler : IChannelMessageHandler<AcceptChannel1Message>
{
    /// <summary>
    /// Sizes a P2WPKH change output before a real change address is reserved; never paid to (it is replaced, or the
    /// funding has no change).
    /// </summary>
    private static readonly WalletAddressModel s_changeAddressProbe = new(AddressType.P2Wpkh, 0, true, string.Empty);

    private readonly IBitcoinWalletService _bitcoinWalletService;
    private readonly IChannelIdFactory _channelIdFactory;
    private readonly IChannelMemoryRepository _channelMemoryRepository;
    private readonly IChannelOpenValidator _channelOpenValidator;
    private readonly ICommitmentTransactionBuilder _commitmentTransactionBuilder;
    private readonly ICommitmentTransactionModelFactory _commitmentTransactionModelFactory;
    private readonly IFundingTransactionBuilder _fundingTransactionBuilder;
    private readonly IFundingTransactionModelFactory _fundingTransactionModelFactory;
    private readonly ILightningSigner _lightningSigner;
    private readonly ChannelStateTransitionService? _transitions;
    private readonly NativeV1ChannelOpening? _nativeOpening;
    private readonly NativeV1FundedOutboundOpening? _nativeFundedOutbound;
    private readonly ILogger<OpenChannel1MessageHandler> _logger;
    private readonly IMessageFactory _messageFactory;
    private readonly IMusig2Service? _musig2;
    private readonly ISha256 _sha256;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IUtxoMemoryRepository _utxoMemoryRepository;

    public AcceptChannel1MessageHandler(IBitcoinWalletService bitcoinWalletService, IChannelIdFactory channelIdFactory,
                                        IChannelMemoryRepository channelMemoryRepository,
                                        IChannelOpenValidator channelOpenValidator,
                                        ICommitmentTransactionBuilder commitmentTransactionBuilder,
                                        ICommitmentTransactionModelFactory commitmentTransactionModelFactory,
                                        IFundingTransactionBuilder fundingTransactionBuilder,
                                        IFundingTransactionModelFactory fundingTransactionModelFactory,
                                        ILightningSigner lightningSigner, ILogger<OpenChannel1MessageHandler> logger,
                                        IMessageFactory messageFactory, ISha256 sha256, IUnitOfWork unitOfWork,
                                        IUtxoMemoryRepository utxoMemoryRepository, IMusig2Service? musig2 = null,
                                        ChannelStateTransitionService? transitions = null,
                                        NativeV1ChannelOpening? nativeOpening = null,
                                        NativeV1FundedOutboundOpening? nativeFundedOutbound = null)
    {
        _nativeOpening = nativeOpening;
        _nativeFundedOutbound = nativeFundedOutbound;
        _musig2 = musig2;
        _bitcoinWalletService = bitcoinWalletService;
        _channelIdFactory = channelIdFactory;
        _channelMemoryRepository = channelMemoryRepository;
        _channelOpenValidator = channelOpenValidator;
        _commitmentTransactionBuilder = commitmentTransactionBuilder;
        _commitmentTransactionModelFactory = commitmentTransactionModelFactory;
        _fundingTransactionBuilder = fundingTransactionBuilder;
        _fundingTransactionModelFactory = fundingTransactionModelFactory;
        _lightningSigner = lightningSigner;
        _transitions = transitions;
        _logger = logger;
        _messageFactory = messageFactory;
        _sha256 = sha256;
        _unitOfWork = unitOfWork;
        _utxoMemoryRepository = utxoMemoryRepository;
    }

    public async Task<IReadOnlyList<IChannelMessage>> HandleAsync(
        AcceptChannel1Message message, ChannelState currentState, FeatureOptions negotiatedFeatures,
        CompactPubKey peerPubKey)
    {
        if (_logger.IsEnabled(LogLevel.Trace))
            _logger.LogTrace("Processing AcceptChannel1Message with ChannelId: {ChannelId} from Peer: {PeerPubKey}",
                             message.Payload.ChannelId, peerPubKey);

        var payload = message.Payload;
        if (_nativeFundedOutbound is { Enabled: true }
         && await _nativeFundedOutbound.TryHandleRetainedAsync(message, peerPubKey, _unitOfWork) is { } retained)
            return [retained];

        if (currentState != ChannelState.None)
            throw new ChannelErrorException("A channel with this id already exists", payload.ChannelId);

        // Check if there's a temporary channel for this peer
        if (_channelMemoryRepository.TryGetTemporaryChannelState(peerPubKey, payload.ChannelId, out currentState))
        {
            if (currentState != ChannelState.V1Opening)
            {
                throw new ChannelErrorException("Channel had the wrong state", payload.ChannelId,
                                                "This channel is already being negotiated with peer");
            }
        }

        // Get the temporary channel
        if (!_channelMemoryRepository.TryGetTemporaryChannel(peerPubKey, payload.ChannelId, out var tempChannel))
            throw new ChannelErrorException("Temporary channel not found", payload.ChannelId);

        // BOLT 2: the channel type must be present and equal to the one we sent in open_channel
        if (message.ChannelTypeTlv is null)
            throw new ChannelErrorException("Channel type was not provided", payload.ChannelId);

        var localParams = tempChannel.ChannelParams.Local;
        if (!message.ChannelTypeTlv.Features.HasSameBits(tempChannel.ChannelParams.ToChannelType()))
            throw new ChannelErrorException("Channel type does not match the one we sent", payload.ChannelId,
                                            "channel_type does not match open_channel");

        // BOLT 2: each side's reserve must be at least the other side's dust limit
        if (payload.ChannelReserveAmount < localParams.DustLimitAmount)
            throw new ChannelErrorException(
                $"Channel reserve ({payload.ChannelReserveAmount}) is below our dust limit ({localParams.DustLimitAmount})",
                payload.ChannelId, "channel_reserve_satoshis is below our dust_limit_satoshis");

        if (localParams.ChannelReserveAmount < payload.DustLimitAmount)
            throw new ChannelErrorException(
                $"Our channel reserve ({localParams.ChannelReserveAmount}) is below the dust limit ({payload.DustLimitAmount})",
                payload.ChannelId, "dust_limit_satoshis is above our channel_reserve_satoshis");

        // Perform optional checks for the channel (the reserve and htlc_minimum limits are relative to it, NL-562)
        var channelAmount = tempChannel.LocalBalance + tempChannel.RemoteBalance;
        _channelOpenValidator.PerformOptionalChecks(
            ChannelOpenOptionalValidationParameters.FromAcceptChannel1Payload(payload, channelAmount));
        // NL-552: the same in-flight floor as for a peer's open (the payload's optional parameters leave it out)
        _channelOpenValidator.CheckMaxHtlcValueInFlight(channelAmount, payload.MaxHtlcValueInFlightAmount);

        // Perform mandatory checks for the channel
        _channelOpenValidator.PerformMandatoryChecks(ChannelOpenMandatoryValidationParameters.FromAcceptChannel1Payload(
                                                         message.ChannelTypeTlv,
                                                         tempChannel.ChannelParams.FeeRateAmountPerKw,
                                                         negotiatedFeatures, payload), out var minimumDepth);

        if (minimumDepth != tempChannel.ChannelParams.MinimumDepth)
            throw new ChannelErrorException("Minimum depth is not acceptable", payload.ChannelId);

        // Simple taproot channels (bolt-simple-taproot.md §accept_channel, NL-877 T5): the accepter's next_local_nonce
        // (its verification nonce for its commitment 0) is required and must parse as two points: our funding_created
        // partial signature is made against it
        if (tempChannel.ChannelParams.OptionSimpleTaproot)
            tempChannel.RemoteOpeningNonce = GetAcceptNonce(message);

        // Check for the upfront shutdown script: it's only required when option_upfront_shutdown_script was negotiated
        if (message.UpfrontShutdownScriptTlv is null && negotiatedFeatures.UpfrontShutdownScript > FeatureSupport.No)
            throw new ChannelErrorException("Upfront shutdown script is required but not provided");

        BitcoinScript? remoteUpfrontShutdownScript = null;
        if (message.UpfrontShutdownScriptTlv is not null && message.UpfrontShutdownScriptTlv.Value.Length > 0)
        {
            // BOLT 2: only a shutdown form the negotiated features allow (NL-776)
            if (!ShutdownScriptValidator.IsValidUpfront(message.UpfrontShutdownScriptTlv.Value, negotiatedFeatures))
                throw new ChannelErrorException("upfront_shutdown_script is not a valid shutdown script",
                                                payload.ChannelId, "upfront_shutdown_script is not a valid form");

            remoteUpfrontShutdownScript = message.UpfrontShutdownScriptTlv.Value;
        }

        // Create the remote key set from the message
        var remoteKeySet = ChannelKeySetModel.CreateForRemote(message.Payload.FundingPubKey,
                                                              message.Payload.RevocationBasepoint,
                                                              message.Payload.PaymentBasepoint,
                                                              message.Payload.DelayedPaymentBasepoint,
                                                              message.Payload.HtlcBasepoint,
                                                              message.Payload.FirstPerCommitmentPoint);

        tempChannel.AddRemoteKeySet(remoteKeySet);

        // Keep the values the peer announced: they bind our HTLCs and our commitment's to_local delay (NL-194)
        tempChannel.UpdateRemoteParams(new ChannelParty(payload.DustLimitAmount, payload.ChannelReserveAmount,
                                                        payload.HtlcMinimumAmount, payload.MaxAcceptedHtlcs,
                                                        payload.MaxHtlcValueInFlightAmount, payload.ToSelfDelay,
                                                        remoteUpfrontShutdownScript));

        // Generate the correct commitment number (we are the opener: opener basepoint first)
        var commitmentNumber = new CommitmentNumber(tempChannel.LocalKeySet.PaymentCompactBasepoint,
                                                    remoteKeySet.PaymentCompactBasepoint, _sha256);

        tempChannel.AddCommitmentNumber(commitmentNumber);

        // Keep the oldChannelId for later
        var oldChannelId = tempChannel.ChannelId;

        var registeredWithSigner = false;
        var capturedNativeOpening = false;
        try
        {
            var fundingAmount = tempChannel.LocalBalance + tempChannel.RemoteBalance;
            var fundingOutput = new FundingOutputInfo(fundingAmount, tempChannel.LocalKeySet.FundingCompactPubKey,
                                                      remoteKeySet.FundingCompactPubKey);

            tempChannel.AddFundingOutput(fundingOutput);

            // Get the utxos to create the funding transaction
            var utxos = _utxoMemoryRepository.GetLockedUtxosForChannel(tempChannel.ChannelId);

            // Size the funding transaction with a P2WPKH change output first, and reserve a change address only when it
            // has one: every address the wallet hands out stays reserved (NL-280), so a funding without change must
            // not use one up. The factory reads only the address type, so the reserved address replaces the probe
            var fundingTransactionModel = _fundingTransactionModelFactory.Create(tempChannel, utxos,
                                                                                 s_changeAddressProbe);
            if (fundingTransactionModel.ChangeAddress is not null)
                fundingTransactionModel.ChangeAddress =
                    await _bitcoinWalletService.GetUnusedAddressAsync(AddressType.P2Wpkh, true);

            // Create the funding transaction
            var fundingTransaction = _fundingTransactionBuilder.Build(fundingTransactionModel);
            fundingOutput.TransactionId = fundingTransaction.Transaction.TxId;
            fundingOutput.Index = fundingTransaction.FundingOutputIndex;

            // If a change was needed, save the change data to the channel
            if (fundingTransactionModel.ChangeAddress is not null)
                tempChannel.ChangeAddress = fundingTransactionModel.ChangeAddress;

            // Create a new channelId
            tempChannel.UpdateChannelId(
                _channelIdFactory.CreateV1(fundingOutput.TransactionId.Value, fundingOutput.Index.Value));

            // Check if the channel already exists in the database (it never should)
            var existingChannel = await _unitOfWork.ChannelDbRepository.GetByIdAsync(tempChannel.ChannelId);
            if (existingChannel is not null)
                throw new ChannelErrorException("Channel already exists in the database", tempChannel.ChannelId,
                                                "Sorry, we had an internal error");

            if (_nativeFundedOutbound is { Enabled: true })
            {
                // Set before the save: its outcome can be uncertain and must never release these inputs.
                capturedNativeOpening = true;
                var created = await _nativeFundedOutbound.StartAsync(tempChannel, oldChannelId, message,
                    negotiatedFeatures, fundingTransaction.Transaction, utxos, fundingTransactionModel.Fee, _unitOfWork);
                _utxoMemoryRepository.UpgradeChannelIdOnLockedUtxos(oldChannelId, tempChannel.ChannelId);
                _channelMemoryRepository.UpgradeChannel(oldChannelId, tempChannel);
                return [created];
            }

            using var nativeInitial = _nativeOpening is { Enabled: true }
                ? await _nativeOpening.BeginInitialCommitAsync(tempChannel, oldChannelId) : null;
            nativeInitial?.Activate();
            // Register the channel with the signer
            _lightningSigner.RegisterChannel(tempChannel.ChannelId, tempChannel.GetSigningInfo());
            registeredWithSigner = true;
            using var openingWorkflow = _lightningSigner is IVlsChannelSigner
                ? await (_transitions ?? throw new InvalidOperationException("VLS opening requires transitions."))
                       .BeginOpeningAsync(tempChannel)
                : null;
            openingWorkflow?.Activate();
            if (_lightningSigner is IVlsChannelSigner setupSigner) setupSigner.EnsureChannelSetup(tempChannel);

            // Generate the base commitment transactions
            var remoteCommitmentTransaction =
                _commitmentTransactionModelFactory.CreateCommitmentTransactionModel(
                    tempChannel, CommitmentSide.Remote, tempChannel.RemoteCommitmentNumber);

            // Build the output and the transactions
            var remoteUnsignedCommitmentTransaction = _commitmentTransactionBuilder.Build(remoteCommitmentTransaction);

            // Sign their remote commitment transaction: ECDSA, or (simple taproot) our MuSig2 partial signature with a
            // fresh signing nonce against the accepter's next_local_nonce, the 64-byte signature field all zeros
            FundingCreatedMessage fundingCreatedMessage;
            if (tempChannel.ChannelParams.OptionSimpleTaproot)
            {
                var partialSignature = _lightningSigner.SignRemoteCommitmentPartial(
                    tempChannel.ChannelId, null, remoteUnsignedCommitmentTransaction,
                    tempChannel.RemoteOpeningNonce!.Value);
                tempChannel.UpdateState(ChannelState.V1FundingCreated);
                fundingCreatedMessage =
                    _messageFactory.CreateFundingCreatedMessage(oldChannelId, fundingOutput.TransactionId.Value,
                                                                fundingOutput.Index.Value, partialSignature);
            }
            else
            {
                var ourSignature =
                    _lightningSigner is IVlsChannelSigner vls
                        ? vls.SignCounterpartyCommitment(tempChannel, remoteCommitmentTransaction).Signature
                        : _lightningSigner.SignChannelTransaction(tempChannel.ChannelId,
                                                                 remoteUnsignedCommitmentTransaction);

                // Update the channel with the new signature and the new state
                tempChannel.UpdateLastSentSignature(ourSignature);
                tempChannel.UpdateState(ChannelState.V1FundingCreated);

                // Create the funding created message
                fundingCreatedMessage =
                    _messageFactory.CreateFundingCreatedMessage(oldChannelId, fundingOutput.TransactionId.Value,
                                                                fundingOutput.Index.Value, ourSignature);
            }

            if (openingWorkflow is not null)
            {
                await _unitOfWork.ChannelDbRepository.AddAsync(tempChannel);
                await _transitions!.StageOpeningCompletionAsync(tempChannel, openingWorkflow, _unitOfWork, false);
                await _unitOfWork.SaveChangesAsync();
                openingWorkflow.Dispose();
            }

            if (_nativeOpening is { Enabled: true })
            {
                await _unitOfWork.ChannelDbRepository.AddAsync(tempChannel);
                await nativeInitial!.StageConsumeAsync(_unitOfWork);
                nativeInitial.Dispose();
                await _nativeOpening.StageConsumeAsync(tempChannel, oldChannelId, _unitOfWork);
                await _unitOfWork.SaveChangesAsync();
            }

            // Move the locked utxos to the real channel id first: UpgradeChannel raises OnChannelUpgraded, and the
            // open-channel subscription looks the locks up by the new id as soon as it sees it (NL-263)
            _utxoMemoryRepository.UpgradeChannelIdOnLockedUtxos(oldChannelId, tempChannel.ChannelId);

            // Upgrade the channel in the dictionary
            _channelMemoryRepository.UpgradeChannel(oldChannelId, tempChannel);

            return [fundingCreatedMessage];
        }
        catch (Exception e)
        {
            if (capturedNativeOpening)
            {
                _logger.LogWarning(e, "Retaining native opening {ChannelId} for original-request recovery", tempChannel.ChannelId);
                throw;
            }

            if (_logger.IsEnabled(LogLevel.Information))
                _logger.LogInformation("Forgetting channel {channelId}", tempChannel.ChannelId);

            // The temporary channel is still keyed by its temporary id; if it was already upgraded, remove the real one
            if (!_channelMemoryRepository.TryRemoveTemporaryChannel(peerPubKey, oldChannelId)
             && !_channelMemoryRepository.TryRemoveChannel(tempChannel.ChannelId))
                _logger.LogWarning("Unable to remove channel with id {channelId} for peer {peerPubKey}",
                                   tempChannel.ChannelId, peerPubKey);

            // Release the utxos we locked for this channel
            _utxoMemoryRepository.ReturnUtxosNotSpentOnChannel(oldChannelId);
            if (tempChannel.ChannelId != oldChannelId)
                _utxoMemoryRepository.ReturnUtxosNotSpentOnChannel(tempChannel.ChannelId);

            // NL-221: a channel we forget here must not stay registered with the signer, or a retry of the open under
            // the same funding txid would hit the stale registration (and the state would leak)
            if (registeredWithSigner)
                _lightningSigner.UnregisterChannel(tempChannel.ChannelId);

            throw new ChannelErrorException("Error creating commitment transaction", e);
        }
    }

    /// <summary>The accepter's simple taproot <c>next_local_nonce</c>, checked (MUST reject the channel otherwise).</summary>
    private MusigPublicNonce GetAcceptNonce(AcceptChannel1Message message)
    {
        if (message.NextLocalNonceTlv is not { } nonceTlv)
            throw new ChannelErrorException("accept_channel of a simple taproot channel without next_local_nonce",
                                            message.Payload.ChannelId, "accept_channel without next_local_nonce");

        if (_musig2 is not null && !TaprootChannelNonces.IsValidPublicNonce(_musig2, nonceTlv.Nonce))
            throw new ChannelErrorException("accept_channel next_local_nonce is not two points",
                                            message.Payload.ChannelId, "next_local_nonce does not parse");

        return nonceTlv.Nonce;
    }
}