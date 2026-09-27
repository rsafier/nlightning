using Microsoft.Extensions.Logging;
using NBitcoin;
using NBitcoin.Crypto;

namespace NLightning.Infrastructure.Bitcoin.Signers;

using Domain.Bitcoin.Transactions.Outputs;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Splicing;
using Domain.Channels.Splicing.Enums;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Exceptions;
using Domain.Money;

/// <summary>
/// Splicing (splicing plan SP1-C-T1..T3): per-funding keys (D5), commitments signed and verified per active funding,
/// the shared-input signature behind invariant SP-I1 and the per-funding S1 rule SP-I4.
/// </summary>
/// <remarks>
/// <para>The channel's current funding is the one in its <see cref="ChannelSigningInfo"/> (the fields every
/// single-funding member uses, so a channel that is never spliced signs byte for byte as before). The other fundings
/// (pending splices, and the replaced or discarded ones kept for their on-chain resolution, SP-I5) are held per channel
/// in <see cref="_spliceFundings"/>, guarded by the channel's commitment lock like the S1 and revocation guards.</para>
/// <para>Funding key <c>i</c> of a channel is <c>m/0'</c> of the channel key for <c>i = 0</c> (the original funding
/// key) and <c>m/0'/i'</c> for <c>i &gt; 0</c>: it depends only on the channel key index and <c>i</c>, so a node
/// restored from its key file and a static channel backup derives it again.</para>
/// </remarks>
public partial class LocalLightningSigner
{
    // The fundings of each channel other than its current one, and the SP-I1 marks, under the channel's commitment lock
    private readonly System.Collections.Concurrent.ConcurrentDictionary<ChannelId, SpliceFundingState> _spliceFundings =
        new();

    /// <inheritdoc />
    public CompactPubKey GetFundingPubKey(ChannelId channelId, uint fundingKeyIndex)
    {
        var signingInfo = GetRegisteredSigningInfo(channelId);
        return GetFundingPubKey(signingInfo.ChannelKeyIndex, fundingKeyIndex);
    }

    /// <summary>
    /// Our funding public key number <paramref name="fundingKeyIndex"/> of the channel with key index
    /// <paramref name="channelKeyIndex"/> (splicing plan D5), without a registered channel: what a restore from a
    /// static channel backup derives.
    /// </summary>
    public CompactPubKey GetFundingPubKey(uint channelKeyIndex, uint fundingKeyIndex)
    {
        using var key = GenerateFundingPrivateKey(channelKeyIndex, fundingKeyIndex);
        return key.PubKey.ToBytes();
    }

    /// <inheritdoc />
    public void RegisterFunding(ChannelId channelId, ChannelFunding funding)
    {
        ArgumentNullException.ThrowIfNull(funding);
        var signingInfo = GetRegisteredSigningInfo(channelId);
        if (funding.Status != ChannelFundingStatus.Pending)
            throw new SignerException($"Only a pending funding can be registered, not a {funding.Status} one",
                                      channelId, "Internal error");

        lock (GetCommitmentLock(channelId))
        {
            signingInfo = GetRegisteredSigningInfo(channelId);
            if (funding.FundingTxId == signingInfo.FundingTxId)
                throw new SignerException($"Funding {funding.FundingTxId} is the channel's current funding",
                                          channelId, "Internal error");

            RegisterFundingLocked(channelId, signingInfo, funding);
        }
    }

    /// <inheritdoc />
    public void MarkSpliceCommitmentPersisted(ChannelId channelId, TxId fundingTxId, ulong localCommitmentNumber)
    {
        _ = GetRegisteredSigningInfo(channelId);
        lock (GetCommitmentLock(channelId))
        {
            var state = GetSpliceState(channelId);
            if (!state.Fundings.TryGetValue(fundingTxId, out var funding)
             || funding.Status != ChannelFundingStatus.Pending)
                throw new SignerException($"Funding {fundingTxId} is not a registered pending splice", channelId,
                                          "Internal error");

            state.PersistedCommitments[fundingTxId] = localCommitmentNumber;
        }

        if (_logger.IsEnabled(LogLevel.Information))
            _logger.LogInformation("Local commitment {Number} of splice funding {FundingTxId} of channel {ChannelId} is "
                                 + "persisted with the peer's signatures", localCommitmentNumber, fundingTxId,
                                   channelId);
    }

