using System.Text.Json;
using NLightning.Domain.Bitcoin.Interfaces;
using NLightning.Domain.Bitcoin.Transactions.Models;
using NLightning.Domain.Bitcoin.ValueObjects;
using NLightning.Domain.Channels.ValueObjects;
using NLightning.Domain.Crypto.ValueObjects;
using NLightning.Domain.Money;
using NLightning.Domain.Onchain.Models;
using Channels = NLightning.Domain.Channels;
using DomainProtocol = NLightning.Domain.Protocol;
using Offers = NLightning.Domain.Offers;
using Wallet = NLightning.Domain.Bitcoin.Wallet;
namespace NLightning.Infrastructure.RemoteSigning;

public static class SignerDispatcher
{
    public static object?[] Execute(ILightningSigner signer, uint operation, JsonElement[] args)
    {
        switch (operation)
        {
            case SignerOperations.CreateNewChannel:
                {
                    var result = signer.CreateNewChannel(out var basepoints, out var firstPerCommitmentPoint);
                    return [result, basepoints, firstPerCommitmentPoint];
                }
            case SignerOperations.GetChannelBasepoints:
                {
                    var channelKeyIndex = SignerWire.Read<uint>(args[0]);
                    var result = signer.GetChannelBasepoints(channelKeyIndex);
                    return [result];
                }
            case SignerOperations.GetChannelBasepoints2:
                {
                    var channelId = SignerWire.Read<ChannelId>(args[0]);
                    var result = signer.GetChannelBasepoints(channelId);
                    return [result];
                }
            case SignerOperations.GetNodePublicKey:
                {
                    var result = signer.GetNodePublicKey();
                    return [result];
                }
            case SignerOperations.SignNodeMessage:
                {
                    var messageHash = SignerWire.Read<Hash>(args[0]);
                    var result = signer.SignNodeMessage(messageHash);
                    return [result];
                }
            case SignerOperations.VerifyNodeMessage:
                {
                    var messageHash = SignerWire.Read<Hash>(args[0]);
                    var signature = SignerWire.Read<CompactSignature>(args[1]);
                    var nodeId = SignerWire.Read<CompactPubKey>(args[2]);
                    var result = signer.VerifyNodeMessage(messageHash, signature, nodeId);
                    return [result];
                }
            case SignerOperations.SignChannelAnnouncement:
                {
                    var channelId = SignerWire.Read<ChannelId>(args[0]);
                    var unsignedAnnouncement = SignerWire.Read<ReadOnlyMemory<byte>>(args[1]);
                    var shortChannelId = SignerWire.Read<ShortChannelId>(args[2]);
                    var result = signer.SignChannelAnnouncement(channelId, unsignedAnnouncement, shortChannelId);
                    return [result];
                }
            case SignerOperations.CreateChannelAnnouncement2Nonces:
                {
                    var channelId = SignerWire.Read<ChannelId>(args[0]);
                    var unsignedAnnouncement = SignerWire.Read<DomainProtocol.Payloads.ChannelAnnouncement2Payload>(args[1]);
                    var result = signer.CreateChannelAnnouncement2Nonces(channelId, unsignedAnnouncement);
                    return [result];
                }
            case SignerOperations.SignChannelAnnouncement2:
                {
                    var channelId = SignerWire.Read<ChannelId>(args[0]);
                    var unsignedAnnouncement = SignerWire.Read<DomainProtocol.Payloads.ChannelAnnouncement2Payload>(args[1]);
                    var remoteNodeNonce = SignerWire.Read<MusigPublicNonce>(args[2]);
                    var remoteBitcoinNonce = SignerWire.Read<MusigPublicNonce>(args[3]);
                    var result = signer.SignChannelAnnouncement2(channelId, unsignedAnnouncement, remoteNodeNonce, remoteBitcoinNonce);
                    return [result];
                }
            case SignerOperations.DiscardChannelAnnouncement2Nonces:
                {
                    var channelId = SignerWire.Read<ChannelId>(args[0]);
                    signer.DiscardChannelAnnouncement2Nonces(channelId);
                    return [];
                }
            case SignerOperations.SignNodeMessageBip340:
                {
                    var messageHash = SignerWire.Read<Hash>(args[0]);
                    var result = signer.SignNodeMessageBip340(messageHash);
                    return [result];
                }
            case SignerOperations.SignLightningMessage:
                {
                    var message = SignerWire.Read<byte[]>(args[0]);
                    var singleHash = SignerWire.Read<bool>(args[1]);
                    var result = signer.SignLightningMessage(message, singleHash);
                    return [result];
                }
            case SignerOperations.GetPerCommitmentPoint:
                {
                    var channelKeyIndex = SignerWire.Read<uint>(args[0]);
                    var commitmentNumber = SignerWire.Read<ulong>(args[1]);
                    var result = signer.GetPerCommitmentPoint(channelKeyIndex, commitmentNumber);
                    return [result];
                }
            case SignerOperations.GetPerCommitmentPoint2:
                {
                    var channelId = SignerWire.Read<ChannelId>(args[0]);
                    var commitmentNumber = SignerWire.Read<ulong>(args[1]);
                    var result = signer.GetPerCommitmentPoint(channelId, commitmentNumber);
                    return [result];
                }
            case SignerOperations.RegisterChannel:
                {
                    var channelId = SignerWire.Read<ChannelId>(args[0]);
                    var signingInfo = SignerWire.Read<ChannelSigningInfo>(args[1]);
                    signer.RegisterChannel(channelId, signingInfo);
                    return [];
                }
            case SignerOperations.UnregisterChannel:
                {
                    var channelId = SignerWire.Read<ChannelId>(args[0]);
                    signer.UnregisterChannel(channelId);
                    return [];
                }
            case SignerOperations.RevealPerCommitmentSecret:
                {
                    var channelId = SignerWire.Read<ChannelId>(args[0]);
                    var commitmentNumber = SignerWire.Read<ulong>(args[1]);
                    var result = signer.RevealPerCommitmentSecret(channelId, commitmentNumber);
                    return [result];
                }
            case SignerOperations.AdvanceLocalCommitment:
                {
                    var channelId = SignerWire.Read<ChannelId>(args[0]);
                    var newLocalCommitmentNumber = SignerWire.Read<ulong>(args[1]);
                    signer.AdvanceLocalCommitment(channelId, newLocalCommitmentNumber);
                    return [];
                }
            case SignerOperations.SignRemoteHtlcTransactions:
                {
                    var channelId = SignerWire.Read<ChannelId>(args[0]);
                    var htlcTransactions = SignerWire.Read<IReadOnlyList<HtlcSigningContext>>(args[1]);
                    var result = signer.SignRemoteHtlcTransactions(channelId, htlcTransactions);
                    return [result];
                }
            case SignerOperations.ValidateLocalHtlcSignatures:
                {
                    var channelId = SignerWire.Read<ChannelId>(args[0]);
                    var htlcTransactions = SignerWire.Read<IReadOnlyList<HtlcSigningContext>>(args[1]);
                    var signatures = SignerWire.Read<IReadOnlyList<CompactSignature>>(args[2]);
                    signer.ValidateLocalHtlcSignatures(channelId, htlcTransactions, signatures);
                    return [];
                }
            case SignerOperations.SignLocalHtlcTransaction:
                {
                    var channelId = SignerWire.Read<ChannelId>(args[0]);
                    var htlcTransaction = SignerWire.Read<HtlcSigningContext>(args[1]);
                    var result = signer.SignLocalHtlcTransaction(channelId, htlcTransaction);
                    return [result];
                }
            case SignerOperations.SignLocalCommitmentForBroadcast:
                {
                    var channelId = SignerWire.Read<ChannelId>(args[0]);
                    var commitmentNumber = SignerWire.Read<ulong>(args[1]);
                    var unsignedCommitment = SignerWire.Read<SignedTransaction>(args[2]);
                    var remoteSignature = SignerWire.Read<CompactSignature>(args[3]);
                    var result = signer.SignLocalCommitmentForBroadcast(channelId, commitmentNumber, unsignedCommitment, remoteSignature);
                    return [result];
                }
            case SignerOperations.MarkDataLoss:
                {
                    var channelId = SignerWire.Read<ChannelId>(args[0]);
                    signer.MarkDataLoss(channelId);
                    return [];
                }
            case SignerOperations.MarkBroadcastSigned:
                {
                    var channelId = SignerWire.Read<ChannelId>(args[0]);
                    var commitmentNumber = SignerWire.Read<ulong>(args[1]);
                    signer.MarkBroadcastSigned(channelId, commitmentNumber);
                    return [];
                }
            case SignerOperations.TryGetBroadcastSignedCommitment:
                {
                    var channelId = SignerWire.Read<ChannelId>(args[0]);
                    var result = signer.TryGetBroadcastSignedCommitment(channelId, out var commitmentNumber);
                    return [result, commitmentNumber];
                }
            case SignerOperations.SignSweepInput:
                {
                    var channelId = SignerWire.Read<ChannelId>(args[0]);
                    var context = SignerWire.Read<SweepSigningContext>(args[1]);
                    var result = signer.SignSweepInput(channelId, context);
                    return [result];
                }
            case SignerOperations.SignWalletTransaction:
                {
                    var unsignedTransaction = SignerWire.Read<SignedTransaction>(args[0]);
                    var result = signer.SignWalletTransaction(unsignedTransaction);
                    return [result, unsignedTransaction];
                }
            case SignerOperations.SignWalletTransaction2:
                {
                    var unsignedTransaction = SignerWire.Read<SignedTransaction>(args[0]);
                    var otherSpentOutputs = SignerWire.Read<IReadOnlyList<Wallet.Models.SpentOutput>>(args[1]);
                    var result = signer.SignWalletTransaction(unsignedTransaction, otherSpentOutputs);
                    return [result, unsignedTransaction];
                }
            case SignerOperations.SignWalletTransaction3:
                {
                    var unsignedTransaction = SignerWire.Read<SignedTransaction>(args[0]);
                    var reservationId = SignerWire.Read<Guid>(args[1]);
                    var otherSpentOutputs = SignerWire.Read<IReadOnlyList<Wallet.Models.SpentOutput>>(args[2]);
                    var result = signer.SignWalletTransaction(unsignedTransaction, reservationId, otherSpentOutputs);
                    return [result, unsignedTransaction];
                }
            case SignerOperations.SignFundingTransaction:
                {
                    var channelId = SignerWire.Read<ChannelId>(args[0]);
                    var unsignedTransaction = SignerWire.Read<SignedTransaction>(args[1]);
                    var result = signer.SignFundingTransaction(channelId, unsignedTransaction);
                    return [result, unsignedTransaction];
                }
            case SignerOperations.SignChannelTransaction:
                {
                    var channelId = SignerWire.Read<ChannelId>(args[0]);
                    var unsignedTransaction = SignerWire.Read<SignedTransaction>(args[1]);
                    var result = signer.SignChannelTransaction(channelId, unsignedTransaction);
                    return [result];
                }
            case SignerOperations.ValidateSignature:
                {
                    var channelId = SignerWire.Read<ChannelId>(args[0]);
                    var signature = SignerWire.Read<CompactSignature>(args[1]);
                    var unsignedTransaction = SignerWire.Read<SignedTransaction>(args[2]);
                    signer.ValidateSignature(channelId, signature, unsignedTransaction);
                    return [];
                }
            case SignerOperations.SignAnchorInput:
                {
                    var channelId = SignerWire.Read<ChannelId>(args[0]);
                    var unsignedTransaction = SignerWire.Read<SignedTransaction>(args[1]);
                    var inputIndex = SignerWire.Read<int>(args[2]);
                    var amount = SignerWire.Read<LightningMoney>(args[3]);
                    var result = signer.SignAnchorInput(channelId, unsignedTransaction, inputIndex, amount);
                    return [result];
                }
            case SignerOperations.SignTaprootAnchorInput:
                {
                    var channelId = SignerWire.Read<ChannelId>(args[0]);
                    var unsignedTransaction = SignerWire.Read<SignedTransaction>(args[1]);
                    var inputIndex = SignerWire.Read<int>(args[2]);
                    var ourPerCommitmentPoint = SignerWire.Read<CompactPubKey?>(args[3]);
                    var spentOutputs = SignerWire.Read<IReadOnlyList<Wallet.Models.SpentOutput>>(args[4]);
                    var result = signer.SignTaprootAnchorInput(channelId, unsignedTransaction, inputIndex, ourPerCommitmentPoint, spentOutputs);
                    return [result];
                }
            case SignerOperations.GetLocalVerificationNonce:
                {
                    var channelKeyIndex = SignerWire.Read<uint>(args[0]);
                    var fundingTxId = SignerWire.Read<TxId?>(args[1]);
                    var localCommitmentNumber = SignerWire.Read<ulong>(args[2]);
                    var result = signer.GetLocalVerificationNonce(channelKeyIndex, fundingTxId, localCommitmentNumber);
                    return [result];
                }
            case SignerOperations.GetLocalVerificationNonce2:
                {
                    var channelId = SignerWire.Read<ChannelId>(args[0]);
                    var fundingTxId = SignerWire.Read<TxId?>(args[1]);
                    var localCommitmentNumber = SignerWire.Read<ulong>(args[2]);
                    var result = signer.GetLocalVerificationNonce(channelId, fundingTxId, localCommitmentNumber);
                    return [result];
                }
            case SignerOperations.SignRemoteCommitmentPartial:
                {
                    var channelId = SignerWire.Read<ChannelId>(args[0]);
                    var fundingTxId = SignerWire.Read<TxId?>(args[1]);
                    var unsignedCommitment = SignerWire.Read<SignedTransaction>(args[2]);
                    var remoteVerificationNonce = SignerWire.Read<MusigPublicNonce>(args[3]);
                    var result = signer.SignRemoteCommitmentPartial(channelId, fundingTxId, unsignedCommitment, remoteVerificationNonce);
                    return [result];
                }
            case SignerOperations.ValidateLocalCommitmentPartialSignature:
                {
                    var channelId = SignerWire.Read<ChannelId>(args[0]);
                    var fundingTxId = SignerWire.Read<TxId?>(args[1]);
                    var localCommitmentNumber = SignerWire.Read<ulong>(args[2]);
                    var remoteSignature = SignerWire.Read<MusigPartialSignatureWithNonce>(args[3]);
                    var unsignedCommitment = SignerWire.Read<SignedTransaction>(args[4]);
                    signer.ValidateLocalCommitmentPartialSignature(channelId, fundingTxId, localCommitmentNumber, remoteSignature, unsignedCommitment);
                    return [];
                }
            case SignerOperations.SignLocalCommitmentForBroadcast2:
                {
                    var channelId = SignerWire.Read<ChannelId>(args[0]);
                    var fundingTxId = SignerWire.Read<TxId?>(args[1]);
                    var commitmentNumber = SignerWire.Read<ulong>(args[2]);
                    var unsignedCommitment = SignerWire.Read<SignedTransaction>(args[3]);
                    var remoteSignature = SignerWire.Read<MusigPartialSignatureWithNonce>(args[4]);
                    var result = signer.SignLocalCommitmentForBroadcast(channelId, fundingTxId, commitmentNumber, unsignedCommitment, remoteSignature);
                    return [result];
                }
            case SignerOperations.CreateClosingNonce:
                {
                    var channelId = SignerWire.Read<ChannelId>(args[0]);
                    var result = signer.CreateClosingNonce(channelId);
                    return [result];
                }
            case SignerOperations.SignClosingAsCloser:
                {
                    var channelId = SignerWire.Read<ChannelId>(args[0]);
                    var unsignedClosing = SignerWire.Read<SignedTransaction>(args[1]);
                    var remoteCloseeNonce = SignerWire.Read<MusigPublicNonce>(args[2]);
                    var result = signer.SignClosingAsCloser(channelId, unsignedClosing, remoteCloseeNonce);
                    return [result];
                }
            case SignerOperations.SignClosingAsClosee:
                {
                    var channelId = SignerWire.Read<ChannelId>(args[0]);
                    var unsignedClosing = SignerWire.Read<SignedTransaction>(args[1]);
                    var localCloseeNonce = SignerWire.Read<MusigPublicNonce>(args[2]);
                    var remoteCloserSignature = SignerWire.Read<MusigPartialSignatureWithNonce>(args[3]);
                    var result = signer.SignClosingAsClosee(channelId, unsignedClosing, localCloseeNonce, remoteCloserSignature);
                    return [result];
                }
            case SignerOperations.ValidateClosingPartialSignature:
                {
                    var channelId = SignerWire.Read<ChannelId>(args[0]);
                    var unsignedClosing = SignerWire.Read<SignedTransaction>(args[1]);
                    var remoteSignature = SignerWire.Read<MusigPartialSignature>(args[2]);
                    var remoteNonce = SignerWire.Read<MusigPublicNonce>(args[3]);
                    var localNonce = SignerWire.Read<MusigPublicNonce>(args[4]);
                    signer.ValidateClosingPartialSignature(channelId, unsignedClosing, remoteSignature, remoteNonce, localNonce);
                    return [];
                }
            case SignerOperations.AggregateClosingSignature:
                {
                    var channelId = SignerWire.Read<ChannelId>(args[0]);
                    var unsignedClosing = SignerWire.Read<SignedTransaction>(args[1]);
                    var localSignature = SignerWire.Read<MusigPartialSignature>(args[2]);
                    var localNonce = SignerWire.Read<MusigPublicNonce>(args[3]);
                    var remoteSignature = SignerWire.Read<MusigPartialSignature>(args[4]);
                    var remoteNonce = SignerWire.Read<MusigPublicNonce>(args[5]);
                    var result = signer.AggregateClosingSignature(channelId, unsignedClosing, localSignature, localNonce, remoteSignature, remoteNonce);
                    return [result];
                }
            case SignerOperations.ForgetClosingNonces:
                {
                    var channelId = SignerWire.Read<ChannelId>(args[0]);
                    signer.ForgetClosingNonces(channelId);
                    return [];
                }
            case SignerOperations.GetLocalVerificationNonce3:
                {
                    var channelId = SignerWire.Read<ChannelId>(args[0]);
                    var fundingKeyIndex = SignerWire.Read<uint>(args[1]);
                    var fundingTxId = SignerWire.Read<TxId>(args[2]);
                    var localCommitmentNumber = SignerWire.Read<ulong>(args[3]);
                    var result = signer.GetLocalVerificationNonce(channelId, fundingKeyIndex, fundingTxId, localCommitmentNumber);
                    return [result];
                }
            case SignerOperations.CreateSpliceFundingNonce:
                {
                    var channelId = SignerWire.Read<ChannelId>(args[0]);
                    var result = signer.CreateSpliceFundingNonce(channelId);
                    return [result];
                }
            case SignerOperations.SignSpliceSharedInputPartial:
                {
                    var channelId = SignerWire.Read<ChannelId>(args[0]);
                    var newFundingTxId = SignerWire.Read<TxId>(args[1]);
                    var unsignedSpliceTransaction = SignerWire.Read<SignedTransaction>(args[2]);
                    var sharedInputIndex = SignerWire.Read<int>(args[3]);
                    var spentOutputs = SignerWire.Read<IReadOnlyList<Wallet.Models.SpentOutput>>(args[4]);
                    var localFundingNonce = SignerWire.Read<MusigPublicNonce>(args[5]);
                    var remoteFundingNonce = SignerWire.Read<MusigPublicNonce>(args[6]);
                    var result = signer.SignSpliceSharedInputPartial(channelId, newFundingTxId, unsignedSpliceTransaction, sharedInputIndex, spentOutputs, localFundingNonce, remoteFundingNonce);
                    return [result];
                }
            case SignerOperations.AggregateSpliceSharedInputSignature:
                {
                    var channelId = SignerWire.Read<ChannelId>(args[0]);
                    var unsignedSpliceTransaction = SignerWire.Read<SignedTransaction>(args[1]);
                    var sharedInputIndex = SignerWire.Read<int>(args[2]);
                    var spentOutputs = SignerWire.Read<IReadOnlyList<Wallet.Models.SpentOutput>>(args[3]);
                    var localSignature = SignerWire.Read<MusigPartialSignatureWithNonce>(args[4]);
                    var remoteSignature = SignerWire.Read<MusigPartialSignatureWithNonce>(args[5]);
                    var result = signer.AggregateSpliceSharedInputSignature(channelId, unsignedSpliceTransaction, sharedInputIndex, spentOutputs, localSignature, remoteSignature);
                    return [result];
                }
            case SignerOperations.GetFundingPubKey:
                {
                    var channelId = SignerWire.Read<ChannelId>(args[0]);
                    var fundingKeyIndex = SignerWire.Read<uint>(args[1]);
                    var result = signer.GetFundingPubKey(channelId, fundingKeyIndex);
                    return [result];
                }
            case SignerOperations.RegisterFunding:
                {
                    var channelId = SignerWire.Read<ChannelId>(args[0]);
                    var funding = SignerWire.Read<Channels.Splicing.ChannelFunding>(args[1]);
                    signer.RegisterFunding(channelId, funding);
                    return [];
                }
            case SignerOperations.MarkSpliceCommitmentPersisted:
                {
                    var channelId = SignerWire.Read<ChannelId>(args[0]);
                    var fundingTxId = SignerWire.Read<TxId>(args[1]);
                    var localCommitmentNumber = SignerWire.Read<ulong>(args[2]);
                    signer.MarkSpliceCommitmentPersisted(channelId, fundingTxId, localCommitmentNumber);
                    return [];
                }
            case SignerOperations.SignSpliceSharedInput:
                {
                    var channelId = SignerWire.Read<ChannelId>(args[0]);
                    var newFundingTxId = SignerWire.Read<TxId>(args[1]);
                    var unsignedSpliceTransaction = SignerWire.Read<SignedTransaction>(args[2]);
                    var sharedInputIndex = SignerWire.Read<int>(args[3]);
                    var result = signer.SignSpliceSharedInput(channelId, newFundingTxId, unsignedSpliceTransaction, sharedInputIndex);
                    return [result];
                }
            case SignerOperations.ValidateSpliceSharedInputSignature:
                {
                    var channelId = SignerWire.Read<ChannelId>(args[0]);
                    var unsignedSpliceTransaction = SignerWire.Read<SignedTransaction>(args[1]);
                    var sharedInputIndex = SignerWire.Read<int>(args[2]);
                    var remoteSignature = SignerWire.Read<CompactSignature>(args[3]);
                    signer.ValidateSpliceSharedInputSignature(channelId, unsignedSpliceTransaction, sharedInputIndex, remoteSignature);
                    return [];
                }
            case SignerOperations.SignChannelTransaction2:
                {
                    var channelId = SignerWire.Read<ChannelId>(args[0]);
                    var fundingTxId = SignerWire.Read<TxId>(args[1]);
                    var unsignedTransaction = SignerWire.Read<SignedTransaction>(args[2]);
                    var result = signer.SignChannelTransaction(channelId, fundingTxId, unsignedTransaction);
                    return [result];
                }
            case SignerOperations.ValidateSignature2:
                {
                    var channelId = SignerWire.Read<ChannelId>(args[0]);
                    var fundingTxId = SignerWire.Read<TxId>(args[1]);
                    var signature = SignerWire.Read<CompactSignature>(args[2]);
                    var unsignedTransaction = SignerWire.Read<SignedTransaction>(args[3]);
                    signer.ValidateSignature(channelId, fundingTxId, signature, unsignedTransaction);
                    return [];
                }
            case SignerOperations.SignLocalCommitmentForBroadcast3:
                {
                    var channelId = SignerWire.Read<ChannelId>(args[0]);
                    var fundingTxId = SignerWire.Read<TxId>(args[1]);
                    var commitmentNumber = SignerWire.Read<ulong>(args[2]);
                    var unsignedCommitment = SignerWire.Read<SignedTransaction>(args[3]);
                    var remoteSignature = SignerWire.Read<CompactSignature>(args[4]);
                    var result = signer.SignLocalCommitmentForBroadcast(channelId, fundingTxId, commitmentNumber, unsignedCommitment, remoteSignature);
                    return [result];
                }
            case SignerOperations.LockFunding:
                {
                    var channelId = SignerWire.Read<ChannelId>(args[0]);
                    var fundingTxId = SignerWire.Read<TxId>(args[1]);
                    signer.LockFunding(channelId, fundingTxId);
                    return [];
                }
            case SignerOperations.LockFunding2:
                {
                    var channelId = SignerWire.Read<ChannelId>(args[0]);
                    var fundingTxId = SignerWire.Read<TxId>(args[1]);
                    var shortChannelId = SignerWire.Read<ShortChannelId?>(args[2]);
                    signer.LockFunding(channelId, fundingTxId, shortChannelId);
                    return [];
                }
            case SignerOperations.GetBolt12PayerId:
                {
                    var invoiceRequestMetadata = SignerWire.Read<ReadOnlyMemory<byte>>(args[0]);
                    var result = signer.GetBolt12PayerId(invoiceRequestMetadata);
                    return [result];
                }
            case SignerOperations.SignBolt12:
                {
                    var key = SignerWire.Read<Offers.Models.Bolt12SigningKey>(args[0]);
                    var tag = SignerWire.Read<string>(args[1]);
                    var merkleRoot = SignerWire.Read<Hash>(args[2]);
                    var result = signer.SignBolt12(key, tag, merkleRoot);
                    return [result];
                }
            default: throw new ArgumentException("Unknown signing operation.");
        }
    }
}