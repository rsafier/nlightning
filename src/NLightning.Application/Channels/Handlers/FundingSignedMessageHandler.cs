using Microsoft.Extensions.Logging;

namespace NLightning.Application.Channels.Handlers;

using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.Transactions.Enums;
using Domain.Bitcoin.Transactions.Interfaces;
using Domain.Bitcoin.Transactions.Models;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Crypto.ValueObjects;
using Domain.Exceptions;
using Domain.Node.Options;
using Domain.Onchain.Enums;
using Domain.Onchain.Models;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Infrastructure.Bitcoin.Builders.Interfaces;
using Infrastructure.Bitcoin.Wallet.Interfaces;
using Interfaces;

public class FundingSignedMessageHandler : IChannelMessageHandler<FundingSignedMessage>
{
    private readonly IBlockchainMonitor _blockchainMonitor;
    private readonly IChannelMemoryRepository _channelMemoryRepository;
    private readonly ICommitmentTransactionBuilder _commitmentTransactionBuilder;
    private readonly ICommitmentTransactionModelFactory _commitmentTransactionModelFactory;
    private readonly IFundingTransactionBuilder _fundingTransactionBuilder;
    private readonly IFundingTransactionModelFactory _fundingTransactionModelFactory;
    private readonly ILightningSigner _lightningSigner;
    private readonly ILogger<FundingSignedMessageHandler> _logger;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IUtxoMemoryRepository _utxoMemoryRepository;

    public FundingSignedMessageHandler(IBlockchainMonitor blockchainMonitor,
                                       IChannelMemoryRepository channelMemoryRepository,
                                       ICommitmentTransactionBuilder commitmentTransactionBuilder,
                                       ICommitmentTransactionModelFactory commitmentTransactionModelFactory,
                                       IFundingTransactionBuilder fundingTransactionBuilder,
                                       IFundingTransactionModelFactory fundingTransactionModelFactory,
                                       ILightningSigner lightningSigner, ILogger<FundingSignedMessageHandler> logger,
                                       IUnitOfWork unitOfWork, IUtxoMemoryRepository utxoMemoryRepository)
    {
        _blockchainMonitor = blockchainMonitor;
        _channelMemoryRepository = channelMemoryRepository;
        _commitmentTransactionBuilder = commitmentTransactionBuilder;
        _commitmentTransactionModelFactory = commitmentTransactionModelFactory;
        _fundingTransactionBuilder = fundingTransactionBuilder;
        _fundingTransactionModelFactory = fundingTransactionModelFactory;
        _lightningSigner = lightningSigner;
        _logger = logger;
        _unitOfWork = unitOfWork;
        _utxoMemoryRepository = utxoMemoryRepository;
    }

