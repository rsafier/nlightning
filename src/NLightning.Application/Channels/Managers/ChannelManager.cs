using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace NLightning.Application.Channels.Managers;

using Domain.Bitcoin.Events;
using Domain.Bitcoin.Interfaces;
using Domain.Channels.Constants;
using Domain.Channels.Enums;
using Domain.Channels.Events;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Exceptions;
using Domain.Node.Options;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Constants;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Handlers;
using Handlers.Interfaces;
using Infrastructure.Bitcoin.Wallet.Interfaces;

public class ChannelManager : IChannelManager
{
    private readonly IChannelMemoryRepository _channelMemoryRepository;
    private readonly ILogger<ChannelManager> _logger;
    private readonly ILightningSigner _lightningSigner;
    private readonly IServiceProvider _serviceProvider;

    public event EventHandler<ChannelResponseMessageEventArgs>? OnResponseMessageReady;

    public ChannelManager(IBlockchainMonitor blockchainMonitor, IChannelMemoryRepository channelMemoryRepository,
                          ILogger<ChannelManager> logger, ILightningSigner lightningSigner,
                          IServiceProvider serviceProvider)
    {
        _channelMemoryRepository = channelMemoryRepository;
        _serviceProvider = serviceProvider;
        _logger = logger;
        _lightningSigner = lightningSigner;

        blockchainMonitor.OnNewBlockDetected += HandleNewBlockDetected;
        blockchainMonitor.OnTransactionConfirmed += HandleFundingConfirmationAsync;
    }

    public Task RegisterExistingChannelAsync(ChannelModel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);

        // Add the channel to the memory repository
        _channelMemoryRepository.AddChannel(channel);

        // Register the channel with the signer
        _lightningSigner.RegisterChannel(channel.ChannelId, channel.GetSigningInfo());

        _logger.LogInformation("Loaded channel {channelId} from database", channel.ChannelId);

        // If the channel is open and ready
        if (channel.State == ChannelState.Open)
        {
            // TODO: Check if the channel has already been reestablished or if we need to reestablish it
        }
        else if (channel.State is ChannelState.ReadyForThem or ChannelState.ReadyForUs)
        {
            _logger.LogInformation("Waiting for channel {ChannelId} to be ready", channel.ChannelId);
        }
        else
        {
            // TODO: Deal with channels that are Closing, Stale, or any other state
            _logger.LogWarning("We don't know how to deal with {channelState} for channel {ChannelId}",
                               Enum.GetName(channel.State), channel.ChannelId);
        }