    /// <inheritdoc />
    public CompactSignature SignSpliceSharedInput(ChannelId channelId, TxId newFundingTxId,
                                                  SignedTransaction unsignedSpliceTransaction, int sharedInputIndex)
    {
        ArgumentNullException.ThrowIfNull(unsignedSpliceTransaction);

        // A channel that is not registered is loaded (a database read) before the lock is taken
        _ = GetRegisteredSigningInfo(channelId);

        lock (GetCommitmentLock(channelId))
        {
            var signingInfo = GetRegisteredSigningInfo(channelId);
            ThrowIfDataLoss(channelId, "sign a splice's shared input");
            ThrowIfBroadcastSigned(channelId, "sign a splice's shared input");

            var state = GetSpliceState(channelId);
            if (!state.Fundings.TryGetValue(newFundingTxId, out var newFunding)
             || newFunding.Status != ChannelFundingStatus.Pending)
                throw new SignerException($"Refusing to sign the shared input: {newFundingTxId} is not a registered "
                                        + "pending splice", channelId, "Internal error");

            // SP-I1: the commitment spending the new funding output, with the peer's signatures, is on disk at the
            // current local commitment number before the current funding output is signed away
            var localCommitmentNumber = _localCommitmentNumbers.GetValueOrDefault(channelId);
            if (!state.PersistedCommitments.TryGetValue(newFundingTxId, out var persistedNumber))
                throw new SignerException(
                    $"Refusing to sign the shared input: no commitment for splice {newFundingTxId} is persisted (SP-I1)",
                    channelId, "Internal error");
            if (persistedNumber != localCommitmentNumber)
                throw new SignerException(
                    $"Refusing to sign the shared input: the persisted commitment for splice {newFundingTxId} is "
                  + $"number {persistedNumber}, the current local commitment is {localCommitmentNumber} (SP-I1)",
                    channelId, "Internal error");

            var tx = LoadTransaction(channelId, unsignedSpliceTransaction, "splice");
            if (new TxId(tx.GetHash().ToBytes()) != newFundingTxId)
                throw new SignerException(
                    $"Refusing to sign the shared input: the transaction is {tx.GetHash()}, not splice {newFundingTxId}",
                    channelId, "Internal error");

            // The splice must create exactly the registered funding output, or the commitment we hold spends nothing
            var newFundingKeys = FromFunding(newFunding);
            if (newFunding.OutputIndex >= tx.Outputs.Count)
                throw new SignerException($"The splice transaction has no output {newFunding.OutputIndex}", channelId,
                                          "Internal error");
            var expected = BuildFundingOutput(newFundingKeys).ToTxOut();
            var actual = tx.Outputs[newFunding.OutputIndex];
            if (actual.ScriptPubKey != expected.ScriptPubKey || actual.Value != expected.Value)
                throw new SignerException("Refusing to sign the shared input: the splice's funding output is not the "
                                        + "registered one", channelId, "Internal error");

            var current = FromSigningInfo(signingInfo);
            ThrowIfNotSharedInput(channelId, tx, sharedInputIndex, current);

            var signature = SignFundingSpend(channelId, signingInfo.ChannelKeyIndex, current, tx, sharedInputIndex);
            if (_logger.IsEnabled(LogLevel.Information))
                _logger.LogInformation("Signed the shared input of splice {FundingTxId} of channel {ChannelId}",
                                       newFundingTxId, channelId);

            return signature;
        }
    }