    public async Task<IReadOnlyList<IChannelMessage>> HandleAsync(
        FundingSignedMessage message, ChannelState currentState, FeatureOptions negotiatedFeatures,
        CompactPubKey peerPubKey)
    {
        if (_logger.IsEnabled(LogLevel.Trace))
            _logger.LogTrace("Processing FundingCreatedMessage with ChannelId: {ChannelId} from Peer: {PeerPubKey}",
                             message.Payload.ChannelId, peerPubKey);

        var payload = message.Payload;

        if (currentState != ChannelState.V1FundingCreated)
            throw new ChannelErrorException(
                $"Received funding signed, but the channel {payload.ChannelId} had the wrong state: {Enum.GetName(currentState)}");

        // Check if there's a temporary channel for this peer
        if (!_channelMemoryRepository.TryGetChannel(payload.ChannelId, out var channel))
            throw new ChannelErrorException("This channel has never been negotiated", payload.ChannelId);

        // Generate the base commitment transactions
        var localCommitmentTransaction =
            _commitmentTransactionModelFactory.CreateCommitmentTransactionModel(channel, CommitmentSide.Local,
                                                                                channel.LocalCommitmentNumber);

        // Build the output and the transactions
        var localUnsignedCommitmentTransaction = _commitmentTransactionBuilder.Build(localCommitmentTransaction);

        // Validate remote signature for our local commitment transaction
        _lightningSigner.ValidateSignature(channel.ChannelId, payload.Signature, localUnsignedCommitmentTransaction);

        // Update the channel with the new signature
        channel.UpdateLastReceivedSignature(payload.Signature);

        // Get the locked utxos to create the funding transaction
        var utxos = _utxoMemoryRepository.GetLockedUtxosForChannel(channel.ChannelId);

        // Get a change address in case we need one
        var fundingTransactionModel = _fundingTransactionModelFactory.Create(channel, utxos, channel.ChangeAddress);
        var fundingTransaction = _fundingTransactionBuilder.Build(fundingTransactionModel);
        var unsignedFundingTransaction = fundingTransaction.Transaction;

        // The rebuilt funding transaction must be the one the peer signed a commitment for
        if (channel.FundingOutput?.TransactionId != unsignedFundingTransaction.TxId
         || channel.FundingOutput?.Index != fundingTransaction.FundingOutputIndex)
            throw new ChannelErrorException("Rebuilt funding transaction does not match the channel funding outpoint",
                                            channel.ChannelId, "Sorry, we had an internal error");

        // Sign the transaction
        var allSigned = _lightningSigner.SignFundingTransaction(channel.ChannelId, unsignedFundingTransaction);
        if (!allSigned)
            throw new ChannelErrorException("Unable to sign all inputs for the funding transaction");

        // One save (BOLT 5 plan O0-T1/T2, NL-258): the channel as V1FundingSigned, the funding watch, the signed funding
        // transaction (sent again after every block until a block holds it, so a crash or a refused send before or
        // during the publish loses nothing) and the watch of the funding output (any spend of it closes the channel)
        channel.UpdateState(ChannelState.V1FundingSigned);
        var fundingWatch = new WatchedTransactionModel(channel.ChannelId, unsignedFundingTransaction.TxId,
                                                       channel.ChannelParams.MinimumDepth);
        var fundingBroadcast = new BroadcastTransactionModel(unsignedFundingTransaction, BroadcastPurpose.Funding,
                                                             channel.ChannelId,
                                                             _blockchainMonitor.LastProcessedBlockHeight);
        var fundingOutputWatch = new WatchedOutpointModel(unsignedFundingTransaction.TxId,
                                                          fundingTransaction.FundingOutputIndex, channel.ChannelId,
                                                          WatchedOutpointPurpose.FundingOutput);
        await PersistChannelAsync(channel, uow =>
        {
            uow.WatchedTransactionDbRepository.Add(fundingWatch);
            uow.BroadcastTransactionDbRepository.Add(fundingBroadcast);
            uow.WatchedOutpointDbRepository.Add(fundingOutputWatch);
        });

        _blockchainMonitor.TrackWatchedTransaction(fundingWatch);
        _blockchainMonitor.TrackWatchedOutpoint(fundingOutputWatch);

        // A refused publish is logged by the broadcaster and retried after the next block
        if (!await _blockchainMonitor.PublishAsync(fundingBroadcast))
            _logger.LogWarning("The funding transaction {TxId} of channel {ChannelId} was not accepted yet; it is sent "
                             + "again after every block", unsignedFundingTransaction.TxId, channel.ChannelId);

        // Announce V1FundingSigned (the open subscription) only once the funding transaction was sent
        _channelMemoryRepository.UpdateChannel(channel);

        return [];
    }

    /// <summary>
    /// Persists a channel, with the rows <paramref name="stageWithChannel"/> stages, in one save of the scoped unit of
    /// work
    /// </summary>
    private async Task PersistChannelAsync(ChannelModel channel, Action<IUnitOfWork> stageWithChannel)
    {
        try
        {
            // Check if we are adding or if we need to update the channel
            var existingChannel = await _unitOfWork.ChannelDbRepository.GetByIdAsync(channel.ChannelId);
            if (existingChannel is not null)
                await _unitOfWork.ChannelDbRepository.UpdateAsync(channel);
            else
                await _unitOfWork.ChannelDbRepository.AddAsync(channel);

            stageWithChannel(_unitOfWork);
            await _unitOfWork.SaveChangesAsync();

            if (_logger.IsEnabled(LogLevel.Debug))
                _logger.LogDebug("Successfully persisted channel {ChannelId} to database", channel.ChannelId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist channel {ChannelId} to database", channel.ChannelId);
            throw;
        }
    }
}