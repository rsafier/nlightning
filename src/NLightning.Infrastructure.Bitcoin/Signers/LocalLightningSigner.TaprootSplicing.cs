using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using NBitcoin;

namespace NLightning.Infrastructure.Bitcoin.Signers;

using Domain.Bitcoin.ValueObjects;
using Domain.Bitcoin.Wallet.Models;
using Domain.Channels.Splicing.Enums;
using Domain.Channels.ValueObjects;
using Domain.Crypto.Models;
using Domain.Crypto.ValueObjects;
using Domain.Exceptions;

/// <summary>
/// Splicing a simple taproot channel (NL-965, plan T5 "Splicing", BOLTs PR #1324 as Eclair 0.14.3 speaks it): the
/// verification nonces of a splice funding before it is registered (<c>tx_complete</c> <c>commit_nonces</c>), the
/// signing nonce of the shared input (<c>funding_nonce</c>) and the MuSig2 key-path signature of the shared input
/// (<c>tx_signatures</c> <c>shared_input_partial_signature</c>).
/// </summary>
/// <remarks>
/// <para>The shared input's signing nonce is just in time and single use: its secret half lives in memory from the
/// <c>tx_complete</c> that sent it until the commitment step signs with it, and is never stored (D-T4). The partial
/// signature is what the caller stores, in the save before our <c>commitment_signed</c>, so our <c>tx_signatures</c>
/// survives a restart without the nonce.</para>
/// <para>A taproot splice always rotates its funding key (<c>m/0'/i'</c>, <c>i &gt;= 1</c>): the verification nonces of
/// key 0 on a v1-opened channel have a txid-free commitment 0 (NL-972), which a splice must never share.</para>
/// </remarks>
public partial class LocalLightningSigner
{
    // Shared-input signing nonces kept per channel: one per splice attempt, so a few at most; the oldest is dropped
    private const int MaxSpliceFundingNoncesPerChannel = 8;

    // Per channel, the secret halves of our live shared-input signing nonces (under the list's own lock)
    private readonly ConcurrentDictionary<ChannelId, List<ClosingNonce>> _spliceFundingNonces = new();

    /// <inheritdoc />
    public MusigPublicNonce GetLocalVerificationNonce(ChannelId channelId, uint fundingKeyIndex, TxId fundingTxId,
                                                      ulong localCommitmentNumber)
    {
        ThrowIfNotCommitmentNumber(localCommitmentNumber);
        var signingInfo = GetRegisteredSigningInfo(channelId);
        ThrowIfNotTaproot(channelId, signingInfo, "derive a MuSig2 verification nonce for a splice funding");
        if (fundingKeyIndex == 0)
            throw new SignerException("A simple taproot splice funding uses a rotated funding key, never key 0",
                                      channelId, "Internal error");

        // The same derivation as the registered funding's (NonceContext binds the txid of a rotated key)
        var fundingPubKey = GetFundingPubKey(signingInfo.ChannelKeyIndex, fundingKeyIndex);
        var pair = DeriveVerificationNonce(signingInfo.ChannelKeyIndex, fundingTxId, localCommitmentNumber,
                                           fundingPubKey);
        pair.SecretNonce.Dispose();
        return pair.PublicNonce;
    }

    /// <inheritdoc />
    public MusigPublicNonce CreateSpliceFundingNonce(ChannelId channelId)
    {
        var (signingInfo, current) = GetClosingFunding(channelId, "create a splice funding nonce");
        var aggregate = _musig2.AggregateTaprootKeyPath(current.LocalPubKey, current.RemotePubKey);
        MusigNoncePair pair;
        using (var fundingKey = DeriveTaprootFundingKey(channelId, signingInfo.ChannelKeyIndex, current))
        {
            var privateKey = fundingKey.ToBytes();
            try
            {
                pair = _musig2.GenerateNonce(current.LocalPubKey, privateKey, aggregate.XOnlyOutputKey);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(privateKey);
            }
        }

        if (_nativeNonceStore is { } store) pair = store.Store("splice", channelId, pair);
        var nonces = _spliceFundingNonces.GetOrAdd(channelId, static _ => []);
        lock (nonces)
        {
            if (nonces.Count >= MaxSpliceFundingNoncesPerChannel)
            {
                _nativeNonceStore?.Forget("splice", channelId, nonces[0].PublicNonce);
                nonces[0].SecretNonce.Dispose();
                nonces.RemoveAt(0);
            }

            nonces.Add(new ClosingNonce(pair.PublicNonce, pair.SecretNonce));
        }

        return pair.PublicNonce;
    }