    /// <inheritdoc />
    public void ValidateSpliceSharedInputSignature(ChannelId channelId, SignedTransaction unsignedSpliceTransaction,
                                                   int sharedInputIndex, CompactSignature remoteSignature)
    {
        ArgumentNullException.ThrowIfNull(unsignedSpliceTransaction);
        ArgumentNullException.ThrowIfNull(remoteSignature);
        var signingInfo = GetRegisteredSigningInfo(channelId);
        var current = FromSigningInfo(signingInfo);

        var tx = LoadTransaction(channelId, unsignedSpliceTransaction, "splice");
        ThrowIfNotSharedInput(channelId, tx, sharedInputIndex, current);
        VerifyFundingSpendSignature(channelId, current, tx, sharedInputIndex, remoteSignature);
    }

    /// <inheritdoc />
    public CompactSignature SignChannelTransaction(ChannelId channelId, TxId fundingTxId,
                                                   SignedTransaction unsignedTransaction)
    {
        ArgumentNullException.ThrowIfNull(unsignedTransaction);
        _ = GetRegisteredSigningInfo(channelId);

        lock (GetCommitmentLock(channelId))
        {
            var signingInfo = GetRegisteredSigningInfo(channelId);
            ThrowIfDataLoss(channelId, "sign a commitment");
            ThrowIfBroadcastSigned(channelId, "sign a channel transaction");

            var funding = ResolveFunding(channelId, signingInfo, fundingTxId, activeOnly: true);
            var tx = LoadTransaction(channelId, unsignedTransaction, "commitment");
            ThrowIfNotSpendingFunding(channelId, tx, funding);

            return SignFundingSpend(channelId, signingInfo.ChannelKeyIndex, funding, tx, 0);
        }
    }

    /// <inheritdoc />
    public void ValidateSignature(ChannelId channelId, TxId fundingTxId, CompactSignature signature,
                                  SignedTransaction unsignedTransaction)
    {
        ArgumentNullException.ThrowIfNull(signature);
        ArgumentNullException.ThrowIfNull(unsignedTransaction);
        var signingInfo = GetRegisteredSigningInfo(channelId);

        FundingKeys funding;
        lock (GetCommitmentLock(channelId))
            funding = ResolveFunding(channelId, signingInfo, fundingTxId, activeOnly: false);

        var tx = LoadTransaction(channelId, unsignedTransaction, "commitment");
        ThrowIfNotSpendingFunding(channelId, tx, funding);
        VerifyFundingSpendSignature(channelId, funding, tx, 0, signature);
    }

    /// <inheritdoc />
    public SignedTransaction SignLocalCommitmentForBroadcast(ChannelId channelId, TxId fundingTxId,
                                                             ulong commitmentNumber,
                                                             SignedTransaction unsignedCommitment,
                                                             CompactSignature remoteSignature)
    {
        ArgumentNullException.ThrowIfNull(unsignedCommitment);
        ArgumentNullException.ThrowIfNull(remoteSignature);
        _ = GetRegisteredSigningInfo(channelId);

        // SP-I4: the same lock as the single-funding broadcast, AdvanceLocalCommitment and RevealPerCommitmentSecret
        lock (GetCommitmentLock(channelId))
        {
            var signingInfo = GetRegisteredSigningInfo(channelId);
            var funding = ResolveFunding(channelId, signingInfo, fundingTxId, activeOnly: true);
            var tx = LoadTransaction(channelId, unsignedCommitment, "commitment");
            ThrowIfNotSpendingFunding(channelId, tx, funding);

            return SignLocalCommitmentForBroadcastCore(channelId, signingInfo, funding, commitmentNumber,
                                                       unsignedCommitment, remoteSignature);
        }
    }

    /// <inheritdoc />
    public void LockFunding(ChannelId channelId, TxId fundingTxId) => LockFunding(channelId, fundingTxId, null);

