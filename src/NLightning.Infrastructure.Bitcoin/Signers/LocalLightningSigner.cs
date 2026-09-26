using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using NBitcoin;
using NBitcoin.Crypto;
using NBitcoin.Secp256k1;

namespace NLightning.Infrastructure.Bitcoin.Signers;

using Builders;
using Crypto.Contexts;
using Domain.Bitcoin.Enums;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.Transactions.Models;
using Domain.Bitcoin.Transactions.Outputs;
using Domain.Bitcoin.ValueObjects;
using Domain.Bitcoin.Wallet.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.Constants;
using Domain.Crypto.ValueObjects;
using Domain.Exceptions;
using Domain.Node.Options;
using Domain.Onchain.Enums;
using Domain.Onchain.Models;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Models;

public class LocalLightningSigner : ILightningSigner
{
    private const int FundingDerivationIndex = 0; // m/0' is the funding key
    private const int RevocationDerivationIndex = 1; // m/1' is the revocation key
    private const int PaymentDerivationIndex = 2; // m/2' is the payment key
    private const int DelayedPaymentDerivationIndex = 3; // m/3' is the delayed payment key
    private const int HtlcDerivationIndex = 4; // m/4' is the HTLC key
    private const int PerCommitmentSeedDerivationIndex = 5; // m/5' is the per-commitment seed

    private readonly ISecureKeyManager _secureKeyManager;
    private readonly IUtxoMemoryRepository _utxoMemoryRepository;
    private readonly IFundingOutputBuilder _fundingOutputBuilder;
    private readonly IKeyDerivationService _keyDerivationService;
    private readonly ConcurrentDictionary<ChannelId, ChannelSigningInfo> _channelSigningInfo = new();

    // Current local commitment number per channel: the revocation guard of RevealPerCommitmentSecret (NL-189)
    private readonly ConcurrentDictionary<ChannelId, ulong> _localCommitmentNumbers = new();

    // Channels whose channel_reestablish proved data loss: nothing is signed for them any more (I12, N9-T4)
    private readonly ConcurrentDictionary<ChannelId, bool> _dataLossChannels = new();

    // Invariant S1 (BOLT 5 plan §3.5): the local commitment number signed for broadcast per channel. Once set, the
    // secret of that commitment is never released and nothing later is signed for the channel.
    private readonly ConcurrentDictionary<ChannelId, ulong> _broadcastSignedNumbers = new();
    private readonly ILogger<LocalLightningSigner> _logger;
    private readonly Network _network;

    public LocalLightningSigner(IFundingOutputBuilder fundingOutputBuilder,
                                IKeyDerivationService keyDerivationService, ILogger<LocalLightningSigner> logger,
                                NodeOptions nodeOptions, ISecureKeyManager secureKeyManager,
                                IUtxoMemoryRepository utxoMemoryRepository)
    {
        _fundingOutputBuilder = fundingOutputBuilder;
        _keyDerivationService = keyDerivationService;
        _logger = logger;
        _secureKeyManager = secureKeyManager;
        _utxoMemoryRepository = utxoMemoryRepository;

        _network = Network.GetNetwork(nodeOptions.BitcoinNetwork) ??
                   throw new ArgumentException("Invalid Bitcoin network specified", nameof(nodeOptions));

        // TODO: Load channel key data from database
    }

    /// <inheritdoc />
    public uint CreateNewChannel(out ChannelBasepoints basepoints, out CompactPubKey firstPerCommitmentPoint)
    {
        // Generate a new key for this channel
        var channelPrivExtKey = _secureKeyManager.GetNextChannelKey(out var index);
        var channelKey = ExtKey.CreateFromBytes(channelPrivExtKey);

        // Generate Lightning basepoints using proper BIP32 derivation paths
        using var localFundingSecret = GenerateFundingPrivateKey(channelKey);
        using var localRevocationSecret = channelKey.Derive(RevocationDerivationIndex, true).PrivateKey;
        using var localPaymentSecret = channelKey.Derive(PaymentDerivationIndex, true).PrivateKey;
        using var localDelayedPaymentSecret = channelKey.Derive(DelayedPaymentDerivationIndex, true).PrivateKey;
        using var localHtlcSecret = channelKey.Derive(HtlcDerivationIndex, true).PrivateKey;
        using var perCommitmentSeed = channelKey.Derive(PerCommitmentSeedDerivationIndex, true).PrivateKey;

        // Generate static basepoints (these don't change per commitment)
        basepoints = new ChannelBasepoints(
            localFundingSecret.PubKey.ToBytes(),
            localRevocationSecret.PubKey.ToBytes(),
            localPaymentSecret.PubKey.ToBytes(),
            localDelayedPaymentSecret.PubKey.ToBytes(),
            localHtlcSecret.PubKey.ToBytes()
        );

        // Generate the first per-commitment point (commitment number 0)
        var firstPerCommitmentSecretBytes = _keyDerivationService
           .GeneratePerCommitmentSecret(perCommitmentSeed.ToBytes(), PerCommitmentIndex.From(0));
        using var firstPerCommitmentSecret = new Key(firstPerCommitmentSecretBytes);
        firstPerCommitmentPoint = firstPerCommitmentSecret.PubKey.ToBytes();

        return index;
    }

    /// <inheritdoc />
    public ChannelBasepoints GetChannelBasepoints(uint channelKeyIndex)
    {
        _logger.LogTrace("Generating channel basepoints for key index {ChannelKeyIndex}", channelKeyIndex);

        // Recreate the basepoints from the channel key index
        var channelExtKey = _secureKeyManager.GetChannelKeyAtIndex(channelKeyIndex);
        var channelKey = ExtKey.CreateFromBytes(channelExtKey);

        using var localFundingSecret = channelKey.Derive(FundingDerivationIndex, true).PrivateKey;
        using var localRevocationSecret = channelKey.Derive(RevocationDerivationIndex, true).PrivateKey;
        using var localPaymentSecret = channelKey.Derive(PaymentDerivationIndex, true).PrivateKey;
        using var localDelayedPaymentSecret = channelKey.Derive(DelayedPaymentDerivationIndex, true).PrivateKey;
        using var localHtlcSecret = channelKey.Derive(HtlcDerivationIndex, true).PrivateKey;

        return new ChannelBasepoints(
            localFundingSecret.PubKey.ToBytes(),
            localRevocationSecret.PubKey.ToBytes(),
            localPaymentSecret.PubKey.ToBytes(),
            localDelayedPaymentSecret.PubKey.ToBytes(),
            localHtlcSecret.PubKey.ToBytes()
        );
    }

