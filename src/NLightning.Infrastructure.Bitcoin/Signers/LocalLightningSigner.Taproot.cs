using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using NBitcoin;

namespace NLightning.Infrastructure.Bitcoin.Signers;

using Crypto.Musig2;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.ValueObjects;
using Domain.Crypto.Interfaces;
using Domain.Crypto.Models;
using Domain.Crypto.ValueObjects;
using Domain.Exceptions;
using Domain.Protocol.Models;
using Taproot;

/// <summary>
/// Simple taproot channels (bolt-simple-taproot.md, plan T3 "Signer" and "Nonces"): MuSig2 commitment signatures over
/// the funding output's key path, the counter-based verification nonces (D-T4), the broadcast of our commitment and the
/// <c>option_simple_close</c> closing signatures with just-in-time nonces.
/// </summary>
/// <remarks>
/// <para><b>Verification nonces</b> are never stored: <c>musig2_shachain_root = HMAC-SHA256(key = "taproot-rev-root"
/// || funding_txid, msg = sha256(shachain_root))</c>, where <c>shachain_root</c> is the channel's per-commitment seed;
/// the nonce of local commitment <c>n</c> is <c>NonceGen(rand' = leaf n of that shachain, pk = our funding key of that
/// funding)</c>, with the leaf index of our per-commitment secrets (<c>2^48-1-n</c>). The funding txid binds every
/// nonce to one funding (a splice signs one number once per funding, NL-904 item 3). Only a v1 open's commitment 0
/// (the channel's original funding, key index 0, not <see cref="ChannelSigningInfo.IsDualFunded"/>) uses the context
/// without a txid: its nonce goes out in <c>open_channel</c>/<c>accept_channel</c> before any txid exists, and a v1
/// open has one funding transaction only (the formula of LND's <c>channeldb.DeriveMusig2Shachain</c>, which binds no
/// txid). Commitment 0 of a splice or of an attempt of a dual-funded open binds its txid (NL-972): those attempts are
/// different transactions, and any of them may be the one we have to broadcast.</para>
/// <para>A verification nonce signs only when we broadcast our commitment. Its secret half is re-derived then, and the
/// signer also refuses a second broadcast signature with the same nonce over another session. That record is memory
/// only (as the S1 mark before it is restored); the txid binding above is what keeps one nonce on one transaction
/// across restarts.</para>
/// <para><b>Signing nonces</b> (our partial signature of the peer's commitment, the closer's nonce) are just in time:
/// fresh randomness, our funding key, the output key and the sighash, used once and never stored. Closee nonces live
/// in the signer-owned nonce journal when configured, and otherwise in memory, until they sign one
/// <c>closing_sig</c> or are forgotten.</para>
/// </remarks>
public partial class LocalLightningSigner
{
    // HMAC key prefix of the MuSig2 shachain root (spec §Counter Based Nonce Generation; LND's taprootRevRootKey)
    private static readonly byte[] s_taprootRevRootKey = "taproot-rev-root"u8.ToArray();

    // Closee nonces kept per channel: one per shutdown or closing_sig, so a few at most; the oldest is dropped beyond
    private const int MaxClosingNoncesPerChannel = 8;

    private readonly IMusig2Service _musig2 = new Musig2Service();

    // Per channel, the session each verification nonce signed for broadcast (key: nonce context and number)
    private readonly ConcurrentDictionary<ChannelId, Dictionary<string, byte[]>> _taprootBroadcastSessions = new();

    // Per channel, the secret halves of our live closee nonces (under the list's own lock)
    private readonly ConcurrentDictionary<ChannelId, List<ClosingNonce>> _closingNonces = new();

    #region Verification nonces

    /// <inheritdoc />
    public MusigPublicNonce GetLocalVerificationNonce(uint channelKeyIndex, TxId? fundingTxId,
                                                      ulong localCommitmentNumber)
    {
        ThrowIfNotCommitmentNumber(localCommitmentNumber);
        if (localCommitmentNumber > 0 && fundingTxId is null)
            throw new SignerException($"The verification nonce of local commitment {localCommitmentNumber} needs the "
                                    + "funding txid", "Internal error");

        // Before a channel exists only the original funding key (index 0) can be meant. The txid is the context as
        // given: null only for a v1 open's commitment 0 (sent before the txid exists), a dual-funded attempt's txid
        // otherwise (NL-972)
        var fundingPubKey = GetFundingPubKey(channelKeyIndex, 0);
        var pair = DeriveVerificationNonce(channelKeyIndex, fundingTxId, localCommitmentNumber, fundingPubKey);
        pair.SecretNonce.Dispose();
        return pair.PublicNonce;
    }

