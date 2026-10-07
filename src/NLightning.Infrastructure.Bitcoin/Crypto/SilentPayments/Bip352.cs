using System.Buffers.Binary;
using System.Security.Cryptography;
using NBitcoin.Secp256k1;

namespace NLightning.Infrastructure.Bitcoin.Crypto.SilentPayments;

using Contexts;
using Domain.Bitcoin.SilentPayments.Models;
using Domain.Crypto.ValueObjects;
using Musig2;

/// <summary>BIP 352 1.1.1. Secret multiplication uses blinded MultGen or constant-time ECDH.</summary>
internal static class Bip352
{
    public const int MaxRecipients = 2323;
    private static Context Ctx => NLightningCryptoContext.Instance;

    public static bool IsValidPoint(ReadOnlySpan<byte> key) => TryPoint(key, out _);

    public static byte[] IndividualPublicKey(ReadOnlySpan<byte> secret)
    {
        var scalar = CheckedScalar(secret);
        try { return Compressed(Ctx.EcMultGenContext.MultGen(scalar).ToGroupElement()); }
        finally { Scalar.Clear(ref scalar); }
    }

    public static bool TrySumPublicKeys(IReadOnlyList<CompactPubKey> keys, out CompactPubKey sum)
    {
        sum = default;
        var aggregate = GEJ.Infinity;
        foreach (var key in keys)
        {
            if (!TryPoint(key, out var point)) return false;
            aggregate = aggregate.AddVariable(point);
        }
        if (aggregate.IsInfinity) return false;
        sum = new CompactPubKey(Compressed(aggregate.ToGroupElementVariable()));
        return true;
    }

    public static byte[] ComputeInputHash(ReadOnlySpan<byte> outpoint, ReadOnlySpan<byte> sum)
    {
        if (outpoint.Length != 36 || !IsValidPoint(sum))
            throw new ArgumentException("A serialized outpoint and a valid compressed aggregate key are required.");
        var hash = new byte[32];
        using var sha = WipingSha256.CreateTagged("BIP0352/Inputs");
        sha.Write(outpoint);
        sha.Write(sum);
        sha.GetHash(hash);
        var scalar = CheckedScalar(hash);
        Scalar.Clear(ref scalar);
        return hash;
    }

    public static byte[] TweakInputPublicKey(ReadOnlySpan<byte> sum, ReadOnlySpan<byte> inputHash)
    {
        var point = Point(sum);
        var scalar = CheckedScalar(inputHash);
        try { return Compressed(Ctx.EcMultContext.Mult(point.ToGroupElementJacobian(), scalar, null).ToGroupElementVariable()); }
        finally { Scalar.Clear(ref scalar); }
    }

    /// <summary>Unhashed ECDH. The caller owns and wipes the returned point.</summary>
    public static byte[] ComputeSharedSecret(ReadOnlySpan<byte> point, ReadOnlySpan<byte> secret)
    {
        if (!ECPubKey.TryCreate(point, Ctx, out var compressed, out var publicKey) || !compressed || publicKey is null)
            throw new ArgumentException("Invalid compressed public key.");
        using var privateKey = ECPrivKey.Create(secret, Ctx);
        var result = new byte[33];
        publicKey.GetSharedPubkey(privateKey).WriteToSpan(true, result, out _);
        return result;
    }

    public static void AggregateSenderSecret(IReadOnlyList<SilentPaymentSenderInput> inputs, Span<byte> destination)
    {
        if (destination.Length != 32) throw new ArgumentException("A 32-byte destination is required.");
        destination.Clear();
        var aggregate = Scalar.Zero;
        var scalar = Scalar.Zero;
        try
        {
            foreach (var input in inputs)
            {
                if (input.Outpoint36.Length != 36) throw new ArgumentException("Invalid serialized outpoint.");
                if (input.PrivateKey32 is null) continue;
                scalar = CheckedScalar(input.PrivateKey32);
                if (input.IsTaproot)
                {
                    var point = Ctx.EcMultGenContext.MultGen(scalar).ToGroupElement();
                    scalar = scalar.CondNegate(point.y.Normalize().IsOdd);
                }
                aggregate = aggregate.Add(scalar);
                Scalar.Clear(ref scalar);
            }
            // Intermediate infinity is valid; only the final sum is checked.
            if (aggregate.IsZero) throw new ArgumentException("The eligible input secret-key sum is zero.");
            aggregate.WriteToSpan(destination);
        }
        finally { Scalar.Clear(ref scalar); Scalar.Clear(ref aggregate); }
    }