    /// <inheritdoc />
    public void LockFunding(ChannelId channelId, TxId fundingTxId, ShortChannelId? shortChannelId)
    {
        _ = GetRegisteredSigningInfo(channelId);
        lock (GetCommitmentLock(channelId))
        {
            var signingInfo = GetRegisteredSigningInfo(channelId);
            if (signingInfo.FundingTxId == fundingTxId)
            {
                // A repeated lock may bring the short channel id the first one did not have
                if (shortChannelId is not null && signingInfo.ShortChannelId is null)
                    _channelSigningInfo[channelId] = signingInfo with { ShortChannelId = shortChannelId };
                return;
            }

            var state = GetSpliceState(channelId);
            if (!state.Fundings.TryGetValue(fundingTxId, out var locked)
             || locked.Status != ChannelFundingStatus.Pending)
                throw new SignerException($"Funding {fundingTxId} is not a registered pending splice", channelId,
                                          "Internal error");

            // The former current funding is spent by the locked splice; its keys stay known (SP-I5)
            var replaced = new ChannelFunding(signingInfo.FundingTxId, signingInfo.FundingOutputIndex,
                                              signingInfo.FundingSatoshis / 1_000, signingInfo.LocalFundingPubKey,
                                              signingInfo.RemoteFundingPubKey, signingInfo.LocalFundingKeyIndex, 0, 0,
                                              signingInfo.LocalFundingKeyIndex == 0
                                                  ? ChannelFundingKind.Initial
                                                  : ChannelFundingKind.Splice, ChannelFundingStatus.Replaced,
                                              ShortChannelId: signingInfo.ShortChannelId);
            foreach (var (txId, other) in state.Fundings.ToList())
            {
                if (other.Status == ChannelFundingStatus.Pending && txId != fundingTxId)
                    state.Fundings[txId] = other with { Status = ChannelFundingStatus.Discarded };
            }

            state.Fundings.Remove(fundingTxId);
            state.Fundings[replaced.FundingTxId] = replaced;
            state.PersistedCommitments.Clear();

            _channelSigningInfo[channelId] = signingInfo with
            {
                FundingTxId = locked.FundingTxId,
                FundingOutputIndex = locked.OutputIndex,
                FundingSatoshis = locked.CapacityMsat,
                LocalFundingPubKey = locked.LocalFundingPubKey,
                RemoteFundingPubKey = locked.RemoteFundingPubKey,
                LocalFundingKeyIndex = locked.LocalFundingKeyIndex,
                ShortChannelId = shortChannelId ?? locked.ShortChannelId
            };
        }

        if (_logger.IsEnabled(LogLevel.Information))
            _logger.LogInformation("Splice {FundingTxId} is the current funding of channel {ChannelId}", fundingTxId,
                                   channelId);
    }

    /// <summary>
    /// Our funding key <paramref name="fundingKeyIndex"/> of the channel (splicing plan D5): index 0 is
    /// <see cref="GenerateFundingPrivateKey(uint)"/> (the channel's original funding key), index <c>i &gt; 0</c> is
    /// <c>m/0'/i'</c> of the channel key.
    /// </summary>
    protected virtual Key GenerateFundingPrivateKey(uint channelKeyIndex, uint fundingKeyIndex)
    {
        if (fundingKeyIndex == 0)
            return GenerateFundingPrivateKey(channelKeyIndex);

        if (fundingKeyIndex > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(fundingKeyIndex), fundingKeyIndex,
                                                  "Funding key indexes are hardened BIP 32 indexes (below 2^31)");

        var channelKey = ExtKey.CreateFromBytes(_secureKeyManager.GetChannelKeyAtIndex(channelKeyIndex));
        return channelKey.Derive(FundingDerivationIndex, true).Derive((int)fundingKeyIndex, true).PrivateKey;
    }