    /// <inheritdoc />
    public MusigPublicNonce GetLocalVerificationNonce(ChannelId channelId, TxId? fundingTxId,
                                                      ulong localCommitmentNumber)
    {
        ThrowIfNotCommitmentNumber(localCommitmentNumber);
        _ = GetRegisteredSigningInfo(channelId);

        FundingKeys funding;
        ChannelSigningInfo signingInfo;
        lock (GetCommitmentLock(channelId))
        {
            signingInfo = GetRegisteredSigningInfo(channelId);
            ThrowIfNotTaproot(channelId, signingInfo, "derive a MuSig2 verification nonce");
            funding = ResolveTaprootFunding(channelId, signingInfo, fundingTxId, activeOnly: true);
        }

        var pair = DeriveVerificationNonce(signingInfo.ChannelKeyIndex,
                                           NonceContext(signingInfo, funding, localCommitmentNumber),
                                           localCommitmentNumber, funding.LocalPubKey);
        pair.SecretNonce.Dispose();
        return pair.PublicNonce;
    }

    #endregion

    #region Commitments

    /// <inheritdoc />
    public MusigPartialSignatureWithNonce SignRemoteCommitmentPartial(ChannelId channelId, TxId? fundingTxId,
                                                                      SignedTransaction unsignedCommitment,
                                                                      MusigPublicNonce remoteVerificationNonce)
    {
        CheckSignFence();

        ArgumentNullException.ThrowIfNull(unsignedCommitment);
        var registered = GetRegisteredSigningInfo(channelId);
        var remoteNumber = GetCommitmentNumber(registered, unsignedCommitment);
        RefreshDurableGuard(channelId);

        MusigPartialSignatureWithNonce partial;
        lock (GetCommitmentLock(channelId))
        {
            var signingInfo = GetRegisteredSigningInfo(channelId);
            ThrowIfNotTaproot(channelId, signingInfo, "sign a commitment with MuSig2");
            ThrowIfDataLoss(channelId, "sign a commitment");
            ThrowIfBroadcastSigned(channelId, "sign a channel transaction");

            var funding = ResolveTaprootFunding(channelId, signingInfo, fundingTxId, activeOnly: true);
            var tx = LoadTransaction(channelId, unsignedCommitment, "commitment");
            ThrowIfNotSpendingFunding(channelId, tx, funding);

            // The peer's commitment is never signed below one already signed, on any funding (NL-1345)
            if (remoteNumber is { } number)
                CheckAndMarkRemoteCommitment(channelId, number);

            partial = SignJustInTime(channelId, signingInfo.ChannelKeyIndex, funding, unsignedCommitment,
                                     remoteVerificationNonce, "the peer's verification nonce");
        }

        PersistRemoteCommitmentGuard(channelId, remoteNumber);
        return partial;
    }

    /// <inheritdoc />
    public void ValidateLocalCommitmentPartialSignature(ChannelId channelId, TxId? fundingTxId,
                                                        ulong localCommitmentNumber,
                                                        MusigPartialSignatureWithNonce remoteSignature,
                                                        SignedTransaction unsignedCommitment)
    {
        ArgumentNullException.ThrowIfNull(unsignedCommitment);
        ThrowIfNotCommitmentNumber(localCommitmentNumber);
        _ = GetRegisteredSigningInfo(channelId);

        FundingKeys funding;
        ChannelSigningInfo signingInfo;
        lock (GetCommitmentLock(channelId))
        {
            signingInfo = GetRegisteredSigningInfo(channelId);
            ThrowIfNotTaproot(channelId, signingInfo, "check a MuSig2 commitment signature");
            funding = ResolveTaprootFunding(channelId, signingInfo, fundingTxId, activeOnly: false);
        }

        var tx = LoadTransaction(channelId, unsignedCommitment, "commitment");
        ThrowIfNotSpendingFunding(channelId, tx, funding);

        var pair = DeriveVerificationNonce(signingInfo.ChannelKeyIndex,
                                           NonceContext(signingInfo, funding, localCommitmentNumber),
                                           localCommitmentNumber, funding.LocalPubKey);
        pair.SecretNonce.Dispose();

        var (aggregate, sigHash) = GetFundingSession(funding, unsignedCommitment);
        VerifyRemotePartial(channelId, aggregate, sigHash, funding.RemotePubKey, remoteSignature.PartialSignature,
                            remoteSignature.PublicNonce, pair.PublicNonce, "partial_signature_with_nonce");
    }