    /// <inheritdoc />
    public MusigPartialSignatureWithNonce SignSpliceSharedInputPartial(ChannelId channelId, TxId newFundingTxId,
                                                                       SignedTransaction unsignedSpliceTransaction,
                                                                       int sharedInputIndex,
                                                                       IReadOnlyList<SpentOutput> spentOutputs,
                                                                       MusigPublicNonce localFundingNonce,
                                                                       MusigPublicNonce remoteFundingNonce)
    {
        ArgumentNullException.ThrowIfNull(unsignedSpliceTransaction);
        ArgumentNullException.ThrowIfNull(spentOutputs);

        // A channel that is not registered is loaded (a database read) before the lock is taken
        _ = GetRegisteredSigningInfo(channelId);

        lock (GetCommitmentLock(channelId))
        {
            var signingInfo = GetRegisteredSigningInfo(channelId);
            const string what = "sign a taproot splice's shared input";
            ThrowIfNotTaproot(channelId, signingInfo, what);
            ThrowIfDataLoss(channelId, what);
            ThrowIfBroadcastSigned(channelId, what);

            var state = GetSpliceState(channelId);
            if (!state.Fundings.TryGetValue(newFundingTxId, out var newFunding)
             || newFunding.Status != ChannelFundingStatus.Pending)
                throw new SignerException($"Refusing to sign the shared input: {newFundingTxId} is not a registered "
                                        + "pending splice", channelId, "Internal error");

            var tx = LoadTransaction(channelId, unsignedSpliceTransaction, "splice");
            if (new TxId(tx.GetHash().ToBytes()) != newFundingTxId)
                throw new SignerException(
                    $"Refusing to sign the shared input: the transaction is {tx.GetHash()}, not splice {newFundingTxId}",
                    channelId, "Internal error");

            // The splice must create exactly the registered funding output, or the commitment we hold spends nothing
            if (newFunding.OutputIndex >= tx.Outputs.Count)
                throw new SignerException($"The splice transaction has no output {newFunding.OutputIndex}", channelId,
                                          "Internal error");
            var expected = BuildTaprootFundingTxOut(FromFunding(newFunding));
            var actual = tx.Outputs[newFunding.OutputIndex];
            if (actual.ScriptPubKey != expected.ScriptPubKey || actual.Value != expected.Value)
                throw new SignerException("Refusing to sign the shared input: the splice's funding output is not the "
                                        + "registered one", channelId, "Internal error");

            var current = FromSigningInfo(signingInfo);
            ThrowIfNotSharedInput(channelId, tx, sharedInputIndex, current);
            var (aggregate, sigHash) = GetSharedInputSession(channelId, tx, sharedInputIndex, current, spentOutputs);
            var session = CreateSession(channelId, aggregate, sigHash, localFundingNonce, remoteFundingNonce,
                                        "funding_nonce");

            // Taken only now: a refused transaction keeps the nonce for the signature the negotiation may still make
            var secretNonce = TakeSpliceFundingNonce(channelId, localFundingNonce)
                           ?? throw new SignerException("The funding nonce is not a live splice nonce of ours",
                                                        channelId, "Internal error");
            MusigPartialSignature partial;
            using (secretNonce)
                partial = SignAndCheck(channelId, signingInfo.ChannelKeyIndex, current,
                                       new MusigNoncePair(secretNonce, localFundingNonce), session);

            if (_logger.IsEnabled(LogLevel.Information))
                _logger.LogInformation("Signed the shared taproot input of splice {FundingTxId} of channel {ChannelId}",
                                       newFundingTxId, channelId);

            return new MusigPartialSignatureWithNonce(partial, localFundingNonce);
        }
    }

