using System.Buffers.Binary;
using System.Security.Cryptography;
using NBitcoin;
using NBitcoin.Secp256k1;
using NBitcoin.Crypto;

namespace NLightning.Infrastructure.Bitcoin.Signers;

using Crypto.SilentPayments;
using Crypto.Musig2;
using Domain.Bitcoin.Enums;
using Domain.Bitcoin.SilentPayments;
using Domain.Bitcoin.SilentPayments.Models;
using Domain.Bitcoin.ValueObjects;
using Domain.Bitcoin.Wallet.Models;
using Domain.Exceptions;
using Taproot;

public partial class LocalLightningSigner
{
    /// <inheritdoc />
    public IReadOnlyList<BitcoinScript> ComputeSilentPaymentOutputs(Guid reservationId,
        IReadOnlyList<SilentPaymentAddress> recipients, IReadOnlyList<(TxId TxId, uint Index)> allInputs)
    {
        ArgumentNullException.ThrowIfNull(recipients);
        ArgumentNullException.ThrowIfNull(allInputs);
        if (allInputs.Count == 0 || recipients.Count == 0)
            throw new SignerException("A silent payment needs inputs and recipients.");
        if (allInputs.Distinct().Count() != allInputs.Count)
            throw new SignerException("A silent payment has duplicate input outpoints.");
        var reserved = _utxoMemoryRepository.GetFeeReservedOutpoints(reservationId);
        if (reserved.Count != allInputs.Count || !reserved.ToHashSet().SetEquals(allInputs))
            throw new SignerException("Silent payment inputs must exactly match the frozen fee reservation.");
        if (recipients.Any(address => address.Version != 0
                                  || !Bip352.IsValidPoint(address.ScanKey)
                                  || !Bip352.IsValidPoint(address.SpendKey)))
            throw new SignerException("A silent payment recipient has an unsupported version or invalid public key.");

        var inputs = new List<SilentPaymentSenderInput>(allInputs.Count);
        try
        {
            foreach (var (txId, index) in allInputs)
            {
                if (!_utxoMemoryRepository.TryGetUtxo(txId, index, out var utxo)
                 || utxo.LockedToChannelId is not null
                 || !_utxoMemoryRepository.TryGetFeeReservation(txId, index, out var held)
                 || held != reservationId)
                    throw new SignerException("A silent payment input is no longer held by its reservation.");
                if (utxo.AddressType is not (AddressType.P2Wpkh or AddressType.P2Tr)
                 || (utxo.WalletAddress is null && utxo.SilentPayment is null))
                    throw new SignerException("Silent payments require exclusively wallet-owned eligible input keys.");

                var prevOut = DeriveWalletPrevOut(utxo, out var key, out var taprootPair);
                using (key)
                {
                    if (prevOut.ScriptPubKey != GetWalletAddressScript(utxo, inputs.Count))
                        throw new SignerException("The silent payment input key does not match its recorded output.");
                    byte[]? secret = key!.ToBytes();
                    try
                    {
                        if (taprootPair is not null)
                        {
                            var tweaked = TweakBip86InputKey(key, secret);
                            CryptographicOperations.ZeroMemory(secret);
                            secret = tweaked;
                        }
                        var outpoint = new byte[36];
                        ((byte[])txId).CopyTo(outpoint, 0);
                        BinaryPrimitives.WriteUInt32LittleEndian(outpoint.AsSpan(32), index);
                        inputs.Add(new SilentPaymentSenderInput(outpoint, secret, utxo.AddressType == AddressType.P2Tr));
                        secret = null;
                    }
                    finally
                    {
                        if (secret is not null)
                            CryptographicOperations.ZeroMemory(secret);
                    }
                }
            }

            var outputs = Bip352.DeriveOutputs(inputs, recipients.Select(address =>
                new SilentPaymentRecipient(address.ScanKey, address.SpendKey)).ToArray());
            try
            {
                return outputs.Select(output => (BitcoinScript)RawTaprootScript(output.OutputKey32).ToBytes()).ToArray();
            }
            finally
            {
                foreach (var output in outputs)
                    CryptographicOperations.ZeroMemory(output.Tweak32);
            }
        }
        finally
        {
            foreach (var input in inputs)
                if (input.PrivateKey32 is { } secret)
                    CryptographicOperations.ZeroMemory(secret);
        }
    }

    private static byte[] TweakBip86InputKey(Key key, ReadOnlySpan<byte> secret)
    {
        var scalar = new Scalar(secret, out _);
        var tweakScalar = Scalar.Zero;
        var tweaked = Scalar.Zero;
        Span<byte> tweak = stackalloc byte[32];
        try
        {
            if (key.PubKey.ToBytes()[0] == 3)
            {
                var even = scalar.Negate();
                Scalar.Clear(ref scalar);
                scalar = even;
                Scalar.Clear(ref even);
            }
            using var hash = WipingSha256.CreateTagged("TapTweak");
            hash.Write(key.PubKey.ToBytes().AsSpan(1));
            hash.GetHash(tweak);
            tweakScalar = new Scalar(tweak, out var overflow);
            if (overflow != 0)
                throw new SignerException("The BIP86 taproot tweak is outside the curve order.");
            tweaked = scalar + tweakScalar;
            if (tweaked.IsZero)
                throw new SignerException("The BIP86 output key is zero.");
            return tweaked.ToBytes();
        }
        finally
        {
            Scalar.Clear(ref scalar);
            Scalar.Clear(ref tweakScalar);
            Scalar.Clear(ref tweaked);
            CryptographicOperations.ZeroMemory(tweak);
        }
    }

    private static Script RawTaprootScript(ReadOnlySpan<byte> outputKey)
    {
        if (outputKey.Length != 32)
            throw new SignerException("The silent payment output key must be 32 bytes.");
        var script = new byte[34];
        script[0] = 0x51;
        script[1] = 0x20;
        outputKey.CopyTo(script.AsSpan(2));
        return new Script(script);
    }

    private static void SignRawTaprootInput(Transaction tx, int inputIndex, Key key, TxOut[] prevOuts)
    {
        var execution = new TaprootExecutionData(inputIndex) { SigHash = TaprootSigHash.All };
        var hash = tx.GetSignatureHashTaproot(prevOuts, execution);
        // BIP 352 d is already the output key. Key.SignTaprootKeySpend would apply another TapTweak even when
        // merkleRoot is null. SignBIP340 applies parity normalization but no BIP341 tweak.
        var signature = new TaprootSignature(new SchnorrSignature(TaprootSignatures.Sign(key, hash)), TaprootSigHash.All);
        tx.Inputs[inputIndex].WitScript = new WitScript(Op.GetPushOp(signature.ToBytes()));
    }
}