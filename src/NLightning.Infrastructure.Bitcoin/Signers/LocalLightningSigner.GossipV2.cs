using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace NLightning.Infrastructure.Bitcoin.Signers;

using Crypto.Contexts;
using Domain.Channels.Splicing.Enums;
using Domain.Channels.ValueObjects;
using Domain.Crypto.Constants;
using Domain.Crypto.Models;
using Domain.Crypto.ValueObjects;
using Domain.Exceptions;
using Domain.Protocol.Payloads;
using Infrastructure.Crypto.Factories;

/// <summary>
/// Taproot gossip (BOLTs PR #1059 draft <c>4eef3dfa</c>, NL-878 T7): the MuSig2 session of a public simple taproot
/// channel's <c>channel_announcement_2</c> and BIP 340 node signatures of <c>channel_update_2</c>/
/// <c>node_announcement_2</c>.
/// </summary>
/// <remarks>
/// <para>The announcement is a 4-of-4 MuSig2: <c>KeyAgg(KeySort(node_id_1, node_id_2, bitcoin_key_1, bitcoin_key_2))</c>
/// with no tweak, two participants per side (our node key and our funding key). Each side sends two public nonces and
/// then two partial signatures.</para>
/// <para><b>Nonces</b> are fresh randomness mixed with the signing key, the aggregate key and the message
/// (<see cref="IMusig2Service.GenerateNonce(CompactPubKey, PrivKey, byte[], byte[], byte[])"/>), held in the
/// signer-owned nonce journal when configured and otherwise in memory,
/// one pair per channel, bound to the announcement's message hash, and consumed by the one signature they make. A new
/// pair (a new connection or a new <c>channel_reestablish</c>) disposes the previous one, and a restart forgets them:
/// the peer then asks for a fresh session (<c>channel_reestablish</c> retransmit bit 1 and TLV 7), so a nonce never
/// signs twice.</para>
/// </remarks>
public partial class LocalLightningSigner
{
    // Channel id -> our live channel_announcement_2 nonce pair (secret halves) and the message it is bound to
    private readonly ConcurrentDictionary<ChannelId, Announcement2NonceSet> _announcement2Nonces = new();

    /// <inheritdoc />
    public ChannelAnnouncement2Nonces CreateChannelAnnouncement2Nonces(ChannelId channelId,
                                                                       ChannelAnnouncement2Payload unsignedAnnouncement)
    {
        ArgumentNullException.ThrowIfNull(unsignedAnnouncement);
        var (signingInfo, funding) = CheckAnnouncement2(channelId, unsignedAnnouncement, allowPendingSplice: true);
        var aggregate = GetAnnouncement2Aggregate(unsignedAnnouncement);
        byte[] message = unsignedAnnouncement.GetSignatureHash();

        MusigNoncePair nodePair;
        var nodeKey = _secureKeyManager.GetNodeKeyPair();
        var nodePrivateKey = nodeKey.PrivKey.Value.ToArray();
        try
        {
            nodePair = _musig2.GenerateNonce(nodeKey.CompactPubKey, nodePrivateKey, aggregate.XOnlyOutputKey,
                                             message);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(nodePrivateKey);
        }

        MusigNoncePair bitcoinPair;
        try
        {
            using var fundingKey = DeriveTaprootFundingKey(channelId, signingInfo.ChannelKeyIndex, funding);
            var privateKey = fundingKey.ToBytes();
            try
            {
                bitcoinPair = _musig2.GenerateNonce(funding.LocalPubKey, privateKey, aggregate.XOnlyOutputKey,
                                                    message);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(privateKey);
            }
        }
        catch
        {
            // The node nonce never outlives a failed call (NL-911)
            nodePair.SecretNonce.Dispose();
            throw;
        }

        if (_nativeNonceStore is { } store)
        {
            store.Forget("gossip-node", channelId);
            store.Forget("gossip-bitcoin", channelId);
            nodePair = store.Store("gossip-node", channelId, nodePair, message);
            bitcoinPair = store.Store("gossip-bitcoin", channelId, bitcoinPair, message);
        }
        var set = new Announcement2NonceSet(nodePair, bitcoinPair, message);
        _announcement2Nonces.AddOrUpdate(channelId, set, (_, previous) =>
        {
            previous.Dispose();
            return set;
        });

        _logger.LogDebug("Created fresh channel_announcement_2 nonces for channel {ChannelId}", channelId);
        return new ChannelAnnouncement2Nonces(nodePair.PublicNonce, bitcoinPair.PublicNonce);
    }