    public static IReadOnlyList<SilentPaymentDerivedOutput> DeriveOutputs(
        IReadOnlyList<SilentPaymentSenderInput> inputs, IReadOnlyList<SilentPaymentRecipient> recipients)
    {
        if (inputs.Count == 0 || recipients.Count == 0) throw new ArgumentException("Inputs and recipients are required.");
        var groups = recipients.Select((recipient, index) => (recipient, index)).GroupBy(x => x.recipient.ScanKey).ToList();
        if (groups.Any(group => group.Count() > MaxRecipients))
            throw new ArgumentException("A scan-key group exceeds the BIP 352 recipient limit.");
        Span<byte> aggregate = stackalloc byte[32];
        Span<byte> scaled = stackalloc byte[32];
        var a = Scalar.Zero;
        var h = Scalar.Zero;
        var product = Scalar.Zero;
        var results = new List<SilentPaymentDerivedOutput>(recipients.Count);
        try
        {
            AggregateSenderSecret(inputs, aggregate);
            var smallest = inputs[0].Outpoint36;
            foreach (var input in inputs)
                if (input.Outpoint36.AsSpan().SequenceCompareTo(smallest) < 0) smallest = input.Outpoint36;
            var hash = ComputeInputHash(smallest, IndividualPublicKey(aggregate));
            a = CheckedScalar(aggregate);
            h = CheckedScalar(hash);
            product = a.Multiply(h);
            product.WriteToSpan(scaled);
            foreach (var group in groups)
            {
                var shared = ComputeSharedSecret(group.Key, scaled);
                try
                {
                    uint k = 0;
                    foreach (var (recipient, index) in group)
                    {
                        var tweak = SharedSecretTweak(shared, k++);
                        try
                        {
                            var point = AddTweak(Point(recipient.SpendKey), tweak);
                            results.Add(new SilentPaymentDerivedOutput(index, XOnly(point), tweak.ToArray()));
                        }
                        finally { CryptographicOperations.ZeroMemory(tweak); }
                    }
                }
                finally { CryptographicOperations.ZeroMemory(shared); }
            }
            return results.OrderBy(output => output.RecipientIndex).ToArray();
        }
        catch
        {
            foreach (var output in results) CryptographicOperations.ZeroMemory(output.Tweak32);
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(aggregate);
            CryptographicOperations.ZeroMemory(scaled);
            Scalar.Clear(ref a); Scalar.Clear(ref h); Scalar.Clear(ref product);
        }
    }

    public static byte[] SharedSecretTweak(ReadOnlySpan<byte> sharedSecret, uint index)
    {
        if (!IsValidPoint(sharedSecret)) throw new ArgumentException("Invalid shared-secret point.");
        return HashScalar("BIP0352/SharedSecret", sharedSecret, index);
    }

    public static byte[] ComputeLabelTweak(ReadOnlySpan<byte> scanSecret, uint label)
    {
        var scalar = CheckedScalar(scanSecret);
        Scalar.Clear(ref scalar);
        return HashScalar("BIP0352/Label", scanSecret, label, requireValidScalar: false);
    }

    public static byte[] AddPublicTweak(ReadOnlySpan<byte> key, ReadOnlySpan<byte> tweak) => Compressed(AddTweak(Point(key), tweak));

