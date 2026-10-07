using System.Buffers.Binary;
using System.Security.Cryptography;
using NBitcoin;
using NBitcoin.Secp256k1;

namespace NLightning.Infrastructure.Bitcoin.Managers;

using Crypto.Contexts;
using Crypto.Musig2;
using Domain.Bitcoin.Constants;
using Domain.Bitcoin.SilentPayments.Interfaces;
using Domain.Crypto.ValueObjects;
using Onion;

public partial class SecureKeyManager : ISilentPaymentKeySource
{
    private readonly object _silentPaymentKeyLock = new();
    private IntPtr _secureScanKeyPtr;
    private ulong _scanKeyLength;
    private byte[]? _scanPubKey;
    private byte[]? _spendPubKey;

    public bool RecoverableElsewhere => DerivationScheme == KeyDerivationScheme.Bip32;

    // CompactPubKey can expose its backing array: return copies rather than mutable cached arrays.
    public CompactPubKey ScanPubKey => GetSilentPaymentPublicKey(isScan: true);
    public CompactPubKey SpendPubKey => GetSilentPaymentPublicKey(isScan: false);

    /// <inheritdoc />
    public void ComputeScanSharedSecret(ReadOnlySpan<byte> tweakedInputKey, Span<byte> point33)
    {
        if (point33.Length != 33)
            throw new ArgumentException("The shared point destination must be 33 bytes.", nameof(point33));
        if (tweakedInputKey.Length != 33
         || !ECPubKey.TryCreate(tweakedInputKey, NLightningCryptoContext.Instance, out var compressed, out var point)
         || !compressed || point is null)
            throw new ArgumentException("The input key must be a compressed secp256k1 point.", nameof(tweakedInputKey));

        lock (_silentPaymentKeyLock)
        {
            var scan = CopyScanPrivateKey();
            try
            {
                using var key = SphinxKeyGenerator.CreatePrivateKey(scan, nameof(scan));
                // GetSharedPubkey uses the constant-time multiplication for the secret scan scalar.
                point.GetSharedPubkey(key).WriteToSpan(true, point33, out _);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(scan);
            }
        }
    }

    /// <inheritdoc />
    public void GetLabelTweak(uint label, Span<byte> scalar32)
    {
        if (scalar32.Length != 32)
            throw new ArgumentException("The label destination must be 32 bytes.", nameof(scalar32));
        lock (_silentPaymentKeyLock)
        {
            var scan = CopyScanPrivateKey();
            Span<byte> index = stackalloc byte[4];
            try
            {
                BinaryPrimitives.WriteUInt32BigEndian(index, label);
                using var hash = WipingSha256.CreateTagged("BIP0352/Label");
                hash.Write(scan);
                hash.Write(index);
                hash.GetHash(scalar32);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(scan);
                CryptographicOperations.ZeroMemory(index);
            }
        }
    }

    /// <inheritdoc />
    public CompactPubKey GetLabelPoint(uint label)
    {
        Span<byte> tweak = stackalloc byte[32];
        var scalar = Scalar.Zero;
        try
        {
            GetLabelTweak(label, tweak);
            // The label is an integer tweak, reduced modulo n (unlike input_hash and t_k).
            scalar = new Scalar(tweak, out _);
            if (scalar.IsZero)
                throw new InvalidOperationException("The silent payment label point is at infinity.");
            scalar.WriteToSpan(tweak);
            using var key = SphinxKeyGenerator.CreatePrivateKey(tweak, nameof(tweak));
            return key.CreatePubKey().ToBytes(true);
        }
        finally
        {
            Scalar.Clear(ref scalar);
            CryptographicOperations.ZeroMemory(tweak);
        }
    }