    /// <inheritdoc />
    public ChannelAnnouncement2PartialSignatures SignChannelAnnouncement2(
        ChannelId channelId, ChannelAnnouncement2Payload unsignedAnnouncement, MusigPublicNonce remoteNodeNonce,
        MusigPublicNonce remoteBitcoinNonce)
    {
        ArgumentNullException.ThrowIfNull(unsignedAnnouncement);
        var (signingInfo, funding) = CheckAnnouncement2(channelId, unsignedAnnouncement);
        byte[] message = unsignedAnnouncement.GetSignatureHash();

        // Taken out first: whatever happens next, this pair never signs again
        Announcement2NonceSet? nonces;
        if (_nativeNonceStore is { } store)
        {
            if (_announcement2Nonces.TryRemove(channelId, out var live)) live.Dispose();
            var node = store.TakeLatest("gossip-node", channelId);
            var bitcoin = store.TakeLatest("gossip-bitcoin", channelId);
            if (node is null || bitcoin is null || node.Value.Context is null || bitcoin.Value.Context is null
             || !node.Value.Context.AsSpan().SequenceEqual(bitcoin.Value.Context))
            {
                node?.Pair.SecretNonce.Dispose();
                bitcoin?.Pair.SecretNonce.Dispose();
                throw new SignerException("No live channel_announcement_2 nonce pair in the signer journal", channelId, "Internal error");
            }
            nonces = new Announcement2NonceSet(node.Value.Pair, bitcoin.Value.Pair, node.Value.Context);
        }
        else if (!_announcement2Nonces.TryRemove(channelId, out nonces))
            throw new SignerException("No live channel_announcement_2 nonces for the channel", channelId,
                                      "Internal error");

        using (nonces)
        {
            if (!nonces.Message.AsSpan().SequenceEqual(message))
                throw new SignerException("The channel_announcement_2 nonces were made for another announcement",
                                          channelId, "Internal error");

            var aggregate = GetAnnouncement2Aggregate(unsignedAnnouncement);
            MusigSigningSession session;
            try
            {
                session = _musig2.CreateSession(aggregate,
                                                [
                                                    nonces.Node.PublicNonce, nonces.Bitcoin.PublicNonce,
                                                    remoteNodeNonce, remoteBitcoinNonce
                                                ], message);
            }
            catch (MusigException e)
            {
                throw new SignerException("Invalid channel_announcement_2 nonces", channelId, e,
                                          "Invalid announcement nonces");
            }

            var nodeKey = _secureKeyManager.GetNodeKeyPair();
            var nodePartial = SignAnnouncement2Partial(channelId, nonces.Node, nodeKey.PrivKey.Value.ToArray(),
                                                       nodeKey.CompactPubKey, session);
            MusigPartialSignature bitcoinPartial;
            using (var fundingKey = DeriveTaprootFundingKey(channelId, signingInfo.ChannelKeyIndex, funding))
                bitcoinPartial = SignAnnouncement2Partial(channelId, nonces.Bitcoin, fundingKey.ToBytes(),
                                                          funding.LocalPubKey, session);

            if (_logger.IsEnabled(LogLevel.Information))
                _logger.LogInformation("Signed the channel_announcement_2 of {ShortChannelId} for channel {ChannelId}",
                                       unsignedAnnouncement.ShortChannelId, channelId);

            return new ChannelAnnouncement2PartialSignatures(nodePartial, bitcoinPartial);
        }
    }

    /// <inheritdoc />
    public void DiscardChannelAnnouncement2Nonces(ChannelId channelId)
    {
        _nativeNonceStore?.Forget("gossip-node", channelId);
        _nativeNonceStore?.Forget("gossip-bitcoin", channelId);
        if (_announcement2Nonces.TryRemove(channelId, out var nonces))
            nonces.Dispose();
    }