    /// <inheritdoc />
    public SignedTransaction SignLocalCommitmentForBroadcast(ChannelId channelId, TxId? fundingTxId,
                                                             ulong commitmentNumber,
                                                             SignedTransaction unsignedCommitment,
                                                             MusigPartialSignatureWithNonce remoteSignature)
    {
        CheckSignFence();

        ArgumentNullException.ThrowIfNull(unsignedCommitment);
        ThrowIfNotCommitmentNumber(commitmentNumber);

        // A channel that is not registered is loaded (a database read) before the lock is taken, not while holding it,
        // and so is the durable guard (NL-1345)
        _ = GetRegisteredSigningInfo(channelId);
        RefreshDurableGuard(channelId);

        // The same lock as the ECDSA broadcast, AdvanceLocalCommitment and RevealPerCommitmentSecret (S1, SP-I4)
        SignedTransaction signed;
        lock (GetCommitmentLock(channelId))
        {
            var signingInfo = GetRegisteredSigningInfo(channelId);
            ThrowIfNotTaproot(channelId, signingInfo, "sign our commitment for broadcast with MuSig2");
            var funding = ResolveTaprootFunding(channelId, signingInfo, fundingTxId, activeOnly: true);
            var tx = LoadTransaction(channelId, unsignedCommitment, "commitment");
            ThrowIfNotSpendingFunding(channelId, tx, funding);
            ThrowIfCannotSignForBroadcast(channelId, commitmentNumber);

            var context = NonceContext(signingInfo, funding, commitmentNumber);
            var (aggregate, sigHash) = GetFundingSession(funding, unsignedCommitment);
            var ourNonce = DeriveVerificationNonce(signingInfo.ChannelKeyIndex, context, commitmentNumber,
                                                   funding.LocalPubKey);
            try
            {
                // The peer's partial signature must be valid for exactly this transaction, or the broadcast fails
                var session = CreateSession(channelId, aggregate, sigHash, ourNonce.PublicNonce,
                                            remoteSignature.PublicNonce, "partial_signature_with_nonce");
                VerifyRemotePartial(channelId, session, funding.RemotePubKey, remoteSignature.PartialSignature,
                                    remoteSignature.PublicNonce, "partial_signature_with_nonce");

                // One verification nonce never signs two sessions: the same commitment again (a rebroadcast) gives
                // the same partial signature, anything else would reveal our funding key
                RecordBroadcastSession(channelId, context, commitmentNumber, session);

                var ourPartial = SignAndCheck(channelId, signingInfo.ChannelKeyIndex, funding, ourNonce, session);
                var signature = AggregateAndCheck(channelId, aggregate, session,
                                                  [ourPartial, remoteSignature.PartialSignature], sigHash);

                // S1: record the broadcast signature before it leaves the signer (durable after the lock, NL-1345)
                MarkBroadcastSignedInMemory(channelId, commitmentNumber);

                // BIP 341 key-path witness: the 64-byte signature alone (SIGHASH_DEFAULT, no sighash byte)
                tx.Inputs[0].WitScript = new WitScript([signature]);
                if (_logger.IsEnabled(LogLevel.Information))
                    _logger.LogInformation("Signed simple taproot local commitment {CommitmentNumber} ({TxId}) of "
                                         + "channel {ChannelId} on funding {FundingTxId} for broadcast",
                                           commitmentNumber, tx.GetHash(), channelId, funding.TxId);

                signed = new SignedTransaction(tx.GetHash().ToBytes(), tx.ToBytes());
            }
            finally
            {
                ourNonce.SecretNonce.Dispose();
            }
        }

        PersistBroadcastGuard(channelId, commitmentNumber);
        return signed;
    }

    #endregion

    #region Cooperative close (option_simple_close)