    /// <summary>
    /// Registers the channel's other fundings and the SP-I1 marks carried by a registration (a restart), under the
    /// commitment lock. Fundings that conflict with a known one are refused and logged, never merged.
    /// </summary>
    private void RestoreSpliceState(ChannelId channelId, ChannelSigningInfo incoming)
    {
        if (incoming.Fundings is not { Count: > 0 } && incoming.PersistedSpliceCommitments is not { Count: > 0 })
            return;

        lock (GetCommitmentLock(channelId))
        {
            var signingInfo = GetRegisteredSigningInfo(channelId);
            foreach (var funding in incoming.Fundings ?? [])
            {
                if (funding.FundingTxId == signingInfo.FundingTxId || funding.Status == ChannelFundingStatus.Current)
                    continue;

                RegisterFundingLocked(channelId, signingInfo, funding);
            }

            var state = GetSpliceState(channelId);
            foreach (var (fundingTxId, number) in incoming.PersistedSpliceCommitments ?? new Dictionary<TxId, ulong>())
            {
                if (state.Fundings.TryGetValue(fundingTxId, out var funding)
                 && funding.Status == ChannelFundingStatus.Pending)
                    state.PersistedCommitments[fundingTxId] = number;
            }
        }
    }

    /// <summary>Whether the channel has fundings besides its current one, or a rotated current funding key.</summary>
    private bool HasSpliceHistory(ChannelId channelId, ChannelSigningInfo current) =>
        current.LocalFundingKeyIndex != 0
     || (_spliceFundings.TryGetValue(channelId, out var state) && state.Fundings.Count > 0);

    /// <summary>
    /// Whether <paramref name="incoming"/> names the current or another known funding of a spliced channel (a
    /// registration from a model or row that predates the lock): it may refresh the channel's other data, never its
    /// fundings.
    /// </summary>
    private bool IsKnownSpliceFunding(ChannelId channelId, ChannelSigningInfo current, ChannelSigningInfo incoming)
    {
        if (incoming.ChannelKeyIndex != current.ChannelKeyIndex || !HasSpliceHistory(channelId, current))
            return false;

        if (incoming.FundingTxId == current.FundingTxId)
            return incoming.FundingOutputIndex == current.FundingOutputIndex;

        return _spliceFundings.TryGetValue(channelId, out var state)
            && state.Fundings.TryGetValue(incoming.FundingTxId, out var known)
            && known.OutputIndex == incoming.FundingOutputIndex;
    }

    private void RegisterFundingLocked(ChannelId channelId, ChannelSigningInfo signingInfo, ChannelFunding funding)
    {
        // The funding key must be ours at the stated index: a wrong index would sign a script we cannot spend
        using (var key = GenerateFundingPrivateKey(signingInfo.ChannelKeyIndex, funding.LocalFundingKeyIndex))
        {
            if (!key.PubKey.ToBytes().AsSpan().SequenceEqual((byte[])funding.LocalFundingPubKey))
                throw new SignerException(
                    $"Funding {funding.FundingTxId} names a local funding key that is not our key "
                  + $"{funding.LocalFundingKeyIndex}", channelId, "Internal error");
        }

        var state = GetSpliceState(channelId);
        if (state.Fundings.TryGetValue(funding.FundingTxId, out var known))
        {
            if (!IsSameFunding(known, funding))
                throw new SignerException($"Funding {funding.FundingTxId} is already registered with other data",
                                          channelId, "Internal error");

            // The same funding registered again once it confirmed: keep what it learned, never its keys or status
            if (known.ShortChannelId is null && funding.ShortChannelId is not null)
                state.Fundings[funding.FundingTxId] = known with
                {
                    ShortChannelId = funding.ShortChannelId,
                    ConfirmedHeight = known.ConfirmedHeight ?? funding.ConfirmedHeight
                };

            return;
        }

        state.Fundings[funding.FundingTxId] = funding;
        if (_logger.IsEnabled(LogLevel.Information))
            _logger.LogInformation("Registered {Status} funding {FundingTxId} of channel {ChannelId}", funding.Status,
                                   funding.FundingTxId, channelId);
    }