    /// <inheritdoc />
    public CompactSignature SignNodeMessageBip340(Hash messageHash)
    {
        var privateKey = _secureKeyManager.GetNodeKeyPair().PrivKey.Value.ToArray();
        var auxRandomness = new byte[CryptoConstants.Sha256HashLen];
        try
        {
            using (var cryptoProvider = CryptoFactory.GetCryptoProvider())
                cryptoProvider.RandomBytes(auxRandomness);

            if (!NLightningCryptoContext.Instance.TryCreateECPrivKey(privateKey, out var ecPrivKey)
             || ecPrivKey is null)
                throw new SignerException("The node key is not a valid secp256k1 private key", "Internal error");

            using (ecPrivKey)
            {
                var signature = ecPrivKey.SignBIP340((byte[])messageHash, auxRandomness);
                var bytes = new byte[MusigConstants.SchnorrSignatureLen];
                signature.WriteToSpan(bytes);
                return bytes;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(privateKey);
            CryptographicOperations.ZeroMemory(auxRandomness);
        }
    }

    /// <summary>
    /// The guards of a <c>channel_announcement_2</c> signature: a registered public simple taproot channel without data
    /// loss, and an announcement of exactly this channel (chain, real scid, funding outpoint and capacity, our node id
    /// and the peer's in ascending order, each funding key next to its node, no merkle root: the funding is BIP 86).
    /// With <paramref name="allowPendingSplice"/> (nonces only, NL-1131) it may name a registered pending splice
    /// instead: its outpoint, capacity and funding keys; its short channel id is only checked for the output index (the
    /// splice is not the channel's yet), and signing it waits for the lock, which makes it the current funding.
    /// </summary>
    private (ChannelSigningInfo SigningInfo, FundingKeys Funding) CheckAnnouncement2(
        ChannelId channelId, ChannelAnnouncement2Payload announcement, bool allowPendingSplice = false)
    {
        var signingInfo = GetRegisteredSigningInfo(channelId);
        ThrowIfDataLoss(channelId, "sign a channel_announcement_2");
        if (!signingInfo.AnnounceChannel)
            throw new SignerException("Refusing to sign a channel announcement for a private channel", channelId,
                                      "Internal error");
        if (!signingInfo.IsSimpleTaproot)
            throw new SignerException("Refusing to sign a channel_announcement_2: the channel is not simple taproot",
                                      channelId, "Internal error");

        var funding = FromSigningInfo(signingInfo);
        var pendingSplice = false;
        if (allowPendingSplice && announcement.FundingTxId != funding.TxId)
        {
            lock (GetCommitmentLock(channelId))
            {
                if (_spliceFundings.TryGetValue(channelId, out var state)
                 && state.Fundings.TryGetValue(announcement.FundingTxId, out var splice)
                 && splice.Status == ChannelFundingStatus.Pending)
                {
                    funding = FromFunding(splice);
                    pendingSplice = true;
                }
            }
        }

        string? refusal = null;
        if (announcement.ChainHash != _chainHash)
            refusal = "it is for another chain";
        else if (!pendingSplice && (GetCurrentShortChannelId(channelId, signingInfo) is not { } scid
                                 || scid != announcement.ShortChannelId))
            refusal = $"it names {announcement.ShortChannelId}, not the channel's short channel id";
        else if (announcement.FundingTxId != funding.TxId || announcement.FundingOutputIndex != funding.OutputIndex
              || announcement.ShortChannelId.OutputIndex != funding.OutputIndex)
            refusal = "it names another funding outpoint";
        else if (announcement.CapacitySatoshis != (ulong)funding.Amount.Satoshi)
            refusal = "its capacity is not the funding amount";
        else if (announcement.MerkleRootHash is not null)
            refusal = "it carries a merkle root";
        else if (announcement.BitcoinKey1 is not { } key1 || announcement.BitcoinKey2 is not { } key2)
            refusal = "it does not carry both funding keys";
        else
        {
            var ourNodeId = GetNodePublicKey();
            bool weAreNode1;
            if (announcement.NodeId1 == ourNodeId)
                weAreNode1 = true;
            else if (announcement.NodeId2 == ourNodeId)
                weAreNode1 = false;
            else
                throw new SignerException("Refusing to sign a channel_announcement_2 that does not name our node id",
                                          channelId, "Internal error");

            var (theirNodeId, ourKey, theirKey) = weAreNode1
                                                      ? (announcement.NodeId2, key1, key2)
                                                      : (announcement.NodeId1, key2, key1);
            if (signingInfo.RemoteNodeId is { } remoteNodeId && theirNodeId != remoteNodeId)
                refusal = "it names another peer";
            else if (ourKey != funding.LocalPubKey || theirKey != funding.RemotePubKey)
                refusal = "it does not name the channel's funding keys";
        }

        if (refusal is not null)
            throw new SignerException($"Refusing to sign a channel_announcement_2: {refusal}", channelId,
                                      "Internal error");

        return (signingInfo, funding);
    }

    /// <summary>The untweaked 4-key aggregate of a <c>channel_announcement_2</c>.</summary>
    private MusigKeyAggregate GetAnnouncement2Aggregate(ChannelAnnouncement2Payload announcement) =>
        _musig2.AggregatePubKeys(_musig2.SortPubKeys([
            announcement.NodeId1, announcement.NodeId2, announcement.BitcoinKey1!.Value,
            announcement.BitcoinKey2!.Value
        ]));

    /// <summary>One partial signature (its nonce consumed), self-verified before it leaves the signer.</summary>
    private MusigPartialSignature SignAnnouncement2Partial(ChannelId channelId, MusigNoncePair nonce,
                                                           byte[] privateKey, CompactPubKey publicKey,
                                                           MusigSigningSession session)
    {
        MusigPartialSignature partial;
        try
        {
            partial = _musig2.Sign(nonce.SecretNonce, privateKey, session);
        }
        catch (MusigException e)
        {
            throw new SignerException("MuSig2 signing of the channel_announcement_2 failed", channelId, e,
                                      "Internal error");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(privateKey);
        }

        if (!_musig2.VerifyPartialSignature(partial, nonce.PublicNonce, publicKey, session))
            throw new SignerException("Our channel_announcement_2 partial signature does not verify", channelId,
                                      "Internal error");

        return partial;
    }

    /// <summary>A live nonce pair and the announcement message it was made for.</summary>
    private sealed class Announcement2NonceSet(MusigNoncePair node, MusigNoncePair bitcoin, byte[] message)
        : IDisposable
    {
        public MusigNoncePair Node { get; } = node;

        public MusigNoncePair Bitcoin { get; } = bitcoin;

        public byte[] Message { get; } = message;

        public void Dispose()
        {
            Node.SecretNonce.Dispose();
            Bitcoin.SecretNonce.Dispose();
        }
    }
}