    /// <inheritdoc />
    public MusigPublicNonce CreateClosingNonce(ChannelId channelId)
    {
        var (signingInfo, funding) = GetClosingFunding(channelId, "create a closing nonce");

        var aggregate = _musig2.AggregateTaprootKeyPath(funding.LocalPubKey, funding.RemotePubKey);
        MusigNoncePair pair;
        using (var fundingKey = DeriveTaprootFundingKey(channelId, signingInfo.ChannelKeyIndex, funding))
        {
            var privateKey = fundingKey.ToBytes();
            try
            {
                pair = _musig2.GenerateNonce(funding.LocalPubKey, privateKey, aggregate.XOnlyOutputKey);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(privateKey);
            }
        }

        if (_nativeNonceStore is { } store) pair = store.Store("close", channelId, pair);
        var nonces = _closingNonces.GetOrAdd(channelId, static _ => []);
        lock (nonces)
        {
            if (nonces.Count >= MaxClosingNoncesPerChannel)
            {
                _nativeNonceStore?.Forget("close", channelId, nonces[0].PublicNonce);
                nonces[0].SecretNonce.Dispose();
                nonces.RemoveAt(0);
            }

            nonces.Add(new ClosingNonce(pair.PublicNonce, pair.SecretNonce));
        }

        return pair.PublicNonce;
    }

    /// <inheritdoc />
    public MusigPartialSignatureWithNonce SignClosingAsCloser(ChannelId channelId, SignedTransaction unsignedClosing,
                                                              MusigPublicNonce remoteCloseeNonce)
    {
        CheckSignFence();

        ArgumentNullException.ThrowIfNull(unsignedClosing);
        var (signingInfo, funding) = GetClosingFunding(channelId, "sign a closing transaction");
        ThrowIfNotSpendingFunding(channelId, LoadTransaction(channelId, unsignedClosing, "closing"), funding);

        return SignJustInTime(channelId, signingInfo.ChannelKeyIndex, funding, unsignedClosing, remoteCloseeNonce,
                              "the peer's closee nonce");
    }

    /// <inheritdoc />
    public MusigPartialSignature SignClosingAsClosee(ChannelId channelId, SignedTransaction unsignedClosing,
                                                     MusigPublicNonce localCloseeNonce,
                                                     MusigPartialSignatureWithNonce remoteCloserSignature)
    {
        CheckSignFence();

        ArgumentNullException.ThrowIfNull(unsignedClosing);
        var (signingInfo, funding) = GetClosingFunding(channelId, "sign a closing transaction");
        ThrowIfNotSpendingFunding(channelId, LoadTransaction(channelId, unsignedClosing, "closing"), funding);

        var (aggregate, sigHash) = GetFundingSession(funding, unsignedClosing);
        var session = CreateSession(channelId, aggregate, sigHash, localCloseeNonce,
                                    remoteCloserSignature.PublicNonce, "closer nonce");

        // The closer's signature first: an invalid closing_complete must not cost us the closee nonce
        VerifyRemotePartial(channelId, session, funding.RemotePubKey, remoteCloserSignature.PartialSignature,
                            remoteCloserSignature.PublicNonce, "closing_complete partial signature");

        var secretNonce = TakeClosingNonce(channelId, localCloseeNonce)
                       ?? throw new SignerException("The closee nonce is not a live closing nonce of ours", channelId,
                                                    "Internal error");
        using (secretNonce)
            return SignAndCheck(channelId, signingInfo.ChannelKeyIndex, funding,
                                new MusigNoncePair(secretNonce, localCloseeNonce), session);
    }

    /// <inheritdoc />
    public void ValidateClosingPartialSignature(ChannelId channelId, SignedTransaction unsignedClosing,
                                                MusigPartialSignature remoteSignature, MusigPublicNonce remoteNonce,
                                                MusigPublicNonce localNonce)
    {
        ArgumentNullException.ThrowIfNull(unsignedClosing);
        var funding = GetCurrentTaprootFunding(channelId, "check a closing signature");
        ThrowIfNotSpendingFunding(channelId, LoadTransaction(channelId, unsignedClosing, "closing"), funding);

        var (aggregate, sigHash) = GetFundingSession(funding, unsignedClosing);
        VerifyRemotePartial(channelId, aggregate, sigHash, funding.RemotePubKey, remoteSignature, remoteNonce,
                            localNonce, "closing partial signature");
    }

