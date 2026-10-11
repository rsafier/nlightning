using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace NLightning.Application.Channels.Services;

using Accounting;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.Transactions.Enums;
using Domain.Bitcoin.Transactions.Interfaces;
using Domain.Bitcoin.Transactions.Models;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Node.Options;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Domain.Serialization.Interfaces;
using Domain.Signing.Recovery;
using Infrastructure.Bitcoin.Builders.Interfaces;
using Infrastructure.Bitcoin.Wallet.Interfaces;

/// <summary>Retains the fundee's funded negotiation before signing and replays only its original receipt.</summary>
public sealed class NativeV1FundedInboundOpening(
    NativeV1ChannelOpening allocation, IRemoteSigningWorkflowCoordinator workflows,
    ICommitmentTransactionBuilder builder, ICommitmentTransactionModelFactory models,
    ILightningSigner signer, IMessageFactory messages, IMessageSerializer serializer,
    IChannelIdFactory ids, IChannelMemoryRepository memory, IBlockchainMonitor monitor,
    ILogger<NativeV1FundedInboundOpening> logger)
{
    public bool Enabled => workflows is INativeOpeningSigningRecovery && allocation.Enabled;

    public async Task<FundingSignedMessage> StartAsync(ChannelModel channel, ChannelId temporaryId,
        FundingCreatedMessage message, FeatureOptions features, IUnitOfWork unit)
    {
        if (!Enabled) throw new InvalidOperationException("Native funded opening recovery is unavailable.");
        var intent = new FundedIntent("inbound-funded", temporaryId.ToString(), channel.LocalKeySet.KeyIndex,
            Convert.ToHexString(SigningWorkflowSnapshot.CreateOpening(channel, SigningWorkflowKind.Opening).SnapshotFingerprint),
            await EncodeAsync(message), JsonSerializer.SerializeToUtf8Bytes(features), monitor.LastProcessedBlockHeight,
            channel.RemoteOpeningNonce is { } peerNonce ? ((byte[])peerNonce).ToArray() : null);
        var encoded = JsonSerializer.SerializeToUtf8Bytes(intent);
        // The full negotiated channel and the immutable input are committed before RegisterChannel or signing.
        await unit.ChannelDbRepository.AddAsync(channel);
        await workflows.StageAsync(Descriptor(channel.ChannelId, encoded), unit);
        await unit.SaveChangesAsync();
        return await CompleteAsync(channel, intent, encoded, unit);
    }

    public async Task<FundingSignedMessage?> TryHandleRetainedAsync(FundingCreatedMessage message,
        CompactPubKey peer, IUnitOfWork unit)
    {
        if (!Enabled) return null;
        var realId = ids.CreateV1(message.Payload.FundingTxId, message.Payload.FundingOutputIndex);
        var saved = await unit.SigningWorkflowDbRepository.GetLatestForChannelAsync(realId, SigningWorkflowKind.Opening);
        if (saved?.PublicationIntent is null) return null;
        var intent = ReadIntent(saved);
        if (intent is null) return null;
        var channel = await unit.ChannelDbRepository.GetByIdAsync(realId)
            ?? throw new InvalidOperationException("Funded opening lost its persisted channel.");
        var encodedInput = await EncodeAsync(message);
        if (channel.RemoteNodeId != peer || intent.TemporaryId != message.Payload.ChannelId.ToString()
         || !intent.InputMessage.AsSpan().SequenceEqual(encodedInput))
            throw new InvalidOperationException("The peer changed its retained funded opening inputs.");
        FundingSignedMessage reply;
        if (saved.State == SigningWorkflowState.Consumed)
        {
            if (channel.State != ChannelState.V1FundingSigned)
                throw new InvalidOperationException("The channel has finished its retained funding negotiation.");
            ValidateSnapshot(channel, intent);
            var receipt = await ((INativeOpeningSigningRecovery)workflows).ReadOpeningReplyAsync(saved);
            reply = Reply(realId, receipt);
        }
        else
        {
            if (saved.State != SigningWorkflowState.Pending)
                throw new InvalidOperationException("An uncertain funded opening requires operator attention.");
            reply = await CompleteAsync(channel, intent, saved.PublicationIntent, unit);
        }
        if (!memory.TryGetChannel(realId, out _)) memory.LoadChannel(channel);
        memory.TryRemoveTemporaryChannel(peer, message.Payload.ChannelId);
        return reply;
    }

    public async Task<bool> ResumeAsync(ChannelModel channel, IUnitOfWork unit)
    {
        if (!Enabled) return false;
        var pending = await workflows.GetPendingAsync(channel.ChannelId);
        if (pending.Count != 1 || pending[0].Kind != SigningWorkflowKind.Opening) return false;
        var saved = pending[0];
        var intent = ReadIntent(saved);
        if (intent is null) return false;
        if (saved.State != SigningWorkflowState.Pending)
            throw new InvalidOperationException("An uncertain funded opening requires operator attention.");
        await CompleteAsync(channel, intent, saved.PublicationIntent!, unit);
        return true;
    }

    private async Task<FundingSignedMessage> CompleteAsync(ChannelModel channel, FundedIntent intent,
        byte[] encoded, IUnitOfWork unit)
    {
        if (channel.State != ChannelState.V1Opening)
            throw new InvalidOperationException("Funded opening no longer matches its original negotiated channel.");
        ValidateSnapshot(channel, intent);
        channel.RemoteOpeningNonce = intent.RemoteOpeningNonce is { } nonce ? new MusigPublicNonce(nonce) : null;
        using var input = new MemoryStream(intent.InputMessage, writable: false);
        var message = await serializer.DeserializeMessageAsync<FundingCreatedMessage>(input)
            ?? throw new InvalidOperationException("Funded opening lost its original funding_created.");
        if (message.Payload.ChannelId.ToString() != intent.TemporaryId
         || ids.CreateV1(message.Payload.FundingTxId, message.Payload.FundingOutputIndex) != channel.ChannelId)
            throw new InvalidOperationException("Funded opening message no longer binds its original funding outpoint.");
        FundingSignedMessage reply;
        using (var workflow = await workflows.BeginAsync(Descriptor(channel.ChannelId, encoded)))
        {
            workflow.Activate();
            signer.RegisterChannel(channel.ChannelId, channel.GetSigningInfo());
            var local = builder.Build(models.CreateCommitmentTransactionModel(channel, CommitmentSide.Local, 0));
            var remote = builder.Build(models.CreateCommitmentTransactionModel(channel, CommitmentSide.Remote, 0));
            if (channel.ChannelParams.OptionSimpleTaproot)
            {
                var partial = message.PartialSignatureWithNonceTlv?.PartialSignatureWithNonce
                    ?? throw new InvalidOperationException("Funded taproot opening lost its original holder signature.");
                signer.ValidateLocalCommitmentPartialSignature(channel.ChannelId, null, 0, partial, local);
                var ours = signer.SignRemoteCommitmentPartial(channel.ChannelId, null, remote,
                    channel.RemoteOpeningNonce ?? throw new InvalidOperationException("Funded opening lost the peer's verification nonce."));
                channel.UpdateLastReceivedPartialSignature(partial);
                reply = messages.CreateFundingSignedMessage(channel.ChannelId, ours);
            }
            else
            {
                signer.ValidateSignature(channel.ChannelId, message.Payload.Signature, local);
                var ours = signer.SignChannelTransaction(channel.ChannelId, remote);
                channel.UpdateLastReceivedSignature(message.Payload.Signature);
                channel.UpdateLastSentSignature(ours);
                reply = messages.CreateFundingSignedMessage(channel.ChannelId, ours);
            }
            channel.UpdateState(ChannelState.V1FundingSigned);
            channel.FundingCreatedAtBlockHeight = intent.Height;
            await unit.ChannelDbRepository.UpdateAsync(channel);
            await workflow.StageConsumeAsync(unit);
        }
        await allocation.StageConsumeAsync(channel, new ChannelId(Convert.FromHexString(intent.TemporaryId)), unit);
        await ChannelAccountingEvents.StagePushAmountAsync(unit, channel.ChannelId, channel.LocalBalance, logger);
        var fundingTx = channel.FundingOutput!.TransactionId!.Value;
        var watch = await unit.WatchedTransactionDbRepository.GetByTransactionIdAsync(fundingTx);
        if (watch is null)
        {
            watch = new WatchedTransactionModel(channel.ChannelId, fundingTx, channel.ChannelParams.MinimumDepth);
            unit.WatchedTransactionDbRepository.Add(watch);
        }
        else if (watch.ChannelId != channel.ChannelId || watch.RequiredDepth != channel.ChannelParams.MinimumDepth)
            throw new InvalidOperationException("Funded opening funding watch belongs to another lifecycle.");
        await unit.SaveChangesAsync();
        monitor.TrackWatchedTransaction(watch);
        return reply;
    }

    private FundingSignedMessage Reply(ChannelId channelId, NativeOpeningReply receipt)
        => receipt.PartialSignature is { } partial ? messages.CreateFundingSignedMessage(channelId, partial)
            : messages.CreateFundingSignedMessage(channelId, receipt.Signature
                ?? throw new InvalidOperationException("Funded opening retained no exact signing reply."));

    private async Task<byte[]> EncodeAsync(FundingCreatedMessage message)
    {
        using var stream = new MemoryStream();
        await serializer.SerializeAsync(message, stream);
        return stream.ToArray();
    }

    private static FundedIntent? ReadIntent(SigningWorkflow workflow)
    {
        var encoded = workflow.PublicationIntent ?? throw new InvalidOperationException("Funded opening lost its immutable intent.");
        if (!SHA256.HashData(encoded).AsSpan().SequenceEqual(workflow.SnapshotFingerprint))
            throw new InvalidOperationException("Funded opening intent fingerprint changed.");
        using var json = JsonDocument.Parse(encoded);
        if (!json.RootElement.TryGetProperty("Direction", out var direction) || direction.GetString() != "inbound-funded") return null;
        return JsonSerializer.Deserialize<FundedIntent>(encoded)
            ?? throw new InvalidOperationException("Funded opening intent is invalid.");
    }

    private static SigningWorkflowDescriptor Descriptor(ChannelId id, byte[] encoded)
        => new(id, SigningWorkflowKind.Opening, 0, 0, SHA256.HashData(encoded)) { PublicationIntent = encoded };

    private static void ValidateSnapshot(ChannelModel channel, FundedIntent intent)
    {
        if (channel.IsInitiator || channel.LocalKeySet.KeyIndex != intent.KeyIndex
         || Convert.ToHexString(SigningWorkflowSnapshot.CreateOpening(channel, SigningWorkflowKind.Opening).SnapshotFingerprint) != intent.Snapshot)
            throw new InvalidOperationException("Funded opening no longer matches its original negotiated channel.");
    }

    private sealed record FundedIntent(string Direction, string TemporaryId, uint KeyIndex, string Snapshot,
        byte[] InputMessage, byte[] Features, uint Height, byte[]? RemoteOpeningNonce);
}