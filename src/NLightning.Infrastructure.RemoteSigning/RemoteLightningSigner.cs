using NLightning.Domain.Bitcoin.Interfaces;
using NLightning.Domain.Bitcoin.SilentPayments;
using NLightning.Domain.Bitcoin.Transactions.Models;
using NLightning.Domain.Bitcoin.ValueObjects;
using NLightning.Domain.Bitcoin.Wallet.Models;
using NLightning.Domain.Channels.ValueObjects;
using NLightning.Domain.Crypto.ValueObjects;
using NLightning.Domain.Money;
using NLightning.Domain.Onchain.Models;
using Channels = NLightning.Domain.Channels;
using DomainProtocol = NLightning.Domain.Protocol;
using Offers = NLightning.Domain.Offers;
using Wallet = NLightning.Domain.Bitcoin.Wallet;

namespace NLightning.Infrastructure.RemoteSigning;

public sealed class RemoteLightningSigner : ILightningSigner
{
    private readonly RemoteSignerConnection _connection;
    private readonly IChannelSigningInfoSource? _source;
    private readonly IUtxoMemoryRepository? _wallet;
    public RemoteLightningSigner(RemoteSignerConnection connection, IChannelSigningInfoSource? source = null, IUtxoMemoryRepository? wallet = null) { _connection = connection; _source = source; _wallet = wallet; }
    public uint CreateNewChannel(out ChannelBasepoints basepoints, out CompactPubKey firstPerCommitmentPoint)
    {
        var result = _connection.Invoke(SignerOperations.CreateNewChannel);
        basepoints = SignerWire.Read<ChannelBasepoints>(result[1]);
        firstPerCommitmentPoint = SignerWire.Read<CompactPubKey>(result[2]);
        return SignerWire.Read<uint>(result[0]);
    }
    public ChannelBasepoints GetChannelBasepoints(uint channelKeyIndex)
    {
        var result = _connection.Invoke(SignerOperations.GetChannelBasepoints, channelKeyIndex);
        return SignerWire.Read<ChannelBasepoints>(result[0]);
    }
    public ChannelBasepoints GetChannelBasepoints(ChannelId channelId)
    {
        EnsureChannel(channelId);
        var result = _connection.Invoke(SignerOperations.GetChannelBasepoints2, channelId);
        return SignerWire.Read<ChannelBasepoints>(result[0]);
    }
    public CompactPubKey GetNodePublicKey()
    {
        var result = _connection.Invoke(SignerOperations.GetNodePublicKey);
        return SignerWire.Read<CompactPubKey>(result[0]);
    }
    public CompactSignature SignNodeMessage(Hash messageHash)
    {
        var result = _connection.Invoke(SignerOperations.SignNodeMessage, messageHash);
        return SignerWire.Read<CompactSignature>(result[0]);
    }
    public bool VerifyNodeMessage(Hash messageHash, CompactSignature signature, CompactPubKey nodeId)
    {
        var result = _connection.Invoke(SignerOperations.VerifyNodeMessage, messageHash, signature, nodeId);
        return SignerWire.Read<bool>(result[0]);
    }
    public ChannelAnnouncementSignatures SignChannelAnnouncement(ChannelId channelId, ReadOnlyMemory<byte> unsignedAnnouncement, ShortChannelId shortChannelId)
    {
        EnsureChannel(channelId);
        var result = _connection.Invoke(SignerOperations.SignChannelAnnouncement, channelId, unsignedAnnouncement, shortChannelId);
        return SignerWire.Read<ChannelAnnouncementSignatures>(result[0]);
    }
    public ChannelAnnouncement2Nonces CreateChannelAnnouncement2Nonces(ChannelId channelId, DomainProtocol.Payloads.ChannelAnnouncement2Payload unsignedAnnouncement)
    {
        EnsureChannel(channelId);
        var result = _connection.Invoke(SignerOperations.CreateChannelAnnouncement2Nonces, channelId, unsignedAnnouncement);
        return SignerWire.Read<ChannelAnnouncement2Nonces>(result[0]);
    }
    public ChannelAnnouncement2PartialSignatures SignChannelAnnouncement2(ChannelId channelId, DomainProtocol.Payloads.ChannelAnnouncement2Payload unsignedAnnouncement, MusigPublicNonce remoteNodeNonce, MusigPublicNonce remoteBitcoinNonce)
    {
        EnsureChannel(channelId);
        var result = _connection.Invoke(SignerOperations.SignChannelAnnouncement2, channelId, unsignedAnnouncement, remoteNodeNonce, remoteBitcoinNonce);
        return SignerWire.Read<ChannelAnnouncement2PartialSignatures>(result[0]);
    }
    public void DiscardChannelAnnouncement2Nonces(ChannelId channelId)
    {
        EnsureChannel(channelId);
        var result = _connection.Invoke(SignerOperations.DiscardChannelAnnouncement2Nonces, channelId);
    }
    public CompactSignature SignNodeMessageBip340(Hash messageHash)
    {
        var result = _connection.Invoke(SignerOperations.SignNodeMessageBip340, messageHash);
        return SignerWire.Read<CompactSignature>(result[0]);
    }
    public byte[] SignLightningMessage(ReadOnlySpan<byte> message, bool singleHash)
    {
        var result = _connection.Invoke(SignerOperations.SignLightningMessage, message.ToArray(), singleHash);
        return SignerWire.Read<byte[]>(result[0]);
    }
    public CompactPubKey GetPerCommitmentPoint(uint channelKeyIndex, ulong commitmentNumber)
    {
        var result = _connection.Invoke(SignerOperations.GetPerCommitmentPoint, channelKeyIndex, commitmentNumber);
        return SignerWire.Read<CompactPubKey>(result[0]);
    }
    public CompactPubKey GetPerCommitmentPoint(ChannelId channelId, ulong commitmentNumber)
    {
        EnsureChannel(channelId);
        var result = _connection.Invoke(SignerOperations.GetPerCommitmentPoint2, channelId, commitmentNumber);
        return SignerWire.Read<CompactPubKey>(result[0]);
    }
    public void RegisterChannel(ChannelId channelId, ChannelSigningInfo signingInfo)
    {
        var result = _connection.Invoke(SignerOperations.RegisterChannel, channelId, signingInfo);
    }
    public void UnregisterChannel(ChannelId channelId)
    {
        var result = _connection.Invoke(SignerOperations.UnregisterChannel, channelId);
    }
    public Secret RevealPerCommitmentSecret(ChannelId channelId, ulong commitmentNumber)
    {
        EnsureChannel(channelId);
        var result = _connection.Invoke(SignerOperations.RevealPerCommitmentSecret, channelId, commitmentNumber);
        return SignerWire.Read<Secret>(result[0]);
    }
    public void AdvanceLocalCommitment(ChannelId channelId, ulong newLocalCommitmentNumber)
    {
        EnsureChannel(channelId);
        var result = _connection.Invoke(SignerOperations.AdvanceLocalCommitment, channelId, newLocalCommitmentNumber);
    }
    public IReadOnlyList<CompactSignature> SignRemoteHtlcTransactions(ChannelId channelId, IReadOnlyList<HtlcSigningContext> htlcTransactions)
    {
        EnsureChannel(channelId);
        var result = _connection.Invoke(SignerOperations.SignRemoteHtlcTransactions, channelId, htlcTransactions);
        return SignerWire.Read<IReadOnlyList<CompactSignature>>(result[0]);
    }
    public void ValidateLocalHtlcSignatures(ChannelId channelId, IReadOnlyList<HtlcSigningContext> htlcTransactions, IReadOnlyList<CompactSignature> signatures)
    {
        EnsureChannel(channelId);
        var result = _connection.Invoke(SignerOperations.ValidateLocalHtlcSignatures, channelId, htlcTransactions, signatures);
    }
    public CompactSignature SignLocalHtlcTransaction(ChannelId channelId, HtlcSigningContext htlcTransaction)
    {
        EnsureChannel(channelId);
        var result = _connection.Invoke(SignerOperations.SignLocalHtlcTransaction, channelId, htlcTransaction);
        return SignerWire.Read<CompactSignature>(result[0]);
    }
    public SignedTransaction SignLocalCommitmentForBroadcast(ChannelId channelId, ulong commitmentNumber, SignedTransaction unsignedCommitment, CompactSignature remoteSignature)
    {
        EnsureChannel(channelId);
        var result = _connection.Invoke(SignerOperations.SignLocalCommitmentForBroadcast, channelId, commitmentNumber, unsignedCommitment, remoteSignature);
        return SignerWire.Read<SignedTransaction>(result[0]);
    }
    public void MarkDataLoss(ChannelId channelId)
    {
        EnsureChannel(channelId);
        var result = _connection.Invoke(SignerOperations.MarkDataLoss, channelId);
    }
    public void MarkBroadcastSigned(ChannelId channelId, ulong commitmentNumber)
    {
        EnsureChannel(channelId);
        var result = _connection.Invoke(SignerOperations.MarkBroadcastSigned, channelId, commitmentNumber);
    }
    public bool TryGetBroadcastSignedCommitment(ChannelId channelId, out ulong commitmentNumber)
    {
        EnsureChannel(channelId);
        var result = _connection.Invoke(SignerOperations.TryGetBroadcastSignedCommitment, channelId);
        commitmentNumber = SignerWire.Read<ulong>(result[1]);
        return SignerWire.Read<bool>(result[0]);
    }
    public CompactSignature SignSweepInput(ChannelId channelId, SweepSigningContext context)
    {
        EnsureChannel(channelId);
        var result = _connection.Invoke(SignerOperations.SignSweepInput, channelId, context);
        return SignerWire.Read<CompactSignature>(result[0]);
    }
    public bool SignWalletTransaction(SignedTransaction unsignedTransaction)
    {
        var result = _connection.Invoke(SignerOperations.SignWalletTransaction, unsignedTransaction, WalletSnapshot.Create(_wallet, unsignedTransaction));
        var updated = SignerWire.Read<SignedTransaction>(result[1]); unsignedTransaction.RawTxBytes = updated.RawTxBytes; unsignedTransaction.TxId = updated.TxId; unsignedTransaction.Signatures = updated.Signatures;
        return SignerWire.Read<bool>(result[0]);
    }
    public bool SignWalletTransaction(SignedTransaction unsignedTransaction, IReadOnlyList<Wallet.Models.SpentOutput> otherSpentOutputs)
    {
        var result = _connection.Invoke(SignerOperations.SignWalletTransaction2, unsignedTransaction, otherSpentOutputs, WalletSnapshot.Create(_wallet, unsignedTransaction));
        var updated = SignerWire.Read<SignedTransaction>(result[1]); unsignedTransaction.RawTxBytes = updated.RawTxBytes; unsignedTransaction.TxId = updated.TxId; unsignedTransaction.Signatures = updated.Signatures;
        return SignerWire.Read<bool>(result[0]);
    }
    public bool SignWalletTransaction(SignedTransaction unsignedTransaction, Guid reservationId, IReadOnlyList<Wallet.Models.SpentOutput> otherSpentOutputs)
    {
        var result = _connection.Invoke(SignerOperations.SignWalletTransaction3, unsignedTransaction, reservationId, otherSpentOutputs, WalletSnapshot.Create(_wallet, unsignedTransaction));
        var updated = SignerWire.Read<SignedTransaction>(result[1]); unsignedTransaction.RawTxBytes = updated.RawTxBytes; unsignedTransaction.TxId = updated.TxId; unsignedTransaction.Signatures = updated.Signatures;
        return SignerWire.Read<bool>(result[0]);
    }
    public bool SignFundingTransaction(ChannelId channelId, SignedTransaction unsignedTransaction)
    {
        EnsureChannel(channelId);
        var result = _connection.Invoke(SignerOperations.SignFundingTransaction, channelId, unsignedTransaction, WalletSnapshot.Create(_wallet, unsignedTransaction));
        var updated = SignerWire.Read<SignedTransaction>(result[1]); unsignedTransaction.RawTxBytes = updated.RawTxBytes; unsignedTransaction.TxId = updated.TxId; unsignedTransaction.Signatures = updated.Signatures;
        return SignerWire.Read<bool>(result[0]);
    }
    public CompactSignature SignChannelTransaction(ChannelId channelId, SignedTransaction unsignedTransaction)
    {
        EnsureChannel(channelId);
        var result = _connection.Invoke(SignerOperations.SignChannelTransaction, channelId, unsignedTransaction);
        return SignerWire.Read<CompactSignature>(result[0]);
    }
    public void ValidateSignature(ChannelId channelId, CompactSignature signature, SignedTransaction unsignedTransaction)
    {
        EnsureChannel(channelId);
        var result = _connection.Invoke(SignerOperations.ValidateSignature, channelId, signature, unsignedTransaction);
    }
    public CompactSignature SignAnchorInput(ChannelId channelId, SignedTransaction unsignedTransaction, int inputIndex, LightningMoney amount)
    {
        EnsureChannel(channelId);
        var result = _connection.Invoke(SignerOperations.SignAnchorInput, channelId, unsignedTransaction, inputIndex, amount);
        return SignerWire.Read<CompactSignature>(result[0]);
    }
    public CompactSignature SignTaprootAnchorInput(ChannelId channelId, SignedTransaction unsignedTransaction, int inputIndex, CompactPubKey? ourPerCommitmentPoint, IReadOnlyList<Wallet.Models.SpentOutput> spentOutputs)
    {
        EnsureChannel(channelId);
        var result = _connection.Invoke(SignerOperations.SignTaprootAnchorInput, channelId, unsignedTransaction, inputIndex, ourPerCommitmentPoint, spentOutputs);
        return SignerWire.Read<CompactSignature>(result[0]);
    }
    public MusigPublicNonce GetLocalVerificationNonce(uint channelKeyIndex, TxId? fundingTxId, ulong localCommitmentNumber)
    {
        var result = _connection.Invoke(SignerOperations.GetLocalVerificationNonce, channelKeyIndex, fundingTxId, localCommitmentNumber);
        return SignerWire.Read<MusigPublicNonce>(result[0]);
    }
    public MusigPublicNonce GetLocalVerificationNonce(ChannelId channelId, TxId? fundingTxId, ulong localCommitmentNumber)
    {
        EnsureChannel(channelId);
        var result = _connection.Invoke(SignerOperations.GetLocalVerificationNonce2, channelId, fundingTxId, localCommitmentNumber);
        return SignerWire.Read<MusigPublicNonce>(result[0]);
    }
    public MusigPartialSignatureWithNonce SignRemoteCommitmentPartial(ChannelId channelId, TxId? fundingTxId, SignedTransaction unsignedCommitment, MusigPublicNonce remoteVerificationNonce)
    {
        EnsureChannel(channelId);
        var result = _connection.Invoke(SignerOperations.SignRemoteCommitmentPartial, channelId, fundingTxId, unsignedCommitment, remoteVerificationNonce);
        return SignerWire.Read<MusigPartialSignatureWithNonce>(result[0]);
    }
    public void ValidateLocalCommitmentPartialSignature(ChannelId channelId, TxId? fundingTxId, ulong localCommitmentNumber, MusigPartialSignatureWithNonce remoteSignature, SignedTransaction unsignedCommitment)
    {
        EnsureChannel(channelId);
        var result = _connection.Invoke(SignerOperations.ValidateLocalCommitmentPartialSignature, channelId, fundingTxId, localCommitmentNumber, remoteSignature, unsignedCommitment);
    }
    public SignedTransaction SignLocalCommitmentForBroadcast(ChannelId channelId, TxId? fundingTxId, ulong commitmentNumber, SignedTransaction unsignedCommitment, MusigPartialSignatureWithNonce remoteSignature)
    {
        EnsureChannel(channelId);
        var result = _connection.Invoke(SignerOperations.SignLocalCommitmentForBroadcast2, channelId, fundingTxId, commitmentNumber, unsignedCommitment, remoteSignature);
        return SignerWire.Read<SignedTransaction>(result[0]);
    }
    public MusigPublicNonce CreateClosingNonce(ChannelId channelId)
    {
        EnsureChannel(channelId);
        var result = _connection.Invoke(SignerOperations.CreateClosingNonce, channelId);
        return SignerWire.Read<MusigPublicNonce>(result[0]);
    }
    public MusigPartialSignatureWithNonce SignClosingAsCloser(ChannelId channelId, SignedTransaction unsignedClosing, MusigPublicNonce remoteCloseeNonce)
    {
        EnsureChannel(channelId);
        var result = _connection.Invoke(SignerOperations.SignClosingAsCloser, channelId, unsignedClosing, remoteCloseeNonce);
        return SignerWire.Read<MusigPartialSignatureWithNonce>(result[0]);
    }
    public MusigPartialSignature SignClosingAsClosee(ChannelId channelId, SignedTransaction unsignedClosing, MusigPublicNonce localCloseeNonce, MusigPartialSignatureWithNonce remoteCloserSignature)
    {
        EnsureChannel(channelId);
        var result = _connection.Invoke(SignerOperations.SignClosingAsClosee, channelId, unsignedClosing, localCloseeNonce, remoteCloserSignature);
        return SignerWire.Read<MusigPartialSignature>(result[0]);
    }
    public void ValidateClosingPartialSignature(ChannelId channelId, SignedTransaction unsignedClosing, MusigPartialSignature remoteSignature, MusigPublicNonce remoteNonce, MusigPublicNonce localNonce)
    {
        EnsureChannel(channelId);
        var result = _connection.Invoke(SignerOperations.ValidateClosingPartialSignature, channelId, unsignedClosing, remoteSignature, remoteNonce, localNonce);
    }
    public SignedTransaction AggregateClosingSignature(ChannelId channelId, SignedTransaction unsignedClosing, MusigPartialSignature localSignature, MusigPublicNonce localNonce, MusigPartialSignature remoteSignature, MusigPublicNonce remoteNonce)
    {
        EnsureChannel(channelId);
        var result = _connection.Invoke(SignerOperations.AggregateClosingSignature, channelId, unsignedClosing, localSignature, localNonce, remoteSignature, remoteNonce);
        return SignerWire.Read<SignedTransaction>(result[0]);
    }
    public void ForgetClosingNonces(ChannelId channelId)
    {
        EnsureChannel(channelId);
        var result = _connection.Invoke(SignerOperations.ForgetClosingNonces, channelId);
    }
    public MusigPublicNonce GetLocalVerificationNonce(ChannelId channelId, uint fundingKeyIndex, TxId fundingTxId, ulong localCommitmentNumber)
    {
        EnsureChannel(channelId);
        var result = _connection.Invoke(SignerOperations.GetLocalVerificationNonce3, channelId, fundingKeyIndex, fundingTxId, localCommitmentNumber);
        return SignerWire.Read<MusigPublicNonce>(result[0]);
    }
    public MusigPublicNonce CreateSpliceFundingNonce(ChannelId channelId)
    {
        EnsureChannel(channelId);
        var result = _connection.Invoke(SignerOperations.CreateSpliceFundingNonce, channelId);
        return SignerWire.Read<MusigPublicNonce>(result[0]);
    }
    public MusigPartialSignatureWithNonce SignSpliceSharedInputPartial(ChannelId channelId, TxId newFundingTxId, SignedTransaction unsignedSpliceTransaction, int sharedInputIndex, IReadOnlyList<Wallet.Models.SpentOutput> spentOutputs, MusigPublicNonce localFundingNonce, MusigPublicNonce remoteFundingNonce)
    {
        EnsureChannel(channelId);
        var result = _connection.Invoke(SignerOperations.SignSpliceSharedInputPartial, channelId, newFundingTxId, unsignedSpliceTransaction, sharedInputIndex, spentOutputs, localFundingNonce, remoteFundingNonce);
        return SignerWire.Read<MusigPartialSignatureWithNonce>(result[0]);
    }
    public byte[] AggregateSpliceSharedInputSignature(ChannelId channelId, SignedTransaction unsignedSpliceTransaction, int sharedInputIndex, IReadOnlyList<Wallet.Models.SpentOutput> spentOutputs, MusigPartialSignatureWithNonce localSignature, MusigPartialSignatureWithNonce remoteSignature)
    {
        EnsureChannel(channelId);
        var result = _connection.Invoke(SignerOperations.AggregateSpliceSharedInputSignature, channelId, unsignedSpliceTransaction, sharedInputIndex, spentOutputs, localSignature, remoteSignature);
        return SignerWire.Read<byte[]>(result[0]);
    }
    public CompactPubKey GetFundingPubKey(ChannelId channelId, uint fundingKeyIndex)
    {
        EnsureChannel(channelId);
        var result = _connection.Invoke(SignerOperations.GetFundingPubKey, channelId, fundingKeyIndex);
        return SignerWire.Read<CompactPubKey>(result[0]);
    }
    public void RegisterFunding(ChannelId channelId, Channels.Splicing.ChannelFunding funding)
    {
        EnsureChannel(channelId);
        var result = _connection.Invoke(SignerOperations.RegisterFunding, channelId, funding);
    }
    public void MarkSpliceCommitmentPersisted(ChannelId channelId, TxId fundingTxId, ulong localCommitmentNumber)
    {
        EnsureChannel(channelId);
        var result = _connection.Invoke(SignerOperations.MarkSpliceCommitmentPersisted, channelId, fundingTxId, localCommitmentNumber);
    }
    public CompactSignature SignSpliceSharedInput(ChannelId channelId, TxId newFundingTxId, SignedTransaction unsignedSpliceTransaction, int sharedInputIndex)
    {
        EnsureChannel(channelId);
        var result = _connection.Invoke(SignerOperations.SignSpliceSharedInput, channelId, newFundingTxId, unsignedSpliceTransaction, sharedInputIndex);
        return SignerWire.Read<CompactSignature>(result[0]);
    }
    public void ValidateSpliceSharedInputSignature(ChannelId channelId, SignedTransaction unsignedSpliceTransaction, int sharedInputIndex, CompactSignature remoteSignature)
    {
        EnsureChannel(channelId);
        var result = _connection.Invoke(SignerOperations.ValidateSpliceSharedInputSignature, channelId, unsignedSpliceTransaction, sharedInputIndex, remoteSignature);
    }
    public CompactSignature SignChannelTransaction(ChannelId channelId, TxId fundingTxId, SignedTransaction unsignedTransaction)
    {
        EnsureChannel(channelId);
        var result = _connection.Invoke(SignerOperations.SignChannelTransaction2, channelId, fundingTxId, unsignedTransaction);
        return SignerWire.Read<CompactSignature>(result[0]);
    }
    public void ValidateSignature(ChannelId channelId, TxId fundingTxId, CompactSignature signature, SignedTransaction unsignedTransaction)
    {
        EnsureChannel(channelId);
        var result = _connection.Invoke(SignerOperations.ValidateSignature2, channelId, fundingTxId, signature, unsignedTransaction);
    }
    public SignedTransaction SignLocalCommitmentForBroadcast(ChannelId channelId, TxId fundingTxId, ulong commitmentNumber, SignedTransaction unsignedCommitment, CompactSignature remoteSignature)
    {
        EnsureChannel(channelId);
        var result = _connection.Invoke(SignerOperations.SignLocalCommitmentForBroadcast3, channelId, fundingTxId, commitmentNumber, unsignedCommitment, remoteSignature);
        return SignerWire.Read<SignedTransaction>(result[0]);
    }
    public void LockFunding(ChannelId channelId, TxId fundingTxId)
    {
        EnsureChannel(channelId);
        var result = _connection.Invoke(SignerOperations.LockFunding, channelId, fundingTxId);
    }
    public void LockFunding(ChannelId channelId, TxId fundingTxId, ShortChannelId? shortChannelId)
    {
        EnsureChannel(channelId);
        var result = _connection.Invoke(SignerOperations.LockFunding2, channelId, fundingTxId, shortChannelId);
    }
    public CompactPubKey GetBolt12PayerId(ReadOnlyMemory<byte> invoiceRequestMetadata)
    {
        var result = _connection.Invoke(SignerOperations.GetBolt12PayerId, invoiceRequestMetadata);
        return SignerWire.Read<CompactPubKey>(result[0]);
    }
    public byte[] SignBolt12(Offers.Models.Bolt12SigningKey key, string tag, Hash merkleRoot)
    {
        var result = _connection.Invoke(SignerOperations.SignBolt12, key, tag, merkleRoot);
        return SignerWire.Read<byte[]>(result[0]);
    }
    public IReadOnlyList<BitcoinScript> ComputeSilentPaymentOutputs(Guid reservationId,
        IReadOnlyList<SilentPaymentAddress> recipients, IReadOnlyList<(TxId TxId, uint Index)> allInputs)
    {
        ArgumentNullException.ThrowIfNull(recipients);
        ArgumentNullException.ThrowIfNull(allInputs);
        var outpoints = allInputs.Select(input => new WalletOutpoint(input.TxId, input.Index)).ToArray();
        var result = _connection.Invoke(SignerOperations.ComputeSilentPaymentOutputs, reservationId, recipients,
            outpoints, WalletSnapshot.CreateReservation(_wallet, reservationId, outpoints));
        return SignerWire.Read<IReadOnlyList<BitcoinScript>>(result[0]);
    }
    public byte[] SignWalletMessage(WalletAddressModel address, byte[] message)
    {
        var result = _connection.Invoke(SignerOperations.SignWalletMessage, address, message);
        return SignerWire.Read<byte[]>(result[0]);
    }
    private void EnsureChannel(ChannelId id) { if (_source is not null && _source.TryGet(id, out var info)) RegisterChannel(id, info); }
}