    /// <inheritdoc />
    public SignedTransaction AggregateClosingSignature(ChannelId channelId, SignedTransaction unsignedClosing,
                                                       MusigPartialSignature localSignature,
                                                       MusigPublicNonce localNonce,
                                                       MusigPartialSignature remoteSignature,
                                                       MusigPublicNonce remoteNonce)
    {
        CheckSignFence();

        ArgumentNullException.ThrowIfNull(unsignedClosing);
        var funding = GetCurrentTaprootFunding(channelId, "aggregate a closing signature");
        var tx = LoadTransaction(channelId, unsignedClosing, "closing");
        ThrowIfNotSpendingFunding(channelId, tx, funding);

        var (aggregate, sigHash) = GetFundingSession(funding, unsignedClosing);
        var session = CreateSession(channelId, aggregate, sigHash, localNonce, remoteNonce, "closing nonce");
        VerifyRemotePartial(channelId, session, funding.RemotePubKey, remoteSignature, remoteNonce,
                            "closing partial signature");
        if (!_musig2.VerifyPartialSignature(localSignature, localNonce, funding.LocalPubKey, session))
            throw new SignerException("Our closing partial signature does not verify in this session", channelId,
                                      "Internal error");

        var signature = AggregateAndCheck(channelId, aggregate, session, [localSignature, remoteSignature], sigHash);
        tx.Inputs[0].WitScript = new WitScript([signature]);
        return new SignedTransaction(tx.GetHash().ToBytes(), tx.ToBytes());
    }

    /// <inheritdoc />
    public void ForgetClosingNonces(ChannelId channelId)
    {
        _nativeNonceStore?.Forget("close", channelId);
        if (!_closingNonces.TryRemove(channelId, out var nonces))
            return;

        lock (nonces)
        {
            foreach (var nonce in nonces)
                nonce.SecretNonce.Dispose();
            nonces.Clear();
        }
    }

    #endregion

    #region Helpers

    /// <summary>
    /// The channel's MuSig2 shachain context for <paramref name="funding"/>: its txid, except for commitment 0 of a v1
    /// open's original funding, whose nonce is sent in <c>open_channel</c>/<c>accept_channel</c> before any funding
    /// txid exists. A splice (a rotated funding key) and every attempt of a dual-funded open bind their txid even for
    /// commitment 0, so two of them never share a nonce (NL-972).
    /// </summary>
    private static TxId? NonceContext(ChannelSigningInfo signingInfo, FundingKeys funding, ulong commitmentNumber) =>
        commitmentNumber == 0 && funding.KeyIndex == 0 && !signingInfo.IsDualFunded ? (TxId?)null : funding.TxId;

    /// <summary>
    /// The verification nonce pair of local commitment <paramref name="commitmentNumber"/> in
    /// <paramref name="contextTxId"/>'s MuSig2 shachain (see the class remarks). The caller disposes the secret half.
    /// </summary>
    private MusigNoncePair DeriveVerificationNonce(uint channelKeyIndex, TxId? contextTxId, ulong commitmentNumber,
                                                   CompactPubKey fundingPubKey)
    {
        var seed = DerivePerCommitmentSeed(channelKeyIndex);
        var rootHash = SHA256.HashData(seed);
        var hmacKey = contextTxId is { } txId ? [.. s_taprootRevRootKey, .. (byte[])txId] : s_taprootRevRootKey;
        var musigRoot = HMACSHA256.HashData(hmacKey, rootHash);
        byte[]? leaf = null;
        try
        {
            leaf = _keyDerivationService.GeneratePerCommitmentSecret(musigRoot, PerCommitmentIndex.From(commitmentNumber));
            return _musig2.GenerateNonce(leaf, fundingPubKey);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(seed);
            CryptographicOperations.ZeroMemory(rootHash);
            CryptographicOperations.ZeroMemory(musigRoot);
            if (leaf is not null)
                CryptographicOperations.ZeroMemory(leaf);
        }
    }

    /// <summary>The channel's per-commitment seed (m/5' of the channel key), the shachain root of BOLT 3.</summary>
    private byte[] DerivePerCommitmentSeed(uint channelKeyIndex)
    {
        using var seed = DeriveChannelChildKey(channelKeyIndex, [PerCommitmentSeedDerivationIndex]);
        return seed.ToBytes();
    }

