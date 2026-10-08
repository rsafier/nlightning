using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Channels.Services;

using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.Transactions.Enums;
using Domain.Bitcoin.Transactions.Interfaces;
using Domain.Bitcoin.ValueObjects;
using Domain.Bitcoin.Wallet.Models;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Node.Options;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Domain.Serialization.Interfaces;
using Domain.Signing.Recovery;
using Infrastructure.Bitcoin.Builders.Interfaces;
using Infrastructure.Bitcoin.Networks;
using Domain.Bitcoin.Wallet.Constants;

/// <summary>Retains the funder's exact negotiated opening and wallet inputs before commitment-zero signing.</summary>
public sealed class NativeV1FundedOutboundOpening(
    NativeV1ChannelOpening allocation, IRemoteSigningWorkflowCoordinator workflows,
    ICommitmentTransactionBuilder builder, ICommitmentTransactionModelFactory models,
    IFundingTransactionBuilder fundingBuilder, IFundingTransactionModelFactory fundingModels,
    ILightningSigner signer, IMessageFactory messages, IMessageSerializer serializer,
    IUtxoMemoryRepository wallet, IOptions<NodeOptions> options, IChannelMemoryRepository memory)
{
    public bool Enabled => allocation.Enabled && workflows is INativeOpeningSigningRecovery;

    public async Task<FundingCreatedMessage> StartAsync(ChannelModel channel, ChannelId temporaryId,
        AcceptChannel1Message input, FeatureOptions features, SignedTransaction unsigned,
        IReadOnlyList<UtxoModel> inputs, LightningMoney fee, IUnitOfWork unit)
    {
        if (!Enabled) throw new InvalidOperationException("Native funded outbound recovery is unavailable.");
        var reservationId = Guid.NewGuid();
        var capturedInputs = inputs.Select(Capture).ToArray();
        var intent = new FundedIntent("outbound-funded", temporaryId.ToString(), channel.LocalKeySet.KeyIndex,
            Snapshot(channel), await EncodeAsync(input), JsonSerializer.SerializeToUtf8Bytes(features),
            channel.RemoteOpeningNonce is { } nonce ? ((byte[])nonce).ToArray() : null,
            reservationId, unsigned.RawTxBytes.ToArray(), fee.MilliSatoshi, capturedInputs);
        var encoded = JsonSerializer.SerializeToUtf8Bytes(intent);
        var network = options.Value.BitcoinNetwork.ToNBitcoinNetwork();
        BitcoinScript? changeScript = channel.ChangeAddress is { } change
            ? new BitcoinScript(NBitcoin.BitcoinAddress.Create(change.Address, network).ScriptPubKey.ToBytes()) : (BitcoinScript?)null;
        var reservation = new FeeInputReservation(reservationId, $"native-funding:{channel.ChannelId}",
            capturedInputs.Select(ToWalletInput).ToArray(), fee,
            LightningMoney.Satoshis(inputs.Sum(x => x.Amount.Satoshi)) - channel.FundingOutput!.Amount - fee,
            changeScript);
        await unit.ChannelDbRepository.AddAsync(channel);
        await workflows.StageAsync(Descriptor(channel.ChannelId, encoded), unit);
        unit.FeeInputReservationDbRepository.Add(reservation, DateTimeOffset.UtcNow);
        // Treat an uncertain save as durable: the caller retains its original locks and signer identity.
        await unit.SaveChangesAsync();
        wallet.LoadFeeReservations(inputs.Select(x => (x.TxId, x.Index, reservationId)));
        return await CompleteAsync(channel, intent, encoded, unit);
    }

    public async Task<FundingCreatedMessage?> TryHandleRetainedAsync(AcceptChannel1Message input,
        CompactPubKey peer, IUnitOfWork unit)
    {
        if (!Enabled) return null;
        var channels = (await unit.ChannelDbRepository.GetByPeerIdAsync(peer)).OfType<ChannelModel>();
        var matches = new List<(ChannelModel Channel, SigningWorkflow Workflow, FundedIntent Intent)>();
        foreach (var channel in channels.Where(x => x.IsInitiator && x.RemoteNodeId == peer))
        {
            var saved = await unit.SigningWorkflowDbRepository.GetLatestForChannelAsync(channel.ChannelId, SigningWorkflowKind.Opening);
            var intent = saved is null ? null : ReadIntent(saved);
            if (intent?.TemporaryId == input.Payload.ChannelId.ToString()) matches.Add((channel, saved!, intent));
        }
        if (matches.Count == 0) return null;
        if (matches.Count != 1) throw new InvalidOperationException("The peer has ambiguous retained outbound openings.");
        var retained = matches[0];
        var inputBytes = await EncodeAsync(input);
        if (!retained.Intent.InputMessage.AsSpan().SequenceEqual(inputBytes))
            throw new InvalidOperationException("The peer changed its retained accept_channel inputs.");
        var reply = await RestoreAsync(retained.Channel, retained.Workflow, retained.Intent, unit);
        if (wallet.GetLockedUtxosForChannel(input.Payload.ChannelId).Count != 0)
            wallet.UpgradeChannelIdOnLockedUtxos(input.Payload.ChannelId, retained.Channel.ChannelId);
        if (!memory.TryGetChannel(retained.Channel.ChannelId, out _)) memory.LoadChannel(retained.Channel);
        memory.TryRemoveTemporaryChannel(peer, input.Payload.ChannelId);
        return reply;
    }

    public async Task<FundingCreatedMessage?> TryGetFundingCreatedAsync(ChannelModel channel, IUnitOfWork unit)
    {
        if (!Enabled || !channel.IsInitiator || channel.State is not (ChannelState.V1Opening or ChannelState.V1FundingCreated)) return null;
        var saved = await unit.SigningWorkflowDbRepository.GetLatestForChannelAsync(channel.ChannelId, SigningWorkflowKind.Opening);
        var intent = saved is null ? null : ReadIntent(saved);
        return intent is null ? null : await RestoreAsync(channel, saved!, intent, unit);
    }

    public async Task<bool> ResumeAsync(ChannelModel channel, IUnitOfWork unit)
        => await TryGetFundingCreatedAsync(channel, unit) is not null;

    /// <summary>Checks the peer acknowledgment will fund precisely the transaction retained before funding_created.</summary>
    public async Task ValidateFundingAsync(ChannelModel channel, SignedTransaction transaction, LightningMoney fee, IUnitOfWork unit)
    {
        var saved = await unit.SigningWorkflowDbRepository.GetLatestForChannelAsync(channel.ChannelId, SigningWorkflowKind.Opening);
        var intent = saved is null ? null : ReadIntent(saved);
        if (intent is null || saved!.State != SigningWorkflowState.Consumed)
            throw new InvalidOperationException("Funding acknowledgment requires its consumed original outbound opening.");
        ValidateSnapshot(channel, intent);
        await RestoreInputsAsync(channel, intent, unit);
        await ((INativeOpeningSigningRecovery)workflows).ReadOpeningReplyAsync(saved!);
        if (!transaction.RawTxBytes.AsSpan().SequenceEqual(intent.UnsignedTransaction) || fee.MilliSatoshi != intent.FeeMsat)
            throw new InvalidOperationException("Funding acknowledgment changed its original transaction or fee.");
    }

    public async Task<SignedTransaction> RestoreUnsignedFundingAsync(ChannelModel channel, IUnitOfWork unit)
    {
        var saved = await unit.SigningWorkflowDbRepository.GetLatestForChannelAsync(channel.ChannelId, SigningWorkflowKind.Opening);
        var intent = saved is null ? null : ReadIntent(saved);
        if (intent is null || saved!.State != SigningWorkflowState.Consumed)
            throw new InvalidOperationException("Funding recovery requires its consumed original outbound opening.");
        ValidateSnapshot(channel, intent);
        await RestoreInputsAsync(channel, intent, unit);
        await ((INativeOpeningSigningRecovery)workflows).ReadOpeningReplyAsync(saved!);
        return new SignedTransaction(channel.FundingOutput!.TransactionId!.Value, intent.UnsignedTransaction.ToArray());
    }

    private async Task<FundingCreatedMessage> RestoreAsync(ChannelModel channel, SigningWorkflow saved, FundedIntent intent, IUnitOfWork unit)
    {
        if (channel.State is not (ChannelState.V1Opening or ChannelState.V1FundingCreated))
            throw new InvalidOperationException("The channel has finished its retained funding negotiation.");
        ValidateSnapshot(channel, intent);
        await RestoreInputsAsync(channel, intent, unit);
        if (saved.State == SigningWorkflowState.Consumed)
        {
            if (channel.State != ChannelState.V1FundingCreated)
                throw new InvalidOperationException("Consumed opening lost its saved funding_created state.");
            return Reply(channel, intent, await ((INativeOpeningSigningRecovery)workflows).ReadOpeningReplyAsync(saved));
        }
        if (saved.State != SigningWorkflowState.Pending)
            throw new InvalidOperationException("An uncertain outbound opening requires operator attention.");
        return await CompleteAsync(channel, intent, saved.PublicationIntent!, unit);
    }

    private async Task<FundingCreatedMessage> CompleteAsync(ChannelModel channel, FundedIntent intent, byte[] encoded, IUnitOfWork unit)
    {
        if (channel.State != ChannelState.V1Opening) throw new InvalidOperationException("Outbound opening state changed before signing.");
        ValidateSnapshot(channel, intent);
        await RestoreInputsAsync(channel, intent, unit);
        channel.RemoteOpeningNonce = intent.RemoteOpeningNonce is { } nonce ? new MusigPublicNonce(nonce) : null;
        FundingCreatedMessage reply;
        using (var workflow = await workflows.BeginAsync(Descriptor(channel.ChannelId, encoded)))
        {
            workflow.Activate();
            signer.RegisterChannel(channel.ChannelId, channel.GetSigningInfo());
            var remote = builder.Build(models.CreateCommitmentTransactionModel(channel, CommitmentSide.Remote, 0));
            NativeOpeningReply receipt;
            if (channel.ChannelParams.OptionSimpleTaproot)
                receipt = new NativeOpeningReply(null, signer.SignRemoteCommitmentPartial(channel.ChannelId, null, remote,
                    channel.RemoteOpeningNonce ?? throw new InvalidOperationException("Outbound opening lost peer nonce.")));
            else
            {
                var signature = signer.SignChannelTransaction(channel.ChannelId, remote);
                channel.UpdateLastSentSignature(signature);
                receipt = new NativeOpeningReply(signature, null);
            }
            reply = Reply(channel, intent, receipt);
            channel.UpdateState(ChannelState.V1FundingCreated);
            await unit.ChannelDbRepository.UpdateAsync(channel);
            await workflow.StageConsumeAsync(unit);
        }
        await allocation.StageConsumeAsync(channel, new ChannelId(Convert.FromHexString(intent.TemporaryId)), unit);
        await unit.SaveChangesAsync();
        return reply;
    }

    private async Task RestoreInputsAsync(ChannelModel channel, FundedIntent intent, IUnitOfWork unit)
    {
        var reservation = await unit.FeeInputReservationDbRepository.GetByIdAsync(intent.ReservationId)
            ?? throw new InvalidOperationException("Outbound opening lost its original input reservation.");
        if (reservation.Purpose != $"native-funding:{channel.ChannelId}" || reservation.Inputs.Count != intent.Inputs.Length
            || reservation.Fee.MilliSatoshi != intent.FeeMsat)
            throw new InvalidOperationException("Outbound opening reservation changed.");
        var selected = new List<UtxoModel>();
        foreach (var input in intent.Inputs)
        {
            var txId = new TxId(Convert.FromHexString(input.TxId));
            var reserved = reservation.Inputs.SingleOrDefault(x => x.TxId == txId && x.Index == input.Index);
            if (reserved is null || reserved.Amount.MilliSatoshi != input.AmountMsat
                || !((byte[])reserved.ScriptPubKey).AsSpan().SequenceEqual(input.Script)
                || !wallet.TryGetUtxo(txId, input.Index, out var utxo)
                || utxo.Amount.MilliSatoshi != input.AmountMsat
                || !Capture(utxo).Script.AsSpan().SequenceEqual(input.Script)
                || utxo.LockedToChannelId is { } locked && locked != channel.ChannelId && locked.ToString() != intent.TemporaryId
                || wallet.TryGetFeeReservation(txId, input.Index, out var held) && held != intent.ReservationId)
                throw new InvalidOperationException("Outbound opening input is missing, changed or held by another operation.");
            var current = Capture(utxo);
            if ((int)reserved.AddressType != input.AddressType || reserved.IsSilentPayment != input.Silent
             || reserved.SilentPaymentLabel != input.Label || current.AddressType != input.AddressType
             || current.Silent != input.Silent || current.Label != input.Label)
                throw new InvalidOperationException("Outbound opening input key metadata changed.");
            selected.Add(utxo);
        }
        wallet.RestoreLocksForChannel(channel.ChannelId, selected.Where(x => x.LockedToChannelId is null).Select(x => (x.TxId, x.Index)).ToArray());
        var model = fundingModels.Create(channel, selected, channel.ChangeAddress);
        var reconstructed = fundingBuilder.Build(model).Transaction;
        if (!reconstructed.RawTxBytes.AsSpan().SequenceEqual(intent.UnsignedTransaction) || model.Fee.MilliSatoshi != intent.FeeMsat)
            throw new InvalidOperationException("Outbound opening no longer builds its original funding transaction.");
    }

    private CapturedInput Capture(UtxoModel utxo)
    {
        var script = utxo.SilentPayment is { } silent ? new byte[] { 0x51, 0x20 }.Concat(silent.OutputKey).ToArray()
            : NBitcoin.BitcoinAddress.Create(utxo.WalletAddress?.Address
                ?? throw new InvalidOperationException("Funding input has no wallet address."), options.Value.BitcoinNetwork.ToNBitcoinNetwork()).ScriptPubKey.ToBytes();
        return new CapturedInput(Convert.ToHexString((byte[])utxo.TxId), utxo.Index, utxo.Amount.MilliSatoshi, (int)utxo.AddressType, script,
            WalletWeights.GetInputWeight(utxo.AddressType), utxo.SilentPayment is not null, utxo.SilentPayment?.Label);
    }
    private static WalletInput ToWalletInput(CapturedInput input) => new(new TxId(Convert.FromHexString(input.TxId)), input.Index,
        new LightningMoney(input.AmountMsat), (Domain.Bitcoin.Enums.AddressType)input.AddressType, new BitcoinScript(input.Script), input.Weight)
    { IsSilentPayment = input.Silent, SilentPaymentLabel = input.Label };
    private FundingCreatedMessage Reply(ChannelModel channel, FundedIntent intent, NativeOpeningReply receipt)
    {
        var temporaryId = new ChannelId(Convert.FromHexString(intent.TemporaryId));
        var funding = channel.FundingOutput!;
        return receipt.PartialSignature is { } partial
            ? messages.CreateFundingCreatedMessage(temporaryId, funding.TransactionId!.Value, funding.Index!.Value, partial)
            : messages.CreateFundingCreatedMessage(temporaryId, funding.TransactionId!.Value, funding.Index!.Value,
                receipt.Signature ?? throw new InvalidOperationException("Outbound opening lost its signing receipt."));
    }
    private async Task<byte[]> EncodeAsync(AcceptChannel1Message message)
    {
        using var stream = new MemoryStream();
        await serializer.SerializeAsync(message, stream);
        return stream.ToArray();
    }
    private static string Snapshot(ChannelModel channel) => Convert.ToHexString(SigningWorkflowSnapshot.CreateOpening(channel, SigningWorkflowKind.Opening).SnapshotFingerprint);
    private static void ValidateSnapshot(ChannelModel channel, FundedIntent intent)
    {
        if (!channel.IsInitiator || channel.LocalKeySet.KeyIndex != intent.KeyIndex || Snapshot(channel) != intent.Snapshot)
            throw new InvalidOperationException("Outbound opening changed its original negotiated channel.");
    }
    private static SigningWorkflowDescriptor Descriptor(ChannelId id, byte[] intent) => new(id, SigningWorkflowKind.Opening, 0, 0, SHA256.HashData(intent)) { PublicationIntent = intent };
    private static FundedIntent? ReadIntent(SigningWorkflow workflow)
    {
        if (workflow.PublicationIntent is not { } encoded) return null;
        using var json = JsonDocument.Parse(encoded);
        if (!json.RootElement.TryGetProperty("Direction", out var direction) || direction.GetString() != "outbound-funded") return null;
        if (!SHA256.HashData(encoded).AsSpan().SequenceEqual(workflow.SnapshotFingerprint))
            throw new InvalidOperationException("Outbound opening intent fingerprint changed.");
        return JsonSerializer.Deserialize<FundedIntent>(encoded) ?? throw new InvalidOperationException("Outbound opening intent is invalid.");
    }
    private sealed record CapturedInput(string TxId, uint Index, ulong AmountMsat, int AddressType, byte[] Script, int Weight, bool Silent, uint? Label);
    private sealed record FundedIntent(string Direction, string TemporaryId, uint KeyIndex, string Snapshot,
        byte[] InputMessage, byte[] Features, byte[]? RemoteOpeningNonce, Guid ReservationId,
        byte[] UnsignedTransaction, ulong FeeMsat, CapturedInput[] Inputs);
}