    public static IReadOnlyList<SilentPaymentScanMatch> Scan(ReadOnlySpan<byte> sharedSecret, CompactPubKey spendKey,
        IReadOnlyList<SilentPaymentScanCandidate> candidates, IReadOnlyDictionary<uint, CompactPubKey>? labelPoints = null)
    {
        var spend = Point(spendKey);
        var labels = new Dictionary<string, uint>(StringComparer.Ordinal);
        if (labelPoints is not null)
            foreach (var (label, point) in labelPoints)
            {
                _ = Point(point);
                if (!labels.TryAdd(Convert.ToHexString((byte[])point), label))
                    throw new ArgumentException("Duplicate label points.");
            }
        var remaining = new List<(SilentPaymentScanCandidate candidate, GE point)>();
        foreach (var candidate in candidates)
        {
            if (candidate.OutputKey32.Length != 32) throw new ArgumentException("Invalid output-key length.");
            if (ECXOnlyPubKey.TryCreate(candidate.OutputKey32, Ctx, out var key) && key is not null)
                remaining.Add((candidate, key.Q));
        }
        if (remaining.Select(x => x.candidate.OutputIndex).Distinct().Count() != remaining.Count)
            throw new ArgumentException("Duplicate output indexes.");
        var matches = new List<SilentPaymentScanMatch>();
        try
        {
            for (uint k = 0; k < MaxRecipients && remaining.Count != 0; k++)
            {
                var tweak = SharedSecretTweak(sharedSecret, k);
                try
                {
                    var expected = AddTweak(spend, tweak);
                    var expectedX = XOnly(expected);
                    // Check the unlabelled key first. Avoid point subtraction for every unrelated output when a plain
                    // receipt exists later in the transaction (important for the K_max case).
                    var plainIndex = remaining.FindIndex(row => expectedX.AsSpan().SequenceEqual(row.candidate.OutputKey32));
                    if (plainIndex >= 0)
                    {
                        var plain = remaining[plainIndex].candidate;
                        matches.Add(new SilentPaymentScanMatch(plain.OutputIndex, plain.OutputKey32.ToArray(), tweak.ToArray(), null));
                        remaining.RemoveAt(plainIndex);
                        continue;
                    }
                    var found = false;
                    for (var i = 0; i < remaining.Count; i++)
                    {
                        var (candidate, output) = remaining[i];
                        uint? matchedLabel = null;
                        var matchesPlain = expectedX.AsSpan().SequenceEqual(candidate.OutputKey32);
                        var matchesLabel = false;
                        if (!matchesPlain && labels.Count != 0)
                        {
                            for (var parity = 0; parity < 2; parity++)
                            {
                                var difference = (parity == 0 ? output : output.Negate()).ToGroupElementJacobian()
                                    .AddVariable(expected.Negate());
                                if (!difference.IsInfinity && labels.TryGetValue(
                                        Convert.ToHexString(Compressed(difference.ToGroupElementVariable())), out var label))
                                { matchedLabel = label; matchesLabel = true; break; }
                            }
                        }
                        if (!matchesPlain && !matchesLabel) continue;
                        matches.Add(new SilentPaymentScanMatch(candidate.OutputIndex, candidate.OutputKey32.ToArray(),
                                                               tweak.ToArray(), matchedLabel));
                        remaining.RemoveAt(i);
                        found = true;
                        break;
                    }
                    if (!found) break;
                }
                finally { CryptographicOperations.ZeroMemory(tweak); }
            }
            return matches;
        }
        catch
        {
            foreach (var match in matches) CryptographicOperations.ZeroMemory(match.Tweak32);
            throw;
        }
    }