    private static bool IsSameFunding(ChannelFunding a, ChannelFunding b) =>
        a.OutputIndex == b.OutputIndex && a.CapacitySatoshis == b.CapacitySatoshis
                                       && a.LocalFundingPubKey == b.LocalFundingPubKey
                                       && a.RemoteFundingPubKey == b.RemoteFundingPubKey
                                       && a.LocalFundingKeyIndex == b.LocalFundingKeyIndex;

    private SpliceFundingState GetSpliceState(ChannelId channelId) =>
        _spliceFundings.GetOrAdd(channelId, static _ => new SpliceFundingState());

    /// <summary>
    /// The funding <paramref name="fundingTxId"/> of the channel: the current one, a pending splice, or (when
    /// <paramref name="activeOnly"/> is false) a replaced or discarded one. Call it under the commitment lock.
    /// </summary>
    private FundingKeys ResolveFunding(ChannelId channelId, ChannelSigningInfo signingInfo, TxId fundingTxId,
                                       bool activeOnly)
    {
        if (fundingTxId == signingInfo.FundingTxId)
            return FromSigningInfo(signingInfo);

        if (!_spliceFundings.TryGetValue(channelId, out var state)
         || !state.Fundings.TryGetValue(fundingTxId, out var funding))
            throw new SignerException($"Funding {fundingTxId} is not a funding of the channel", channelId,
                                      "Internal error");

        if (activeOnly && funding.Status != ChannelFundingStatus.Pending)
            throw new SignerException($"Refusing to sign for {funding.Status} funding {fundingTxId}", channelId,
                                      "Internal error");

        return FromFunding(funding);
    }

    private static FundingKeys FromSigningInfo(ChannelSigningInfo signingInfo) =>
        new(signingInfo.FundingTxId, signingInfo.FundingOutputIndex, signingInfo.FundingSatoshis,
            signingInfo.LocalFundingPubKey, signingInfo.RemoteFundingPubKey, signingInfo.LocalFundingKeyIndex);

    private static FundingKeys FromFunding(ChannelFunding funding) =>
        new(funding.FundingTxId, funding.OutputIndex, LightningMoney.Satoshis(funding.CapacitySatoshis),
            funding.LocalFundingPubKey, funding.RemoteFundingPubKey, funding.LocalFundingKeyIndex);

    private Outputs.FundingOutput BuildFundingOutput(FundingKeys funding) =>
        _fundingOutputBuilder.Build(new FundingOutputInfo(funding.Amount, funding.LocalPubKey, funding.RemotePubKey,
                                                          funding.TxId, funding.OutputIndex));

    private Transaction LoadTransaction(ChannelId channelId, SignedTransaction transaction, string what)
    {
        try
        {
            return Transaction.Load(transaction.RawTxBytes, _network);
        }
        catch (Exception e)
        {
            throw new SignerException($"Failed to load the {what} transaction", channelId, e, "Internal error");
        }
    }

    private static void ThrowIfNotSpendingFunding(ChannelId channelId, Transaction tx, FundingKeys funding)
    {
        if (tx.Inputs.Count != 1)
            throw new SignerException("A commitment transaction has exactly one input", channelId, "Internal error");

        var prevOut = tx.Inputs[0].PrevOut;
        if (new TxId(prevOut.Hash.ToBytes()) != funding.TxId || prevOut.N != funding.OutputIndex)
            throw new SignerException($"The transaction does not spend funding {funding.TxId}:{funding.OutputIndex}",
                                      channelId, "Internal error");
    }