    /// <inheritdoc />
    public byte[] AggregateSpliceSharedInputSignature(ChannelId channelId, SignedTransaction unsignedSpliceTransaction,
                                                      int sharedInputIndex, IReadOnlyList<SpentOutput> spentOutputs,
                                                      MusigPartialSignatureWithNonce localSignature,
                                                      MusigPartialSignatureWithNonce remoteSignature)
    {
        ArgumentNullException.ThrowIfNull(unsignedSpliceTransaction);
        ArgumentNullException.ThrowIfNull(spentOutputs);
        var current = GetCurrentTaprootFunding(channelId, "aggregate a taproot splice's shared input signature");
        var tx = LoadTransaction(channelId, unsignedSpliceTransaction, "splice");
        ThrowIfNotSharedInput(channelId, tx, sharedInputIndex, current);

        var (aggregate, sigHash) = GetSharedInputSession(channelId, tx, sharedInputIndex, current, spentOutputs);
        var session = CreateSession(channelId, aggregate, sigHash, localSignature.PublicNonce,
                                    remoteSignature.PublicNonce, "shared_input_partial_signature nonce");
        VerifyRemotePartial(channelId, session, current.RemotePubKey, remoteSignature.PartialSignature,
                            remoteSignature.PublicNonce, "shared_input_partial_signature");
        if (!_musig2.VerifyPartialSignature(localSignature.PartialSignature, localSignature.PublicNonce,
                                            current.LocalPubKey, session))
            throw new SignerException("Our shared_input_partial_signature does not verify in this session", channelId,
                                      "Internal error");

        return AggregateAndCheck(channelId, aggregate, session,
                                 [localSignature.PartialSignature, remoteSignature.PartialSignature], sigHash);
    }

    /// <summary>
    /// The MuSig2 key aggregate of the current funding and the BIP 341 key-path sighash (<c>SIGHASH_DEFAULT</c>) of
    /// input <paramref name="sharedInputIndex"/>, over every output the transaction spends: the shared input's is the
    /// current funding output (the signer's own), the others come from <paramref name="spentOutputs"/> by outpoint.
    /// </summary>
    private (MusigKeyAggregate Aggregate, byte[] SigHash) GetSharedInputSession(
        ChannelId channelId, Transaction tx, int sharedInputIndex, FundingKeys current,
        IReadOnlyList<SpentOutput> spentOutputs)
    {
        var aggregate = _musig2.AggregateTaprootKeyPath(current.LocalPubKey, current.RemotePubKey);
        var spent = new TxOut[tx.Inputs.Count];
        for (var i = 0; i < tx.Inputs.Count; i++)
        {
            if (i == sharedInputIndex)
            {
                spent[i] = BuildTaprootFundingTxOut(current);
                continue;
            }

            var prevOut = tx.Inputs[i].PrevOut;
            var output = spentOutputs.FirstOrDefault(o => o.Index == prevOut.N
                                                       && ((byte[])o.TxId).AsSpan()
                                                                          .SequenceEqual(prevOut.Hash.ToBytes()))
                      ?? throw new SignerException(
                             $"The output spent by input {i} ({prevOut}) of the splice is not known", channelId,
                             "Internal error");
            spent[i] = new TxOut(Money.Satoshis(output.Amount.Satoshi), new Script((byte[])output.ScriptPubKey));
        }

        var sigHash = tx.GetSignatureHashTaproot(spent, new TaprootExecutionData(sharedInputIndex)
        {
            SigHash = TaprootSigHash.Default
        });
        return (aggregate, sigHash.ToBytes());
    }

    /// <summary>Takes (removes) the secret half of our shared-input nonce <paramref name="publicNonce"/>; null if unknown.</summary>
    private MusigSecretNonce? TakeSpliceFundingNonce(ChannelId channelId, MusigPublicNonce publicNonce)
    {
        if (_nativeNonceStore is { } store)
        {
            var restored = store.Take("splice", channelId, publicNonce);
            if (_spliceFundingNonces.TryGetValue(channelId, out var live))
                lock (live)
                {
                    var index = live.FindIndex(n => n.PublicNonce == publicNonce);
                    if (index >= 0) { live[index].SecretNonce.Dispose(); live.RemoveAt(index); }
                }
            return restored;
        }
        if (!_spliceFundingNonces.TryGetValue(channelId, out var nonces))
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

    /// <summary>Drops (and zeroes) the channel's live shared-input nonces (the channel is unregistered).</summary>
    private void ForgetSpliceFundingNonces(ChannelId channelId)
    {
        _nativeNonceStore?.Forget("splice", channelId);
        if (!_spliceFundingNonces.TryRemove(channelId, out var nonces))
            return;

        lock (nonces)
        {
            foreach (var nonce in nonces)
                nonce.SecretNonce.Dispose();
            nonces.Clear();
        }
    }
}