    public static byte[] DeriveSpendPrivateKey(ReadOnlySpan<byte> spendSecret, ReadOnlySpan<byte> tweak,
                                              ReadOnlySpan<byte> labelTweak = default)
    {
        var secret = CheckedScalar(spendSecret);
        var t = Scalar.Zero;
        var label = Scalar.Zero;
        try
        {
            t = CheckedScalar(tweak);
            if (!labelTweak.IsEmpty)
            {
                if (labelTweak.Length != 32) throw new ArgumentException("A 32-byte label tweak is required.");
                // BIP 352 labels are integer tweaks reduced modulo n. Unlike input_hash and t_k, zero and >= n
                // are allowed for this optional addend.
                label = new Scalar(labelTweak, out _);
            }
            secret = secret.Add(t).Add(label);
            if (secret.IsZero) throw new ArgumentException("Derived spend key is zero.");
            var point = Ctx.EcMultGenContext.MultGen(secret).ToGroupElement();
            secret = secret.CondNegate(point.y.Normalize().IsOdd);
            var result = new byte[32];
            secret.WriteToSpan(result);
            return result;
        }
        finally { Scalar.Clear(ref secret); Scalar.Clear(ref t); Scalar.Clear(ref label); }
    }

    public static byte[] SignSpend(ReadOnlySpan<byte> spendSecret, ReadOnlySpan<byte> tweak,
                                  ReadOnlySpan<byte> labelTweak, ReadOnlySpan<byte> message, ReadOnlySpan<byte> auxiliary)
    {
        var secret = DeriveSpendPrivateKey(spendSecret, tweak, labelTweak);
        byte[]? auxiliaryCopy = null;
        try
        {
            auxiliaryCopy = auxiliary.ToArray();
            using var key = ECPrivKey.Create(secret, Ctx);
            var signature = key.SignBIP340(message, auxiliaryCopy);
            var bytes = new byte[64];
            signature.WriteToSpan(bytes);
            return bytes;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
            if (auxiliaryCopy is not null) CryptographicOperations.ZeroMemory(auxiliaryCopy);
        }
    }

    private static byte[] HashScalar(string tag, ReadOnlySpan<byte> secret, uint index, bool requireValidScalar = true)
    {
        Span<byte> encoded = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(encoded, index);
        var result = new byte[32];
        try
        {
            using var sha = WipingSha256.CreateTagged(tag);
            sha.Write(secret); sha.Write(encoded); sha.GetHash(result);
            if (requireValidScalar)
            {
                var scalar = CheckedScalar(result);
                Scalar.Clear(ref scalar);
            }
            return result;
        }
        catch { CryptographicOperations.ZeroMemory(result); throw; }
    }

    private static GE AddTweak(GE point, ReadOnlySpan<byte> tweak)
    {
        var scalar = CheckedScalar(tweak);
        try
        {
            var result = point.ToGroupElementJacobian().AddVariable(Ctx.EcMultGenContext.MultGen(scalar).ToGroupElement());
            if (result.IsInfinity) throw new ArgumentException("Tweaked output is the point at infinity.");
            return result.ToGroupElementVariable();
        }
        finally { Scalar.Clear(ref scalar); }
    }

    private static Scalar CheckedScalar(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != 32) throw new ArgumentException("A 32-byte scalar is required.");
        var scalar = new Scalar(bytes, out var overflow);
        if (overflow != 0 || scalar.IsZero)
        { Scalar.Clear(ref scalar); throw new ArgumentException("Scalar must be in 1..n-1."); }
        return scalar;
    }

    private static bool TryPoint(ReadOnlySpan<byte> bytes, out GE point)
    {
        point = default;
        if (bytes.Length != 33 || (bytes[0] != 2 && bytes[0] != 3)
         || !ECPubKey.TryCreate(bytes, Ctx, out var compressed, out var key) || !compressed || key is null) return false;
        point = key.Q;
        return true;
    }

    private static GE Point(ReadOnlySpan<byte> bytes) => TryPoint(bytes, out var point) ? point
        : throw new ArgumentException("Invalid compressed public key.");

    private static byte[] XOnly(GE point)
    {
        var bytes = new byte[32]; point.x.NormalizeVariable().WriteToSpan(bytes); return bytes;
    }

    private static byte[] Compressed(GE point)
    {
        var bytes = new byte[33];
        bytes[0] = point.y.NormalizeVariable().IsOdd ? (byte)3 : (byte)2;
        point.x.NormalizeVariable().WriteToSpan(bytes.AsSpan(1));
        return bytes;
    }
}