    /// <summary>
    /// The private key at the hardened <paramref name="path"/> below the channel key, with the extended key bytes the
    /// key manager handed out zeroed and every intermediate private key disposed on every path (NL-911). The caller
    /// disposes the result.
    /// </summary>
    private Key DeriveChannelChildKey(uint channelKeyIndex, ReadOnlySpan<int> path)
    {
        var extKeyBytes = _secureKeyManager.GetChannelKeyAtIndex(channelKeyIndex).Value;
        ExtKey? current = null;
        try
        {
            current = ExtKey.CreateFromBytes(extKeyBytes);
            foreach (var index in path)
            {
                var next = current.Derive(index, true);
                current.PrivateKey.Dispose();
                current = next;
            }

            var result = current.PrivateKey;
            current = null;
            return result;
        }
        finally
        {
            current?.PrivateKey.Dispose();
            CryptographicOperations.ZeroMemory(extKeyBytes);
        }
    }

    /// <summary>
    /// Our partial signature of <paramref name="unsignedTransaction"/> (key-path spend of <paramref name="funding"/>)
    /// with a fresh just-in-time nonce mixed with our funding key, the output key and the sighash, against the peer's
    /// <paramref name="remoteNonce"/>; self-verified. The secret nonce is used once and never leaves this method.
    /// </summary>
    private MusigPartialSignatureWithNonce SignJustInTime(ChannelId channelId, uint channelKeyIndex,
                                                          FundingKeys funding, SignedTransaction unsignedTransaction,
                                                          MusigPublicNonce remoteNonce, string remoteNonceName)
    {
        var (aggregate, sigHash) = GetFundingSession(funding, unsignedTransaction);

        MusigNoncePair pair;
        using (var fundingKey = DeriveTaprootFundingKey(channelId, channelKeyIndex, funding))
        {
            var privateKey = fundingKey.ToBytes();
            try
            {
                pair = _musig2.GenerateNonce(funding.LocalPubKey, privateKey, aggregate.XOnlyOutputKey, sigHash);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(privateKey);
            }
        }

        using (pair.SecretNonce)
        {
            var session = CreateSession(channelId, aggregate, sigHash, pair.PublicNonce, remoteNonce,
                                        remoteNonceName);
            var partial = SignAndCheck(channelId, channelKeyIndex, funding, pair, session);
            return new MusigPartialSignatureWithNonce(partial, pair.PublicNonce);
        }
    }

    /// <summary>
    /// Signs <paramref name="session"/> with our funding key of <paramref name="funding"/> and
    /// <paramref name="nonce"/>'s secret half (consumed), then verifies our own partial signature before it leaves the
    /// signer (NL-904 item 7).
    /// </summary>
    private MusigPartialSignature SignAndCheck(ChannelId channelId, uint channelKeyIndex, FundingKeys funding,
                                               MusigNoncePair nonce, MusigSigningSession session)
    {
        MusigPartialSignature partial;
        using (var fundingKey = DeriveTaprootFundingKey(channelId, channelKeyIndex, funding))
        {
            var privateKey = fundingKey.ToBytes();
            try
            {
                partial = _musig2.Sign(nonce.SecretNonce, privateKey, session);
            }
            catch (MusigException e)
            {
                throw new SignerException("MuSig2 signing failed", channelId, e, "Internal error");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(privateKey);
            }
        }

        if (!_musig2.VerifyPartialSignature(partial, nonce.PublicNonce, funding.LocalPubKey, session))
            throw new SignerException("Our MuSig2 partial signature does not verify", channelId, "Internal error");

        return partial;
    }

    /// <summary>
    /// PartialSigAgg of both partial signatures, then a BIP 340 check of the result against the funding output key
    /// (self-verification: a faulty signature never leaves the signer).
    /// </summary>
    private byte[] AggregateAndCheck(ChannelId channelId, MusigKeyAggregate aggregate, MusigSigningSession session,
                                     IReadOnlyList<MusigPartialSignature> partials, byte[] sigHash)
    {
        byte[] signature;
        try
        {
            signature = _musig2.AggregatePartialSignatures(partials, session);
        }
        catch (MusigException e)
        {
            throw new SignerException("Failed to aggregate the MuSig2 partial signatures", channelId, e,
                                      "Internal error");
        }

        if (!_musig2.VerifySignature(signature, aggregate.XOnlyOutputKey, sigHash))
            throw new SignerException("The aggregated MuSig2 signature does not verify against the funding output key",
                                      channelId, "Internal error");

        return signature;
    }