        return Task.CompletedTask;
    }

    public async Task<IChannelMessage?> HandleChannelMessageAsync(IChannelMessage message,
                                                                  FeatureOptions negotiatedFeatures,
                                                                  CompactPubKey peerPubKey)
    {
        using var scope = _serviceProvider.CreateScope();

        // Check if the channel exists on the state dictionary
        _channelMemoryRepository.TryGetChannelState(message.Payload.ChannelId, out var currentState);

        // In this case we can only handle messages that are opening a channel
        switch (message.Type)
        {
            case MessageTypes.OpenChannel:
                // Handle opening channel message
                var openChannel1Message = message as OpenChannel1Message
                                       ?? throw new ChannelErrorException("Error boxing message to OpenChannel1Message",
                                                                          "Sorry, we had an internal error");
                return await GetChannelMessageHandler<OpenChannel1Message>(scope)
                          .HandleAsync(openChannel1Message, currentState, negotiatedFeatures, peerPubKey);

            case MessageTypes.AcceptChannel:
                // Handle the accept channel message
                var acceptChannel1Message = message as AcceptChannel1Message
                                         ?? throw new ChannelErrorException(
                                                "Error boxing message to AcceptChannel1Message",
                                                "Sorry, we had an internal error");
                return await GetChannelMessageHandler<AcceptChannel1Message>(scope)
                          .HandleAsync(acceptChannel1Message, currentState, negotiatedFeatures, peerPubKey);

            case MessageTypes.FundingCreated:
                // Handle the funding-created message
                var fundingCreatedMessage = message as FundingCreatedMessage
                                         ?? throw new ChannelErrorException(
                                                "Error boxing message to FundingCreatedMessage",
                                                "Sorry, we had an internal error");
                return await GetChannelMessageHandler<FundingCreatedMessage>(scope)
                          .HandleAsync(fundingCreatedMessage, currentState, negotiatedFeatures, peerPubKey);

            case MessageTypes.ChannelReady:
                // Handle channel ready message
                var channelReadyMessage = message as ChannelReadyMessage
                                       ?? throw new ChannelErrorException("Error boxing message to ChannelReadyMessage",
                                                                          "Sorry, we had an internal error");
                return await GetChannelMessageHandler<ChannelReadyMessage>(scope)
                          .HandleAsync(channelReadyMessage, currentState, negotiatedFeatures, peerPubKey);

            case MessageTypes.FundingSigned:
                // Handle funding signed message
                var fundingSignedMessage = message as FundingSignedMessage
                                        ?? throw new ChannelErrorException(
                                               "Error boxing message to FundingSignedMessage",
                                               "Sorry, we had an internal error");
                return await GetChannelMessageHandler<FundingSignedMessage>(scope)
                          .HandleAsync(fundingSignedMessage, currentState, negotiatedFeatures, peerPubKey);

            default:
                throw new ChannelErrorException("Unknown message type", "Sorry, we had an internal error");
        }
    }

    private IChannelMessageHandler<T> GetChannelMessageHandler<T>(IServiceScope scope)
        where T : IChannelMessage
    {
        var handler = scope.ServiceProvider.GetRequiredService<IChannelMessageHandler<T>>() ??
                      throw new ChannelErrorException($"No handler found for message type {typeof(T).FullName}",
                                                      "Sorry, we had an internal error");
        return handler;
    }

    /// <summary>
    /// Persists a channel to the database using a scoped Unit of Work
    /// </summary>
    private async Task PersistChannelAsync(ChannelModel channel)
    {
        using var scope = _serviceProvider.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        try
        {
            // Check if the channel already exists

            _ = await unitOfWork.ChannelDbRepository.GetByIdAsync(channel.ChannelId)
             ?? throw new ChannelWarningException("Channel not found", channel.ChannelId);
            await unitOfWork.ChannelDbRepository.UpdateAsync(channel);
            await unitOfWork.SaveChangesAsync();

            // Remove from dictionaries
            _channelMemoryRepository.TryRemoveChannel(channel.ChannelId);

            _logger.LogDebug("Successfully persisted channel {ChannelId} to database", channel.ChannelId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist channel {ChannelId} to database", channel.ChannelId);
            throw;
        }
    }

    private void HandleNewBlockDetected(object? sender, NewBlockEventArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var currentHeight = (int)args.Height;

        // Deal with stale channels
        ForgetStaleChannels(currentHeight);

        // Deal with channels that are waiting for funding confirmation on start-up
        ConfirmUnconfirmedChannels(currentHeight);
    }

    private void ForgetStaleChannels(int currentHeight)
    {
        // Channels saved before FundingCreatedMessageHandler recorded the creation height have it as 0; start their
        // timeout now so they can still be forgotten.
        BackfillMissingFundingCreatedHeights(currentHeight);

        var heightLimit = currentHeight - ChannelConstants.MaxUnconfirmedChannelAge;
        if (heightLimit < 0)
        {
            _logger.LogDebug("Block height {BlockHeight} is too low to forget channels", currentHeight);
            return;
        }

        // BOLT 2: only the fundee SHOULD forget a channel whose funding transaction was not seen after 2016 blocks.
        // Only consider channels still awaiting funding confirmation, with a real (non-zero) creation height.
        var staleChannels = _channelMemoryRepository.FindChannels(c => IsAwaitingFundingAndStale(c, heightLimit));

        _logger.LogDebug(
            "Forgetting stale channels created before block height {HeightLimit}, found {StaleChannelCount} channels",
            heightLimit, staleChannels.Count);

        foreach (var staleChannel in staleChannels)
        {
            _logger.LogInformation(
                "Forgetting stale channel {ChannelId} with funding created at block height {BlockHeight}",
                staleChannel.ChannelId, staleChannel.FundingCreatedAtBlockHeight);

            // Set states
            staleChannel.UpdateState(ChannelState.Stale);
            _channelMemoryRepository.UpdateChannel(staleChannel);

            // Persist on Db
            try
            {
                PersistChannelAsync(staleChannel).ContinueWith(task =>
                {
                    _logger.LogError(task.Exception, "Error while marking channel {channelId} as stale.",
                                     staleChannel.ChannelId);
                }, TaskContinuationOptions.OnlyOnFaulted);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Failed to persist stale channel {ChannelId} to database at height {currentHeight}",
                                 staleChannel.ChannelId, currentHeight);
            }
        }
    }

    private void BackfillMissingFundingCreatedHeights(int currentHeight)
    {
        if (currentHeight <= 0)
            return;

        var channelsWithoutHeight = _channelMemoryRepository.FindChannels(IsAwaitingFundingWithoutCreationHeight);
        if (channelsWithoutHeight.Count == 0)
            return;

        using var scope = _serviceProvider.CreateScope();
        using var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        foreach (var channel in channelsWithoutHeight)
        {
            _logger.LogInformation(
                "Channel {ChannelId} has no funding creation height, starting its unconfirmed timeout at {BlockHeight}",
                channel.ChannelId, currentHeight);

            channel.FundingCreatedAtBlockHeight = (uint)currentHeight;
            _channelMemoryRepository.UpdateChannel(channel);

            try
            {
                uow.ChannelDbRepository.UpdateAsync(channel).GetAwaiter().GetResult();
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Failed to persist funding creation height for channel {ChannelId}",
                                 channel.ChannelId);
            }
        }

        try
        {
            uow.SaveChangesAsync().GetAwaiter().GetResult();
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Failed to persist funding creation heights at block height {BlockHeight}",
                             currentHeight);
        }
    }

    private static bool IsAwaitingFundingWithoutCreationHeight(ChannelModel channel)
    {
        return !channel.IsInitiator
            && channel.State is ChannelState.V1FundingSigned or ChannelState.ReadyForThem
            && channel.FundingCreatedAtBlockHeight == 0;
    }

    private static bool IsAwaitingFundingAndStale(ChannelModel channel, int heightLimit)
    {
        return !channel.IsInitiator
            && channel.State is ChannelState.V1FundingSigned or ChannelState.ReadyForThem
            && channel.FundingCreatedAtBlockHeight > 0
            && channel.FundingCreatedAtBlockHeight <= heightLimit;
    }

    private void ConfirmUnconfirmedChannels(int currentHeight)
    {
        // Only channels still waiting for our own funding confirmation. ReadyForUs channels were already confirmed
        // for us (and sent channel_ready), so re-running the confirmation for them would bump the commitment number
        // and re-send channel_ready on every block.
        var unconfirmedChannels = _channelMemoryRepository.FindChannels(IsAwaitingOurFundingConfirmation);
        if (unconfirmedChannels.Count == 0)
            return;

        using var scope = _serviceProvider.CreateScope();
        using var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        foreach (var unconfirmedChannel in unconfirmedChannels)
        {
            if (unconfirmedChannel.FundingOutput?.TransactionId is null)
            {
                _logger.LogError("Channel {ChannelId} has no funding transaction Id, cannot confirm",
                                 unconfirmedChannel.ChannelId);
                continue;
            }

            var watchedTransaction =
                uow.WatchedTransactionDbRepository.GetByTransactionIdAsync(
                    unconfirmedChannel.FundingOutput.TransactionId.Value).GetAwaiter().GetResult();
            if (watchedTransaction is null)
            {
                _logger.LogError("Watched transaction for channel {ChannelId} not found",
                                 unconfirmedChannel.ChannelId);
                continue;
            }

            // Only a watched transaction that already reached its required depth (e.g. while we were offline) is
            // confirmed here; pending ones are confirmed by the blockchain monitor when they reach the depth.
            if (!watchedTransaction.IsCompleted)
                continue;

            // Create a TransactionConfirmedEventArgs and call the event handler
            var args = new TransactionConfirmedEventArgs(watchedTransaction, (uint)currentHeight);
            HandleFundingConfirmationAsync(this, args);
        }
    }

    private static bool IsAwaitingOurFundingConfirmation(ChannelModel channel)
    {
        return channel.State is ChannelState.V1FundingSigned or ChannelState.ReadyForThem;
    }

    private void HandleFundingConfirmationAsync(object? sender, TransactionConfirmedEventArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.WatchedTransaction.FirstSeenAtHeight is null)
        {
            _logger.LogError(
                "Received null {nameof_FirstSeenAtHeight} in {nameof_TransactionConfirmedEventArgs} for channel {ChannelId}",
                nameof(args.WatchedTransaction.FirstSeenAtHeight), nameof(TransactionConfirmedEventArgs),
                args.WatchedTransaction.ChannelId);
            return;
        }

        if (args.WatchedTransaction.TransactionIndex is null)
        {
            _logger.LogError(
                "Received null {nameof_FirstSeenAtHeight} in {nameof_TransactionConfirmedEventArgs} for channel {ChannelId}",
                nameof(args.WatchedTransaction.FirstSeenAtHeight), nameof(TransactionConfirmedEventArgs),
                args.WatchedTransaction.ChannelId);
            return;
        }

        // Create a scope to handle the funding confirmation
        var scope = _serviceProvider.CreateScope();

        var channelId = args.WatchedTransaction.ChannelId;
        // Check if the transaction is a funding transaction for any channel
        if (!_channelMemoryRepository.TryGetChannel(channelId, out var channel))
        {
            // Channel isn't found in memory, check the database
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            channel = uow.ChannelDbRepository.GetByIdAsync(channelId).GetAwaiter().GetResult();
            if (channel is null)
            {
                _logger.LogError("Funding confirmation for unknown channel {ChannelId}", channelId);
                return;
            }

            _lightningSigner.RegisterChannel(channelId, channel.GetSigningInfo());
            _channelMemoryRepository.AddChannel(channel);
        }

        // Funding confirmation is only processed once per channel
        if (!IsAwaitingOurFundingConfirmation(channel))
        {
            _logger.LogDebug("Ignoring funding confirmation for channel {ChannelId} in state {State}", channelId,
                             Enum.GetName(channel.State));
            scope.Dispose();
            return;
        }

        var fundingConfirmedHandler = scope.ServiceProvider.GetRequiredService<FundingConfirmedMessageHandler>();

        // If we get a response, raise the event with the message
        fundingConfirmedHandler.OnMessageReady += (_, message) =>
            OnResponseMessageReady?.Invoke(this, new ChannelResponseMessageEventArgs(channel.RemoteNodeId, message));

        // Add confirmation information to the channel
        channel.FundingCreatedAtBlockHeight = args.WatchedTransaction.FirstSeenAtHeight.Value;
        channel.ShortChannelId = new ShortChannelId(args.WatchedTransaction.FirstSeenAtHeight.Value,
                                                    args.WatchedTransaction.TransactionIndex.Value,
                                                    channel.FundingOutput.Index!.Value);

        fundingConfirmedHandler.HandleAsync(channel).ContinueWith(task =>
        {
            if (task.IsFaulted)
                _logger.LogError(task.Exception, "Error while handling funding confirmation for channel {channelId}",
                                 channel.ChannelId);

            scope.Dispose();
        });
    }
}