    private static void ThrowIfNotSharedInput(ChannelId channelId, Transaction tx, int sharedInputIndex,
                                              FundingKeys current)
    {
        if (sharedInputIndex < 0 || sharedInputIndex >= tx.Inputs.Count)
            throw new SignerException($"The splice transaction has no input {sharedInputIndex}", channelId,
                                      "Internal error");

        var prevOut = tx.Inputs[sharedInputIndex].PrevOut;
        if (new TxId(prevOut.Hash.ToBytes()) != current.TxId || prevOut.N != current.OutputIndex)
            throw new SignerException(
                $"Input {sharedInputIndex} of the splice does not spend the current funding output {current.TxId}:"
              + $"{current.OutputIndex}", channelId, "Internal error");
    }

    /// <summary>
    /// Our funding-key signature (<c>SIGHASH_ALL</c>, BIP 143, RFC 6979, low-S) of input <paramref name="inputIndex"/>
    /// of <paramref name="tx"/>, which spends <paramref name="funding"/>, without the guards of the public members.
    /// </summary>
    private CompactSignature SignFundingSpend(ChannelId channelId, uint channelKeyIndex, FundingKeys funding,
                                              Transaction tx, int inputIndex)
    {
        var fundingOutput = BuildFundingOutput(funding);
        var sigHash = tx.GetSignatureHash(fundingOutput.RedeemScript, inputIndex, SigHash.All,
                                          fundingOutput.ToTxOut(), HashVersion.WitnessV0);

        using var fundingKey = GenerateFundingPrivateKey(channelKeyIndex, funding.KeyIndex);

        // A rotated key is checked against the funding's script (index 0 signs exactly as the single-funding path)
        if (funding.KeyIndex != 0 && !fundingKey.PubKey.ToBytes().AsSpan().SequenceEqual((byte[])funding.LocalPubKey))
            throw new SignerException(
                $"The derived funding key {funding.KeyIndex} does not match the local funding key of {funding.TxId}",
                channelId, "Internal error");

        var signature = fundingKey.Sign(sigHash, new SigningOptions(SigHash.All, false));
        return signature.Signature.MakeCanonical().ToCompact();
    }

    /// <summary>
    /// Checks the peer's signature (compact, low-S, <c>SIGHASH_ALL</c>) of input <paramref name="inputIndex"/> of
    /// <paramref name="tx"/> spending <paramref name="funding"/> against its remote funding key.
    /// </summary>
    private void VerifyFundingSpendSignature(ChannelId channelId, FundingKeys funding, Transaction tx, int inputIndex,
                                             CompactSignature signature)
    {
        if (!ECDSASignature.TryParseFromCompact(signature, out var ecdsa))
            throw new SignerException("Failed to parse compact signature", channelId, "Signature format error");
        if (!ecdsa.IsLowS)
            throw new SignerException("Signature is not low S", channelId, "Signature is malleable");

        PubKey remoteKey;
        try
        {
            remoteKey = new PubKey(funding.RemotePubKey);
        }
        catch (Exception e)
        {
            throw new SignerException("The remote funding key is not a valid public key", channelId, e,
                                      "Internal error");
        }

        var fundingOutput = BuildFundingOutput(funding);
        var sigHash = tx.GetSignatureHash(fundingOutput.RedeemScript, inputIndex, SigHash.All,
                                          fundingOutput.ToTxOut(), HashVersion.WitnessV0);
        if (!remoteKey.Verify(sigHash, ecdsa))
            throw new SignerException("Peer signature is invalid", channelId, "Invalid signature provided");
    }

    /// <summary>One funding the signer can sign for: outpoint, amount, both funding keys and our key index.</summary>
    private readonly record struct FundingKeys(TxId TxId, ushort OutputIndex, LightningMoney Amount,
                                               CompactPubKey LocalPubKey, CompactPubKey RemotePubKey, uint KeyIndex);

    /// <summary>A channel's fundings besides the current one, and the SP-I1 marks of its pending splices.</summary>
    private sealed class SpliceFundingState
    {
        public Dictionary<TxId, ChannelFunding> Fundings { get; } = new();

        public Dictionary<TxId, ulong> PersistedCommitments { get; } = new();
    }
}