    /// <inheritdoc />
    public ChannelBasepoints GetChannelBasepoints(ChannelId channelId)
    {
        _logger.LogTrace("Retrieving channel basepoints for channel {ChannelId}", channelId);

        if (!_channelSigningInfo.TryGetValue(channelId, out var signingInfo))
            throw new SignerException($"Channel {channelId} not registered", channelId);

        return GetChannelBasepoints(signingInfo.ChannelKeyIndex);
    }

    /// <inheritdoc />
    public CompactPubKey GetNodePublicKey() => _secureKeyManager.GetNodeKeyPair().CompactPubKey;

    /// <inheritdoc />
    public CompactSignature SignNodeMessage(Hash messageHash)
    {
        // The key manager hands out a copy of the node key; wipe it once the key is parsed
        var privateKey = _secureKeyManager.GetNodeKeyPair().PrivKey.Value;
        try
        {
            if (!NLightningCryptoContext.Instance.TryCreateECPrivKey(privateKey, out var ecPrivKey)
             || ecPrivKey is null)
                throw new SignerException("The node key is not a valid secp256k1 private key",
                                          "Internal error");

            using (ecPrivKey)
            {
                // libsecp256k1 signs with RFC 6979 nonces and always returns a low-S signature
                if (!ecPrivKey.TrySignECDSA((byte[])messageHash, out var signature) || signature is null)
                    throw new SignerException("Failed to sign the node message", "Internal error");

                var compact = new byte[CryptoConstants.MaxSignatureSize];
                signature.WriteCompactToSpan(compact);
                return compact;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(privateKey);
        }
    }

    /// <inheritdoc />
    public bool VerifyNodeMessage(Hash messageHash, CompactSignature signature, CompactPubKey nodeId)
    {
        ArgumentNullException.ThrowIfNull(signature);
        if (signature.Value.Length != CryptoConstants.MaxSignatureSize
         || !SecpECDSASignature.TryCreateFromCompact(signature.Value, out var ecdsaSignature)
         || ecdsaSignature is null
         || !ECPubKey.TryCreate((byte[])nodeId, NLightningCryptoContext.Instance, out _, out var ecPubKey)
         || ecPubKey is null)
            return false;

        // libsecp256k1 verification rejects high-S signatures, but a relayed (malleated) one is still valid
        var (r, s) = ecdsaSignature;
        if (s.IsHigh)
            ecdsaSignature = new SecpECDSASignature(r, s.Negate(), true);

        return ecPubKey.SigVerify(ecdsaSignature, (byte[])messageHash);
    }

    /// <inheritdoc />
    public CompactPubKey GetPerCommitmentPoint(uint channelKeyIndex, ulong commitmentNumber)
    {
        _logger.LogTrace(
            "Generating per-commitment point for channel key index {ChannelKeyIndex} and commitment number {CommitmentNumber}",
            channelKeyIndex, commitmentNumber);

        // Derive the per-commitment seed from the channel key
        var channelExtKey = _secureKeyManager.GetChannelKeyAtIndex(channelKeyIndex);
        var channelKey = ExtKey.CreateFromBytes(channelExtKey);
        using var perCommitmentSeed = channelKey.Derive(PerCommitmentSeedDerivationIndex, true).PrivateKey;

        // BOLT 3: commitment n uses the per-commitment secret at index 2^48-1-n (NL-187)
        var perCommitmentSecret =
            _keyDerivationService.GeneratePerCommitmentSecret(perCommitmentSeed.ToBytes(),
                                                              PerCommitmentIndex.From(commitmentNumber));

        var perCommitmentPoint = new Key(perCommitmentSecret).PubKey;
        return perCommitmentPoint.ToBytes();
    }

    /// <inheritdoc />
    public CompactPubKey GetPerCommitmentPoint(ChannelId channelId, ulong commitmentNumber)
    {
        if (!_channelSigningInfo.TryGetValue(channelId, out var signingInfo))
            throw new SignerException($"Channel {channelId} not registered", channelId);

        return GetPerCommitmentPoint(signingInfo.ChannelKeyIndex, commitmentNumber);
    }

    /// <inheritdoc />
    public void RegisterChannel(ChannelId channelId, ChannelSigningInfo signingInfo)
    {
        _logger.LogTrace("Registering channel {ChannelId} with signing info", channelId);

        _channelSigningInfo.TryAdd(channelId, signingInfo);

        // The guard only ever moves forward, also when a channel is registered again (e.g. reloaded from the database)
        _localCommitmentNumbers.AddOrUpdate(channelId, signingInfo.LocalCommitmentNumber,
                                            (_, current) => Math.Max(current, signingInfo.LocalCommitmentNumber));

        // Data loss is sticky: a registration never clears it
        if (signingInfo.DataLossDetected)
            _dataLossChannels[channelId] = true;
    }

    /// <inheritdoc />
    public void MarkDataLoss(ChannelId channelId)
    {
        _logger.LogCritical("Data loss on channel {ChannelId}: the signer refuses every further signature for it",
                            channelId);
        _dataLossChannels[channelId] = true;
    }

    /// <inheritdoc />
    public void MarkBroadcastSigned(ChannelId channelId, ulong commitmentNumber)
    {
        if (commitmentNumber > CommitmentNumber.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(commitmentNumber), commitmentNumber,
                                                  "Commitment numbers are 48-bit values");

        // Keep the lowest number: every commitment from it on stays unrevoked (sticky, never cleared)
        _broadcastSignedNumbers.AddOrUpdate(channelId, commitmentNumber,
                                            (_, current) => Math.Min(current, commitmentNumber));

        if (_logger.IsEnabled(LogLevel.Information))
            _logger.LogInformation(
                "Local commitment {CommitmentNumber} of channel {ChannelId} is signed for broadcast: its secret is "
              + "never released and no later commitment is signed", commitmentNumber, channelId);
    }

    /// <inheritdoc />
    public bool TryGetBroadcastSignedCommitment(ChannelId channelId, out ulong commitmentNumber) =>
        _broadcastSignedNumbers.TryGetValue(channelId, out commitmentNumber);

    /// <inheritdoc />
    public CompactSignature SignSweepInput(ChannelId channelId, SweepSigningContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var signingInfo = GetRegisteredSigningInfo(channelId);

        Transaction tx;
        try
        {
            tx = Transaction.Load(context.UnsignedTransaction, _network);
        }
        catch (Exception e)
        {
            throw new SignerException("Failed to load the sweep transaction", channelId, e, "Internal error");
        }

        if (context.InputIndex < 0 || context.InputIndex >= tx.Inputs.Count)
            throw new SignerException($"The sweep transaction has no input {context.InputIndex}", channelId,
                                      "Internal error");

        using var key = DeriveSweepKey(channelId, signingInfo.ChannelKeyIndex, context);
        var pubKey = key.PubKey;

        Script scriptCode;
        if (context.WitnessScript is null)
        {
            // Only a P2WPKH to_remote has no witness script: BIP 143 signs its P2PKH script code
            if (context.KeyKind != SweepKeyKind.Payment)
                throw new SignerException($"A {context.KeyKind} spend needs its witness script", channelId,
                                          "Internal error");

            scriptCode = pubKey.Hash.ScriptPubKey;
        }
        else
        {
            scriptCode = new Script(context.WitnessScript);

            // The script must commit to the derived key (as is, or its HASH160 as in the HTLC revocation branch), so a
            // wrong key kind, point or secret never yields a signature
            if (!ScriptCommitsToKey(scriptCode, pubKey))
                throw new SignerException(
                    $"The witness script does not contain the {context.KeyKind} key of input {context.InputIndex}",
                    channelId, "Internal error");
        }

        var spentOutput = new TxOut(Money.Satoshis(context.AmountSat),
                                    context.WitnessScript is null
                                        ? pubKey.WitHash.ScriptPubKey
                                        : scriptCode.WitHash.ScriptPubKey);
        var sigHash = tx.GetSignatureHash(scriptCode, context.InputIndex, SigHash.All, spentOutput,
                                          HashVersion.WitnessV0);
        var signature = key.Sign(sigHash, new SigningOptions(SigHash.All, false));
        return signature.Signature.MakeCanonical().ToCompact();
    }

    /// <inheritdoc />
    public SignedTransaction SignLocalCommitmentForBroadcast(ChannelId channelId, ulong commitmentNumber,
                                                             SignedTransaction unsignedCommitment,
                                                             CompactSignature remoteSignature)
    {
        ArgumentNullException.ThrowIfNull(unsignedCommitment);
        ArgumentNullException.ThrowIfNull(remoteSignature);
        var signingInfo = GetRegisteredSigningInfo(channelId);
        ThrowIfDataLoss(channelId, "broadcast our commitment");

        // I4: never sign a revoked commitment for broadcast (the peer holds its revocation secret)
        var localCommitmentNumber = _localCommitmentNumbers.GetValueOrDefault(channelId);
        if (commitmentNumber < localCommitmentNumber)
            throw new SignerException(
                $"Refusing to sign revoked local commitment {commitmentNumber} for broadcast (current local "
              + $"commitment is {localCommitmentNumber})", channelId, "Internal error");

        // S1: once a commitment is signed for broadcast, only that commitment may be signed again (a retry)
        if (_broadcastSignedNumbers.TryGetValue(channelId, out var broadcastNumber) && commitmentNumber != broadcastNumber)
            throw new SignerException(
                $"Refusing to sign local commitment {commitmentNumber} for broadcast: commitment {broadcastNumber} is "
              + "already signed for broadcast", channelId, "Internal error");

        Transaction tx;
        try
        {
            tx = Transaction.Load(unsignedCommitment.RawTxBytes, _network);
        }
        catch (Exception e)
        {
            throw new SignerException("Failed to load the commitment transaction", channelId, e, "Internal error");
        }

        if (tx.Inputs.Count != 1)
            throw new SignerException("A commitment transaction has exactly one input", channelId, "Internal error");

        // The peer's signature must be valid for exactly this transaction, or the broadcast would be rejected
        ValidateSignature(channelId, remoteSignature, unsignedCommitment);
        var localCompact = SignFundingInput(channelId, signingInfo, unsignedCommitment);

        var fundingOutput = _fundingOutputBuilder.Build(new FundingOutputInfo(signingInfo.FundingSatoshis,
                                                                              signingInfo.LocalFundingPubKey,
                                                                              signingInfo.RemoteFundingPubKey,
                                                                              signingInfo.FundingTxId,
                                                                              signingInfo.FundingOutputIndex));
        var fundingScript = fundingOutput.RedeemScript;

        if (!ECDSASignature.TryParseFromCompact(localCompact, out var localSignature)
         || !ECDSASignature.TryParseFromCompact(remoteSignature, out var remoteEcdsa))
            throw new SignerException("Failed to parse a commitment signature", channelId, "Internal error");

        var localSig = new TransactionSignature(localSignature, SigHash.All).ToBytes();
        var remoteSig = new TransactionSignature(remoteEcdsa, SigHash.All).ToBytes();

        // S1: record the broadcast signature before it leaves the signer, so the secret of this commitment can never
        // be released afterwards (a racing revoke_and_ack would hand the peer the key to our on-chain to_local)
        MarkBroadcastSigned(channelId, commitmentNumber);

        // BOLT 3 funding witness: 0 <pubkey1_signature> <pubkey2_signature> <funding script>, in the script's key order
        var localFirst = IsFirstFundingKey(fundingScript, signingInfo.LocalFundingPubKey);
        tx.Inputs[0].WitScript = new WitScript(new[]
        {
            Array.Empty<byte>(), localFirst ? localSig : remoteSig, localFirst ? remoteSig : localSig,
            fundingScript.ToBytes()
        });

        if (_logger.IsEnabled(LogLevel.Information))
            _logger.LogInformation("Signed local commitment {CommitmentNumber} ({TxId}) of channel {ChannelId} for "
                                 + "broadcast", commitmentNumber, tx.GetHash(), channelId);

        return new SignedTransaction(tx.GetHash().ToBytes(), tx.ToBytes());
    }

    /// <inheritdoc />
    public Secret RevealPerCommitmentSecret(ChannelId channelId, ulong commitmentNumber)
    {
        var signingInfo = GetRegisteredSigningInfo(channelId);

        // NL-189: never reveal the secret of a commitment that has not been superseded by a persisted one
        var localCommitmentNumber = _localCommitmentNumbers.GetValueOrDefault(channelId);
        if (commitmentNumber >= localCommitmentNumber)
            throw new SignerException(
                $"Refusing to reveal the per-commitment secret of unrevoked commitment {commitmentNumber} "
              + $"(current local commitment is {localCommitmentNumber})", channelId, "Internal error");

        // S1: the secret of a commitment signed for broadcast (and of any later one) is never released
        if (_broadcastSignedNumbers.TryGetValue(channelId, out var broadcastNumber) && commitmentNumber >= broadcastNumber)
            throw new SignerException(
                $"Refusing to reveal the per-commitment secret of commitment {commitmentNumber}: local commitment "
              + $"{broadcastNumber} is signed for broadcast", channelId, "Internal error");

        return DerivePerCommitmentSecret(signingInfo.ChannelKeyIndex, commitmentNumber);
    }

    /// <inheritdoc />
    public void AdvanceLocalCommitment(ChannelId channelId, ulong newLocalCommitmentNumber)
    {
        if (newLocalCommitmentNumber > CommitmentNumber.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(newLocalCommitmentNumber), newLocalCommitmentNumber,
                                                  "Commitment numbers are 48-bit values");

        _ = GetRegisteredSigningInfo(channelId);

        // S1: a newer local commitment would make the broadcast one revocable
        if (_broadcastSignedNumbers.TryGetValue(channelId, out var broadcastNumber)
         && newLocalCommitmentNumber > broadcastNumber)
            throw new SignerException(
                $"Refusing to advance the local commitment to {newLocalCommitmentNumber}: local commitment "
              + $"{broadcastNumber} is signed for broadcast", channelId, "Internal error");

        while (true)
        {
            var current = _localCommitmentNumbers.GetValueOrDefault(channelId);
            if (newLocalCommitmentNumber < current)
                throw new SignerException(
                    $"Local commitment number cannot go back from {current} to {newLocalCommitmentNumber}", channelId,
                    "Internal error");

            if (newLocalCommitmentNumber == current
             || _localCommitmentNumbers.TryUpdate(channelId, newLocalCommitmentNumber, current))
                return;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<CompactSignature> SignRemoteHtlcTransactions(
        ChannelId channelId, IReadOnlyList<HtlcSigningContext> htlcTransactions)
    {
        ArgumentNullException.ThrowIfNull(htlcTransactions);
        var signingInfo = GetRegisteredSigningInfo(channelId);
        ThrowIfDataLoss(channelId, "sign HTLC transactions of a new commitment");
        ThrowIfBroadcastSigned(channelId, "sign HTLC transactions of a new commitment");

        if (htlcTransactions.Count == 0)
            return [];

        using var htlcBasepointSecret = GetHtlcBasepointSecret(signingInfo.ChannelKeyIndex);
        var signatures = new List<CompactSignature>(htlcTransactions.Count);
        foreach (var context in htlcTransactions)
        {
            // We are the counterparty of this HTLC transaction: SINGLE|ANYONECANPAY with anchors (BOLT 3)
            signatures.Add(SignHtlcTransaction(htlcBasepointSecret, context,
                                               GetCounterpartyHtlcSigHash(context.HasAnchors)));
        }

        return signatures;
    }

    /// <inheritdoc />
    public void ValidateLocalHtlcSignatures(ChannelId channelId, IReadOnlyList<HtlcSigningContext> htlcTransactions,
                                            IReadOnlyList<CompactSignature> signatures)
    {
        ArgumentNullException.ThrowIfNull(htlcTransactions);
        ArgumentNullException.ThrowIfNull(signatures);
        var signingInfo = GetRegisteredSigningInfo(channelId);

        // BOLT 2: htlc_signatures holds exactly one signature per HTLC output of the new commitment
        if (signatures.Count != htlcTransactions.Count)
            throw new SignerException(
                $"Expected {htlcTransactions.Count} HTLC signatures but received {signatures.Count}", channelId,
                "Wrong number of htlc_signatures");

        if (htlcTransactions.Count == 0)
            return;

        if (signingInfo.RemoteHtlcBasepoint is null)
            throw new SignerException("The remote htlc_basepoint is not known", channelId, "Internal error");

        for (var i = 0; i < htlcTransactions.Count; i++)
        {
            var context = htlcTransactions[i];
            var signature = ParseLowSSignature(channelId, signatures[i], i);

            // The peer's HTLC key for our commitment: remote_htlc_basepoint tweaked by our per-commitment point
            var remoteHtlcPubKey = _keyDerivationService.DerivePublicKey(signingInfo.RemoteHtlcBasepoint.Value,
                                                                         context.PerCommitmentPoint);
            var sigHash = ComputeHtlcSigHash(context, GetCounterpartyHtlcSigHash(context.HasAnchors));

            if (!new PubKey(remoteHtlcPubKey).Verify(sigHash, signature))
                throw new SignerException($"HTLC signature {i} is invalid", channelId,
                                          "Invalid htlc_signature provided");
        }
    }

    /// <inheritdoc />
    public CompactSignature SignLocalHtlcTransaction(ChannelId channelId, HtlcSigningContext htlcTransaction)
    {
        ArgumentNullException.ThrowIfNull(htlcTransaction);
        var signingInfo = GetRegisteredSigningInfo(channelId);
        ThrowIfDataLoss(channelId, "sign our HTLC transaction");

        // The holder's own signature on its HTLC transaction is always SIGHASH_ALL
        using var htlcBasepointSecret = GetHtlcBasepointSecret(signingInfo.ChannelKeyIndex);
        return SignHtlcTransaction(htlcBasepointSecret, htlcTransaction, SigHash.All);
    }

    public bool SignWalletTransaction(SignedTransaction unsignedTransaction)
    {
        throw new NotImplementedException();
    }

    public bool SignFundingTransaction(ChannelId channelId, SignedTransaction unsignedTransaction)
    {
        _logger.LogTrace("Signing funding transaction for channel {ChannelId} with TxId {TxId}", channelId,
                         unsignedTransaction.TxId);

        if (!_channelSigningInfo.TryGetValue(channelId, out var signingInfo))
            throw new SignerException($"Channel {channelId} not registered with signer", channelId);

        Transaction nBitcoinTx;
        try
        {
            nBitcoinTx = Transaction.Load(unsignedTransaction.RawTxBytes, _network);
        }
        catch (Exception ex)
        {
            throw new ArgumentException(
                $"Failed to load transaction from RawTxBytes. TxId hint: {unsignedTransaction.TxId}", ex);
        }

        try
        {
            // Verify the funding output exists and is correct
            if (signingInfo.FundingOutputIndex >= nBitcoinTx.Outputs.Count)
                throw new SignerException($"Funding output index {signingInfo.FundingOutputIndex} is out of range",
                                          channelId);

            // Build the funding output using the channel's signing info
            var fundingOutputInfo = new FundingOutputInfo(signingInfo.FundingSatoshis, signingInfo.LocalFundingPubKey,
                                                          signingInfo.RemoteFundingPubKey, signingInfo.FundingTxId,
                                                          signingInfo.FundingOutputIndex);

            var expectedFundingOutput = _fundingOutputBuilder.Build(fundingOutputInfo);
            var expectedTxOut = expectedFundingOutput.ToTxOut();

            // Validate the transaction output matches what we expect
            var actualTxOut = nBitcoinTx.Outputs[signingInfo.FundingOutputIndex];
            if (!actualTxOut.ToBytes().SequenceEqual(expectedTxOut.ToBytes()))
                throw new SignerException("Funding output script does not match expected script", channelId);

            if (actualTxOut.Value != expectedTxOut.Value)
                throw new SignerException(
                    $"Funding output amount {actualTxOut.Value} does not match expected amount {expectedTxOut.Value}",
                    channelId);

            _logger.LogDebug("Funding output validation passed for channel {ChannelId}", channelId);

            // Check transaction structure
            if (nBitcoinTx.Inputs.Count == 0)
                throw new SignerException("Funding transaction has no inputs", channelId);

            // Get the utxoSet for the channel
            var utxoModels = _utxoMemoryRepository.GetLockedUtxosForChannel(channelId);

            var signedInputCount = 0;
            var prevOuts = new TxOut[nBitcoinTx.Inputs.Count];
            var signingKeys = new Key?[nBitcoinTx.Inputs.Count];
            var taprootKeyPairs = new TaprootKeyPair?[nBitcoinTx.Inputs.Count];
            var utxos = new UtxoModel[nBitcoinTx.Inputs.Count];

            // Sign each input
            for (var i = 0; i < nBitcoinTx.Inputs.Count; i++)
            {
                var input = nBitcoinTx.Inputs[i];

                // Try to get the address being spent
                var utxo = utxoModels.FirstOrDefault(x => x.TxId.Equals(new TxId(input.PrevOut.Hash.ToBytes()))
                                                       && x.Index.Equals(input.PrevOut.N));
                if (utxo is null)
                {
                    _logger.LogWarning("Could not find UTXO for input {InputIndex} in funding transaction", i);
                    continue;
                }

                if (utxo.WalletAddress is null)
                {
                    _logger.LogWarning(
                        "UTXO did not have a WalletAddress for input {InputIndex} in funding transaction", i);
                    continue;
                }

                utxos[i] = utxo;

                try
                {
                    // Create the scriptPubKey and previous output based on the address type
                    Script scriptPubKey;
                    ExtPrivKey signingExtKey;
                    Key? signingKey = null;
                    TaprootKeyPair? taprootKeyPair = null;

                    switch (utxo.AddressType)
                    {
                        case AddressType.P2Wpkh:
                            // Derive the key for this specific UTXO
                            signingExtKey =
                                _secureKeyManager.GetDepositP2WpkhKeyAtIndex(
                                    utxo.WalletAddress.Index, utxo.WalletAddress.IsChange);
                            signingKey = ExtKey.CreateFromBytes(signingExtKey).PrivateKey;
                            // For P2WPKH: OP_0 <20-byte-pubkey-hash>
                            scriptPubKey = signingKey.PubKey.WitHash.ScriptPubKey;
                            break;

                        case AddressType.P2Tr:
                            // Derive the key for this specific UTXO
                            signingExtKey =
                                _secureKeyManager.GetDepositP2TrKeyAtIndex(
                                    utxo.WalletAddress.Index, utxo.WalletAddress.IsChange);
                            var rootKey = ExtKey.CreateFromBytes(signingExtKey).PrivateKey;
                            // For P2TR (Taproot): OP_1 <32-byte-taproot-output>
                            taprootKeyPair = rootKey.CreateTaprootKeyPair();
                            scriptPubKey = taprootKeyPair.PubKey.ScriptPubKey;
                            break;

                        default:
                            throw new SignerException($"Unsupported address type {utxo.AddressType} for input {i}",
                                                      channelId);
                    }

                    signingKeys[i] = signingKey;
                    taprootKeyPairs[i] = taprootKeyPair;
                    prevOuts[i] = new TxOut(new Money(utxo.Amount.Satoshi), scriptPubKey);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to sign input {InputIndex} in funding transaction", i);
                    throw new SignerException(
                        $"Failed to sign input {i}",
                        channelId, ex, "Signing error");
                }
            }

            for (var i = 0; i < nBitcoinTx.Inputs.Count; i++)
            {
                try
                {
                    var utxo = utxos[i];
                    var signingKey = signingKeys[i];
                    var taprootKeyPair = taprootKeyPairs[i];
                    var prevOut = prevOuts[i];

                    switch (utxo.AddressType)
                    {
                        // Sign based on the address type
                        case AddressType.P2Wpkh:
                            if (signingKey is null)
                                throw new SignerException($"Missing signing key for P2WPKH input {i}", channelId);

                            // Sign P2WPKH input
                            SignP2WpkhInput(nBitcoinTx, i, signingKey, prevOut);
                            break;
                        case AddressType.P2Tr:
                            if (taprootKeyPair is null)
                                throw new SignerException($"Missing taproot key pair for P2TR input {i}", channelId);

                            // Sign P2TR (Taproot) input - key path spend
                            SignP2TrInput(nBitcoinTx, i, taprootKeyPair, prevOuts);
                            break;
                        default:
                            throw new SignerException($"Unsupported address type {utxo.AddressType} for input {i}",
                                                      channelId);
                    }

                    signedInputCount++;

                    _logger.LogTrace("Signed input {InputIndex} for funding transaction", i);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to sign input {InputIndex} in funding transaction", i);
                    throw new SignerException(
                        $"Failed to sign input {i}",
                        channelId, ex, "Signing error");
                }
            }

            if (signedInputCount == 0)
                throw new SignerException("No inputs were successfully signed", channelId, "Signing failed");

            // Update the transaction bytes in the SignedTransaction
            unsignedTransaction.RawTxBytes = nBitcoinTx.ToBytes();

            _logger.LogInformation(
                "Successfully signed {SignedCount}/{TotalCount} inputs for funding transaction {TxId}",
                signedInputCount, nBitcoinTx.Inputs.Count, nBitcoinTx.GetHash());

            return signedInputCount == nBitcoinTx.Inputs.Count;
        }
        catch (SignerException)
        {
            throw;
        }
        catch (Exception e)
        {
            throw new SignerException($"Exception during funding transaction signing for TxId {nBitcoinTx.GetHash()}",
                                      channelId, e);
        }
    }

    /// <inheritdoc />
    public CompactSignature SignChannelTransaction(ChannelId channelId, SignedTransaction unsignedTransaction)
    {
        if (_logger.IsEnabled(LogLevel.Trace))
            _logger.LogTrace("Signing transaction for channel {ChannelId} with TxId {TxId}", channelId,
                             unsignedTransaction.TxId);

        if (!_channelSigningInfo.TryGetValue(channelId, out var signingInfo))
            throw new InvalidOperationException($"Channel {channelId} not registered with signer");

        ThrowIfDataLoss(channelId, "sign a commitment");
        ThrowIfBroadcastSigned(channelId, "sign a channel transaction");

        return SignFundingInput(channelId, signingInfo, unsignedTransaction);
    }

    /// <inheritdoc />
    public void ValidateSignature(ChannelId channelId, CompactSignature signature,
                                  SignedTransaction unsignedTransaction)
    {
        if (_logger.IsEnabled(LogLevel.Trace))
            _logger.LogTrace("Validating signature for channel {ChannelId} with TxId {TxId}", channelId,
                             unsignedTransaction.TxId);

        if (!_channelSigningInfo.TryGetValue(channelId, out var signingInfo))
            throw new SignerException("Channel not registered with signer", channelId, "Internal error");

        Transaction nBitcoinTx;
        try
        {
            nBitcoinTx = Transaction.Load(unsignedTransaction.RawTxBytes, _network);
        }
        catch (Exception e)
        {
            throw new SignerException("Failed to load transaction from RawTxBytes", channelId, e, "Internal error");
        }

        PubKey pubKey;
        try
        {
            pubKey = new PubKey(signingInfo.RemoteFundingPubKey);
        }
        catch (Exception e)
        {
            throw new SignerException("Failed to parse public key from CompactPubKey", channelId, e, "Internal error");
        }

        ECDSASignature txSignature;
        try
        {
            if (!ECDSASignature.TryParseFromCompact(signature, out txSignature))
                throw new SignerException("Failed to parse compact signature", channelId, "Signature format error");

            if (!txSignature.IsLowS)
                throw new SignerException("Signature is not low S", channelId,
                                          "Signature is malleable");
        }
        catch (Exception e)
        {
            throw new SignerException("Failed to parse DER signature", channelId, e,
                                      "Signature format error");
        }

        try
        {
            // Build the funding output using the channel's signing info
            var fundingOutputInfo = new FundingOutputInfo(signingInfo.FundingSatoshis, signingInfo.LocalFundingPubKey,
                                                          signingInfo.RemoteFundingPubKey, signingInfo.FundingTxId,
                                                          signingInfo.FundingOutputIndex);

            var fundingOutput = _fundingOutputBuilder.Build(fundingOutputInfo);
            var spentOutput = fundingOutput.ToTxOut();

            var signatureHash =
                nBitcoinTx.GetSignatureHash(fundingOutput.RedeemScript, 0, SigHash.All, spentOutput,
                                            HashVersion.WitnessV0);

            if (!pubKey.Verify(signatureHash, txSignature))
                throw new SignerException("Peer signature is invalid", channelId, "Invalid signature provided");
        }
        catch (Exception e)
        {
            throw new SignerException("Exception during signature verification", channelId, e,
                                      "Signature verification error");
        }
    }

    protected virtual Key GenerateFundingPrivateKey(uint channelKeyIndex)
    {
        var channelExtKey = _secureKeyManager.GetChannelKeyAtIndex(channelKeyIndex);
        var channelKey = ExtKey.CreateFromBytes(channelExtKey);

        return GenerateFundingPrivateKey(channelKey);
    }

    /// <summary>
    /// The channel's <c>htlc_basepoint_secret</c> (m/4' of the channel key).
    /// </summary>
    protected virtual Key GetHtlcBasepointSecret(uint channelKeyIndex)
    {
        var channelExtKey = _secureKeyManager.GetChannelKeyAtIndex(channelKeyIndex);
        var channelKey = ExtKey.CreateFromBytes(channelExtKey);

        return channelKey.Derive(HtlcDerivationIndex, true).PrivateKey;
    }

    /// <summary>
    /// The channel's <c>revocation_basepoint_secret</c> (m/1' of the channel key).
    /// </summary>
    protected virtual Key GetRevocationBasepointSecret(uint channelKeyIndex) =>
        DeriveChannelBasepointSecret(channelKeyIndex, RevocationDerivationIndex);

    /// <summary>
    /// The channel's <c>payment_basepoint_secret</c> (m/2' of the channel key); with static_remotekey it is also the
    /// key of our <c>to_remote</c> outputs.
    /// </summary>
    protected virtual Key GetPaymentBasepointSecret(uint channelKeyIndex) =>
        DeriveChannelBasepointSecret(channelKeyIndex, PaymentDerivationIndex);

    /// <summary>
    /// The channel's <c>delayed_payment_basepoint_secret</c> (m/3' of the channel key).
    /// </summary>
    protected virtual Key GetDelayedPaymentBasepointSecret(uint channelKeyIndex) =>
        DeriveChannelBasepointSecret(channelKeyIndex, DelayedPaymentDerivationIndex);

    private Key DeriveChannelBasepointSecret(uint channelKeyIndex, int derivationIndex)
    {
        var channelExtKey = _secureKeyManager.GetChannelKeyAtIndex(channelKeyIndex);
        var channelKey = ExtKey.CreateFromBytes(channelExtKey);

        return channelKey.Derive(derivationIndex, true).PrivateKey;
    }

    /// <summary>
    /// The private key of one sweep input (BOLT 3 §Key Derivation), from the channel's basepoint secrets.
    /// </summary>
    private Key DeriveSweepKey(ChannelId channelId, uint channelKeyIndex, SweepSigningContext context)
    {
        switch (context.KeyKind)
        {
            case SweepKeyKind.Payment:
                return GetPaymentBasepointSecret(channelKeyIndex);

            case SweepKeyKind.DelayedPayment:
                {
                    var point = RequirePoint(channelId, context);
                    using var basepointSecret = GetDelayedPaymentBasepointSecret(channelKeyIndex);
                    return CreateAndWipe(_keyDerivationService.DerivePrivateKey(basepointSecret.ToBytes(), point));
                }

            case SweepKeyKind.HtlcRemotePoint:
                {
                    var point = RequirePoint(channelId, context);
                    using var basepointSecret = GetHtlcBasepointSecret(channelKeyIndex);
                    return CreateAndWipe(_keyDerivationService.DerivePrivateKey(basepointSecret.ToBytes(), point));
                }

            case SweepKeyKind.Revocation:
                {
                    if (context.PerCommitmentSecret is not { } secret)
                        throw new SignerException("A revocation spend needs the peer's per-commitment secret", channelId,
                                                  "Internal error");

                    byte[] secretBytes = secret;
                    using var secretKey = TryCreateKey(secretBytes)
                                       ?? throw new SignerException("The per-commitment secret is not a valid key",
                                                                    channelId, "Internal error");
                    if (context.PerCommitmentPoint is { } claimedPoint
                     && !secretKey.PubKey.ToBytes().AsSpan().SequenceEqual((byte[])claimedPoint))
                        throw new SignerException("The per-commitment secret does not match the given point", channelId,
                                                  "Internal error");

                    using var basepointSecret = GetRevocationBasepointSecret(channelKeyIndex);
                    return CreateAndWipe(_keyDerivationService.DeriveRevocationPrivKey(basepointSecret.ToBytes(),
                                                                                       secretBytes));
                }

            default:
                throw new SignerException($"Unknown sweep key kind {context.KeyKind}", channelId, "Internal error");
        }
    }

    private static CompactPubKey RequirePoint(ChannelId channelId, SweepSigningContext context) =>
        context.PerCommitmentPoint
     ?? throw new SignerException($"A {context.KeyKind} spend needs the per-commitment point", channelId,
                                  "Internal error");

    private static Key CreateAndWipe(PrivKey privKey)
    {
        byte[] bytes = privKey;
        try
        {
            return new Key(bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static Key? TryCreateKey(byte[] bytes)
    {
        try
        {
            return new Key(bytes);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// True when <paramref name="script"/> pushes <paramref name="pubKey"/> or its HASH160.
    /// </summary>
    private static bool ScriptCommitsToKey(Script script, PubKey pubKey)
    {
        var keyBytes = pubKey.ToBytes();
        var keyHash = pubKey.Hash.ToBytes();
        foreach (var op in script.ToOps())
        {
            if (op.PushData is not { } data)
                continue;

            if (data.AsSpan().SequenceEqual(keyBytes) || data.AsSpan().SequenceEqual(keyHash))
                return true;
        }

        return false;
    }

    /// <summary>
    /// The per-commitment secret of commitment <paramref name="commitmentNumber"/>. Private on purpose: the only way
    /// out of the signer is <see cref="RevealPerCommitmentSecret"/>, which enforces the revocation guard (NL-189).
    /// </summary>
    private Secret DerivePerCommitmentSecret(uint channelKeyIndex, ulong commitmentNumber)
    {
        // Derive the per-commitment seed from the channel key
        var channelExtKey = _secureKeyManager.GetChannelKeyAtIndex(channelKeyIndex);
        var channelKey = ExtKey.CreateFromBytes(channelExtKey);
        using var perCommitmentSeed = channelKey.Derive(PerCommitmentSeedDerivationIndex, true).PrivateKey;

        // BOLT 3: commitment n uses the per-commitment secret at index 2^48-1-n (NL-187)
        return _keyDerivationService.GeneratePerCommitmentSecret(
            perCommitmentSeed.ToBytes(), PerCommitmentIndex.From(commitmentNumber));
    }

    private ChannelSigningInfo GetRegisteredSigningInfo(ChannelId channelId)
    {
        if (!_channelSigningInfo.TryGetValue(channelId, out var signingInfo))
            throw new SignerException($"Channel {channelId} not registered with signer", channelId, "Internal error");

        return signingInfo;
    }

    private void ThrowIfDataLoss(ChannelId channelId, string what)
    {
        if (_dataLossChannels.ContainsKey(channelId))
            throw new SignerException($"Refusing to {what}: data loss was detected on the channel", channelId,
                                      "Internal error");
    }

    private void ThrowIfBroadcastSigned(ChannelId channelId, string what)
    {
        if (_broadcastSignedNumbers.TryGetValue(channelId, out var broadcastNumber))
            throw new SignerException(
                $"Refusing to {what}: local commitment {broadcastNumber} is signed for broadcast", channelId,
                "Internal error");
    }

    /// <summary>
    /// Our funding-key signature (<c>SIGHASH_ALL</c>, input 0) of a transaction spending the funding output, without
    /// the guards of the public entry points.
    /// </summary>
    private CompactSignature SignFundingInput(ChannelId channelId, ChannelSigningInfo signingInfo,
                                              SignedTransaction unsignedTransaction)
    {
        Transaction nBitcoinTx;
        try
        {
            nBitcoinTx = Transaction.Load(unsignedTransaction.RawTxBytes, _network);
        }
        catch (Exception ex)
        {
            throw new ArgumentException(
                $"Failed to load transaction from RawTxBytes. TxId hint: {unsignedTransaction.TxId}", ex);
        }

        try
        {
            // Build the funding output using the channel's signing info
            var fundingOutputInfo = new FundingOutputInfo(signingInfo.FundingSatoshis, signingInfo.LocalFundingPubKey,
                                                          signingInfo.RemoteFundingPubKey, signingInfo.FundingTxId,
                                                          signingInfo.FundingOutputIndex);

            var fundingOutput = _fundingOutputBuilder.Build(fundingOutputInfo);
            var spentOutput = fundingOutput.ToTxOut();

            // Get the signature hash for SegWit
            var signatureHash = nBitcoinTx.GetSignatureHash(fundingOutput.RedeemScript, 0, SigHash.All, spentOutput,
                                                            HashVersion.WitnessV0);

            // Get the funding private key
            using var fundingPrivateKey = GenerateFundingPrivateKey(signingInfo.ChannelKeyIndex);

            var signature = fundingPrivateKey.Sign(signatureHash, new SigningOptions(SigHash.All, false));

            return signature.Signature.MakeCanonical().ToCompact();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Exception during signature verification for TxId {nBitcoinTx.GetHash()} of channel {channelId}", ex);
        }
    }

    /// <summary>
    /// True when <paramref name="pubKey"/> is the first key of the 2-of-2 funding script
    /// (<c>OP_2 &lt;pubkey1&gt; &lt;pubkey2&gt; OP_2 OP_CHECKMULTISIG</c>).
    /// </summary>
    private static bool IsFirstFundingKey(Script fundingScript, CompactPubKey pubKey)
    {
        var ops = fundingScript.ToOps().ToList();
        if (ops.Count != 5 || ops[1].PushData is null)
            throw new InvalidOperationException("The funding script is not a 2-of-2 multisig");

        return ops[1].PushData.AsSpan().SequenceEqual((byte[])pubKey);
    }

    private static SigHash GetCounterpartyHtlcSigHash(bool hasAnchors) =>
        hasAnchors ? SigHash.Single | SigHash.AnyoneCanPay : SigHash.All;

    private CompactSignature SignHtlcTransaction(Key htlcBasepointSecret, HtlcSigningContext context, SigHash sigHash)
    {
        // BOLT 3: htlcprivkey = htlc_basepoint_secret + SHA256(per_commitment_point || htlc_basepoint)
        var htlcPrivKey = _keyDerivationService.DerivePrivateKey(htlcBasepointSecret.ToBytes(),
                                                                 context.PerCommitmentPoint);
        using var htlcKey = new Key(htlcPrivKey);

        // RFC 6979 without low-R grinding, like SignChannelTransaction; the sighash flag is added by the tx builder
        var signature = htlcKey.Sign(ComputeHtlcSigHash(context, sigHash), new SigningOptions(sigHash, false));
        return signature.Signature.MakeCanonical().ToCompact();
    }

    private uint256 ComputeHtlcSigHash(HtlcSigningContext context, SigHash sigHash)
    {
        var built = context.HtlcTransaction;
        var tx = Transaction.Load(built.Transaction.RawTxBytes, _network);
        if (tx.Inputs.Count != 1)
            throw new ArgumentException("An HTLC transaction has exactly one input", nameof(context));

        var witnessScript = new Script((byte[])built.SpentWitnessScript);
        var spentOutput = new TxOut(Money.Satoshis(built.SpentAmount.Satoshi), witnessScript.WitHash.ScriptPubKey);
        return tx.GetSignatureHash(witnessScript, 0, sigHash, spentOutput, HashVersion.WitnessV0);
    }

    private static ECDSASignature ParseLowSSignature(ChannelId channelId, CompactSignature signature, int index)
    {
        if (!ECDSASignature.TryParseFromCompact(signature, out var ecdsaSignature))
            throw new SignerException($"HTLC signature {index} is not a valid compact signature", channelId,
                                      "Signature format error");

        if (!ecdsaSignature.IsLowS)
            throw new SignerException($"HTLC signature {index} is not low S", channelId, "Signature is malleable");

        return ecdsaSignature;
    }

    private static Key GenerateFundingPrivateKey(ExtKey extKey)
    {
        return extKey.Derive(FundingDerivationIndex, true).PrivateKey;
    }

    /// <summary>
    /// Sign a P2WPKH (Pay-to-Witness-PubKey-Hash) input
    /// </summary>
    private static void SignP2WpkhInput(Transaction tx, int inputIndex, Key signingKey, TxOut prevOut)
    {
        // For P2WPKH, the scriptCode is the P2PKH script: OP_DUP OP_HASH160 <pubkeyhash> OP_EQUALVERIFY OP_CHECKSIG
        var scriptCode = signingKey.PubKey.Hash.ScriptPubKey;

        // Get the signature hash for SegWit v0
        var sigHash =
            tx.GetSignatureHash(scriptCode, inputIndex, SigHash.All, prevOut, HashVersion.WitnessV0);

        // Sign the hash
        var transactionSignature = signingKey.Sign(sigHash, new SigningOptions(SigHash.All, false));

        // For P2WPKH, witness is: <signature> <pubkey>
        var witness = new WitScript(
            Op.GetPushOp(transactionSignature.ToBytes()),
            Op.GetPushOp(signingKey.PubKey.ToBytes()));

        tx.Inputs[inputIndex].WitScript = witness;
    }

    /// <summary>
    /// Sign a P2TR (Pay-to-Taproot) input using the key path spend
    /// </summary>
    /// <remarks>For Taproot, we use BIP341 signing</remarks>
    private static void SignP2TrInput(Transaction tx, int inputIndex, TaprootKeyPair taprootKeyPair, TxOut[] prevOuts)
    {
        // Create the TaprootExecutionData
        var taprootExecutionData = new TaprootExecutionData(inputIndex)
        {
            SigHash = TaprootSigHash.All
        };

        // Calculate the signature hash using Taproot rules (BIP341)
        var sigHash = tx.GetSignatureHashTaproot(prevOuts.ToArray(), taprootExecutionData);

        // Sign with Schnorr signature (BIP340)
        var taprootSignature = taprootKeyPair.SignTaprootKeySpend(sigHash, TaprootSigHash.All);

        // For key path spend, witness is just: <signature>
        tx.Inputs[inputIndex].WitScript = new WitScript(Op.GetPushOp(taprootSignature.ToBytes()));
    }
}