    /// <summary>The MuSig2 key aggregate of the funding and the key-path sighash of the transaction spending it.</summary>
    private (MusigKeyAggregate Aggregate, byte[] SigHash) GetFundingSession(FundingKeys funding,
                                                                           SignedTransaction unsignedTransaction)
    {
        var aggregate = _musig2.AggregateTaprootKeyPath(funding.LocalPubKey, funding.RemotePubKey);
        var sigHash = TaprootSignatures.ComputeFundingKeySpendSigHash(unsignedTransaction, funding.Amount,
                                                                      aggregate.GetTaprootScriptPubKey());
        return (aggregate, sigHash);
    }

    /// <summary>A session bound to both public nonces; an undecodable nonce is a <see cref="SignerException"/>.</summary>
    private MusigSigningSession CreateSession(ChannelId channelId, MusigKeyAggregate aggregate, byte[] sigHash,
                                              MusigPublicNonce localNonce, MusigPublicNonce remoteNonce,
                                              string remoteNonceName)
    {
        if ((byte[])localNonce is null || (byte[])remoteNonce is null)
            throw new SignerException("A MuSig2 nonce is missing", channelId, "Internal error");

        try
        {
            return _musig2.CreateSession(aggregate, [localNonce, remoteNonce], sigHash);
        }
        catch (MusigException e)
        {
            throw new SignerException($"Invalid MuSig2 nonce ({remoteNonceName})", channelId, e,
                                      $"Invalid {remoteNonceName}");
        }
    }

    private void VerifyRemotePartial(ChannelId channelId, MusigKeyAggregate aggregate, byte[] sigHash,
                                     CompactPubKey remotePubKey, MusigPartialSignature remoteSignature,
                                     MusigPublicNonce remoteNonce, MusigPublicNonce localNonce, string what)
    {
        var session = CreateSession(channelId, aggregate, sigHash, localNonce, remoteNonce, what);
        VerifyRemotePartial(channelId, session, remotePubKey, remoteSignature, remoteNonce, what);
    }

    private void VerifyRemotePartial(ChannelId channelId, MusigSigningSession session, CompactPubKey remotePubKey,
                                     MusigPartialSignature remoteSignature, MusigPublicNonce remoteNonce, string what)
    {
        bool valid;
        try
        {
            valid = (byte[])remoteSignature is not null
                 && _musig2.VerifyPartialSignature(remoteSignature, remoteNonce, remotePubKey, session);
        }
        catch (MusigException e)
        {
            throw new SignerException($"Invalid {what}", channelId, e, $"Invalid {what}");
        }

        if (!valid)
            throw new SignerException($"The peer's {what} is invalid", channelId, $"Invalid {what}");
    }

    /// <summary>
    /// Records that the verification nonce of <paramref name="context"/>/<paramref name="commitmentNumber"/> signs
    /// <paramref name="session"/> for broadcast; refuses another session with that nonce. Under the commitment lock.
    /// </summary>
    private void RecordBroadcastSession(ChannelId channelId, TxId? context, ulong commitmentNumber,
                                        MusigSigningSession session)
    {
        var key = $"{(context is { } txId ? txId.ToString() : "-")}:{commitmentNumber}";
        var digest = SHA256.HashData([.. session.Message.Span, .. (byte[])session.AggregateNonce]);
        var sessions = _taprootBroadcastSessions.GetOrAdd(channelId, static _ => new Dictionary<string, byte[]>());
        if (sessions.TryGetValue(key, out var known))
        {
            if (!known.AsSpan().SequenceEqual(digest))
                throw new SignerException(
                    $"Refusing to sign local commitment {commitmentNumber} for broadcast again over another transaction "
                  + "or nonce: its verification nonce already signed (nonce reuse would reveal the funding key)",
                    channelId, "Internal error");
            return;
        }

        sessions[key] = digest;
    }