    /// <summary>
    /// Derives a raw BIP 352 output private key for the local signer. The caller owns and must wipe the returned
    /// scalar. It has no BIP86 tweak; the BIP340 signer handles output-key parity.
    /// </summary>
    public byte[] GetSilentPaymentSpendKey(ReadOnlySpan<byte> tweak32, uint? label)
    {
        if (tweak32.Length != 32)
            throw new ArgumentException("The output tweak must be 32 bytes.", nameof(tweak32));
        var outputTweak = new Scalar(tweak32, out var overflow);
        var spendScalar = Scalar.Zero;
        var labelScalar = Scalar.Zero;
        var sum = Scalar.Zero;
        Span<byte> labelTweak = stackalloc byte[32];
        byte[]? spend = null;
        try
        {
            if (overflow != 0 || outputTweak.IsZero)
                throw new ArgumentException("The output tweak must be in 1..n-1.", nameof(tweak32));
            lock (_silentPaymentKeyLock)
            {
                spend = DeriveSilentPaymentPrivateKey(isScan: false);
                spendScalar = new Scalar(spend, out _);
                if (label is { } index)
                {
                    GetLabelTweak(index, labelTweak);
                    labelScalar = new Scalar(labelTweak, out _);
                }
                sum = spendScalar + outputTweak + labelScalar;
                if (sum.IsZero)
                    throw new InvalidOperationException("The silent payment output private key is zero.");
                return sum.ToBytes();
            }
        }
        finally
        {
            Scalar.Clear(ref outputTweak);
            Scalar.Clear(ref spendScalar);
            Scalar.Clear(ref labelScalar);
            Scalar.Clear(ref sum);
            CryptographicOperations.ZeroMemory(labelTweak);
            if (spend is not null)
                CryptographicOperations.ZeroMemory(spend);
        }
    }

    private CompactPubKey GetSilentPaymentPublicKey(bool isScan)
    {
        lock (_silentPaymentKeyLock)
        {
            if (_secureMasterKeyPtr == IntPtr.Zero)
                throw new ObjectDisposedException(nameof(SecureKeyManager));
            var cached = isScan ? _scanPubKey : _spendPubKey;
            if (cached is null)
            {
                var material = DeriveSilentPaymentPrivateKey(isScan);
                try
                {
                    using var key = SphinxKeyGenerator.CreatePrivateKey(material, nameof(material));
                    cached = key.CreatePubKey().ToBytes(true);
                    if (isScan)
                        _scanPubKey = cached;
                    else
                        _spendPubKey = cached;
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(material);
                }
            }
            return cached.AsSpan();
        }
    }

    private byte[] CopyScanPrivateKey()
    {
        if (_secureMasterKeyPtr == IntPtr.Zero)
            throw new ObjectDisposedException(nameof(SecureKeyManager));
        if (_secureScanKeyPtr == IntPtr.Zero)
        {
            // Receive-off nodes never allocate a persistent scan private key. Initialize only when scanning or
            // constructing a label; both are receiver operations.
            var material = DeriveSilentPaymentPrivateKey(isScan: true);
            try
            {
                _secureScanKeyPtr = AllocateSecure(material, out _scanKeyLength);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(material);
            }
        }
        return CopyFromSecure(_secureScanKeyPtr, _scanKeyLength);
    }

    private byte[] DeriveSilentPaymentPrivateKey(bool isScan)
    {
        if (_secureMasterKeyPtr == IntPtr.Zero)
            throw new ObjectDisposedException(nameof(SecureKeyManager));
        var path = new KeyPath(KeyConstants.GetSilentPaymentKeyPath(_network == Network.Main, isScan));
        var extended = GetMasterKey();
        try
        {
            // ExtKey.Derive(KeyPath) leaves its intermediate private keys to finalization. Derive each element
            // explicitly so both the root and every intermediate secret are released as soon as they are replaced.
            foreach (var index in path.Indexes)
            {
                var child = extended.Derive(index);
                extended.PrivateKey.Dispose();
                extended = child;
            }
            // Return an independent scalar array; the extended key is always disposed before the caller receives it.
            return extended.PrivateKey.ToBytes();
        }
        finally
        {
            extended.PrivateKey.Dispose();
        }
    }
}