    /// <summary>Takes (removes) the secret half of our closee nonce <paramref name="publicNonce"/>; null if unknown.</summary>
    private MusigSecretNonce? TakeClosingNonce(ChannelId channelId, MusigPublicNonce publicNonce)
    {
        if (_nativeNonceStore is { } store)
        {
            var restored = store.Take("close", channelId, publicNonce);
            if (_closingNonces.TryGetValue(channelId, out var live))
                lock (live)
                {
                    var index = live.FindIndex(n => n.PublicNonce == publicNonce);
                    if (index >= 0) { live[index].SecretNonce.Dispose(); live.RemoveAt(index); }
                }
            return restored;
        }
        if (!_closingNonces.TryGetValue(channelId, out var nonces))
            return null;

        lock (nonces)
        {
            var index = nonces.FindIndex(n => n.PublicNonce == publicNonce);
            if (index < 0)
                return null;

            var secretNonce = nonces[index].SecretNonce;
            nonces.RemoveAt(index);
            return secretNonce.IsUsed ? null : secretNonce;
        }
    }

    /// <summary>
    /// The registered taproot channel and its current funding, with the guards of the ECDSA closing signature
    /// (<see cref="SignChannelTransaction(ChannelId, SignedTransaction)"/>): data loss (I12) and S1.
    /// </summary>
    private (ChannelSigningInfo SigningInfo, FundingKeys Funding) GetClosingFunding(ChannelId channelId, string what)
    {
        var signingInfo = GetRegisteredSigningInfo(channelId);
        ThrowIfNotTaproot(channelId, signingInfo, what);
        ThrowIfDataLoss(channelId, what);
        ThrowIfBroadcastSigned(channelId, what);
        return (signingInfo, FromSigningInfo(signingInfo));
    }

    private FundingKeys GetCurrentTaprootFunding(ChannelId channelId, string what)
    {
        var signingInfo = GetRegisteredSigningInfo(channelId);
        ThrowIfNotTaproot(channelId, signingInfo, what);
        return FromSigningInfo(signingInfo);
    }

    /// <summary>
    /// The funding <paramref name="fundingTxId"/> (null: the current one) of a taproot channel; call it under the
    /// commitment lock.
    /// </summary>
    private FundingKeys ResolveTaprootFunding(ChannelId channelId, ChannelSigningInfo signingInfo, TxId? fundingTxId,
                                              bool activeOnly) =>
        fundingTxId is { } txId
            ? ResolveFunding(channelId, signingInfo, txId, activeOnly)
            : FromSigningInfo(signingInfo);

    /// <summary>Our funding private key of <paramref name="funding"/>, checked against its local funding key.</summary>
    private Key DeriveTaprootFundingKey(ChannelId channelId, uint channelKeyIndex, FundingKeys funding)
    {
        var key = GenerateFundingPrivateKey(channelKeyIndex, funding.KeyIndex);
        if (!key.PubKey.ToBytes().AsSpan().SequenceEqual((byte[])funding.LocalPubKey))
        {
            key.Dispose();
            throw new SignerException($"The derived funding key {funding.KeyIndex} does not match the local funding "
                                    + $"key of {funding.TxId}", channelId, "Internal error");
        }

        return key;
    }

    /// <summary>The simple taproot funding output: the BIP 86 key path of <c>KeyAgg(KeySort(both funding keys))</c>.</summary>
    private TxOut BuildTaprootFundingTxOut(FundingKeys funding)
    {
        var aggregate = _musig2.AggregateTaprootKeyPath(funding.LocalPubKey, funding.RemotePubKey);
        return new TxOut(Money.Satoshis(funding.Amount.Satoshi), new Script(aggregate.GetTaprootScriptPubKey()));
    }

    private static void ThrowIfNotTaproot(ChannelId channelId, ChannelSigningInfo signingInfo, string what)
    {
        if (!signingInfo.IsSimpleTaproot)
            throw new SignerException($"Refusing to {what}: the channel is not a simple taproot channel", channelId,
                                      "Internal error");
    }

    private static void ThrowIfTaproot(ChannelId channelId, ChannelSigningInfo signingInfo, string what)
    {
        if (signingInfo.IsSimpleTaproot)
            throw new SignerException($"Refusing to {what}: the channel is a simple taproot channel, whose funding "
                                    + "output is spent by MuSig2 key path only", channelId, "Internal error");
    }

    private static void ThrowIfNotCommitmentNumber(ulong commitmentNumber)
    {
        if (commitmentNumber > CommitmentNumber.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(commitmentNumber), commitmentNumber,
                                                  "Commitment numbers are 48-bit values");
    }

    /// <summary>One of our live closee nonces: the public half and the single-use secret half.</summary>
    private sealed record ClosingNonce(MusigPublicNonce PublicNonce, MusigSecretNonce SecretNonce);

    #endregion
}