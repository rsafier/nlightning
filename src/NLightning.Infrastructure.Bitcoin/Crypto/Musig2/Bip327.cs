using System.Buffers.Binary;
using System.Security.Cryptography;
using NBitcoin.Secp256k1;
using TaggedSha256 = NBitcoin.Secp256k1.SHA256;

namespace NLightning.Infrastructure.Bitcoin.Crypto.Musig2;

using Contexts;
using Domain.Crypto.Constants;
using Domain.Crypto.Enums;
using Domain.Exceptions;

/// <summary>
/// BIP 327 (MuSig2 v1.0.0), written line by line after the BIP's reference implementation (<c>reference.py</c>) over
/// NBitcoin.Secp256k1's group and scalar types. Every value is raw bytes, as in the reference, so the official vectors
/// run here unchanged; <see cref="Musig2Service"/> maps the Domain types onto it.
/// </summary>
/// <remarks>
/// <para>
/// Why not <c>NBitcoin.Secp256k1.Musig</c> (decision D-T3, taproot plan T0): its secret nonce cannot be built from the
/// 97-byte BIP 327 encoding (the constructor is internal), its Sign cannot take an aggregate nonce, and its public
/// nonce parser accepts the point at infinity (33 zero bytes) in a signer's nonce, which BIP 327 refuses
/// (<c>det_sign_vectors.json</c> error case 3).
/// </para>
/// <para>
/// Errors follow the reference: an invalid value from a party is a <see cref="MusigInvalidContributionException"/>
/// (its <c>InvalidContributionError</c>, same signer index and contribution), a refused argument a
/// <see cref="MusigException"/> with the reference's <c>ValueError</c> message. Multiplications by a secret scalar go
/// through the constant-time, blinded generator context; the others use the variable-time multiplication.
/// </para>
/// </remarks>
internal static class Bip327
{
    private const int PointLen = CryptoConstants.CompactPubkeyLen;
    private const int ScalarLen = 32;

    private static readonly byte[] s_infinityEncoding = new byte[PointLen];
    private static readonly GE s_generator = Context.Instance.EcMultGenContext.MultGen(Scalar.One).ToGroupElement();

    private static Context Ctx => NLightningCryptoContext.Instance;

    /// <summary>
    /// BIP 327 <c>KeyAggContext</c>: the aggregate point and the accumulated tweak sign and value.
    /// </summary>
    internal readonly record struct KeyAggContext(GE Q, Scalar Gacc, Scalar Tacc);

    /// <summary>
    /// BIP 327 <c>SessionContext</c>: aggnonce, keys, tweaks (with their x-only flags), message.
    /// </summary>
    internal sealed record SessionContext(byte[] AggNonce, IReadOnlyList<byte[]> PubKeys,
                                          IReadOnlyList<(byte[] Tweak, bool IsXOnly)> Tweaks, byte[] Message);

    private readonly record struct SessionValues(GE Q, Scalar Gacc, Scalar Tacc, Scalar B, GE R, Scalar E);

    #region Keys

    /// <summary>
    /// <c>individual_pk</c>: the 33-byte public key of a secret key in 1..n-1.
    /// </summary>
    public static byte[] IndividualPubKey(ReadOnlySpan<byte> secretKey)
    {
        var d = ScalarInRange(secretKey, "The secret key must be an integer in the range 1..n-1.");
        try
        {
            return Cbytes(Ctx.EcMultGenContext.MultGen(d).ToGroupElement());
        }
        finally
        {
            Scalar.Clear(ref d);
        }
    }

    /// <summary>
    /// <c>key_sort</c>: the keys in lexicographic byte order (a new list; the input is left alone).
    /// </summary>
    public static List<byte[]> KeySort(IEnumerable<byte[]> pubKeys)
    {
        var sorted = pubKeys.ToList();
        sorted.Sort((a, b) => a.AsSpan().SequenceCompareTo(b));
        return sorted;
    }

    /// <summary>
    /// <c>key_agg</c>.
    /// </summary>
    public static KeyAggContext KeyAgg(IReadOnlyList<byte[]> pubKeys)
    {
        var pk2 = GetSecondKey(pubKeys);
        var keysHash = HashKeys(pubKeys);
        var q = GEJ.Infinity;
        for (var i = 0; i < pubKeys.Count; i++)
        {
            if (!TryCpoint(pubKeys[i], out var p))
                throw new MusigInvalidContributionException(i, MusigContribution.PubKey);

            var a = KeyAggCoeffInternal(keysHash, pubKeys[i], pk2);
            q = q.AddVariable(Mul(p, a));
        }

        // Q is not the point at infinity except with negligible probability
        if (q.IsInfinity)
            throw new InvalidOperationException("The aggregate public key is the point at infinity.");

        return new KeyAggContext(q.ToGroupElementVariable(), Scalar.One, Scalar.Zero);
    }

    /// <summary>
    /// <c>get_xonly_pk</c>: the 32-byte x coordinate of the aggregate point.
    /// </summary>
    public static byte[] GetXOnlyPubKey(KeyAggContext keyAggContext) => Xbytes(keyAggContext.Q);

    /// <summary>
    /// The 33-byte compressed encoding of the aggregate point.
    /// </summary>
    public static byte[] GetPlainPubKey(KeyAggContext keyAggContext) => Cbytes(keyAggContext.Q);

    /// <summary>
    /// <c>apply_tweak</c>.
    /// </summary>
    public static KeyAggContext ApplyTweak(KeyAggContext keyAggContext, ReadOnlySpan<byte> tweak, bool isXOnly)
    {
        if (tweak.Length != MusigConstants.TweakLen)
            throw new MusigException("The tweak must be a 32-byte array.");

        var (q, gacc, tacc) = keyAggContext;
        var g = isXOnly && !HasEvenY(q) ? Scalar.MinusOne : Scalar.One;
        var t = new Scalar(tweak, out var overflow);
        if (overflow != 0)
            throw new MusigException("The tweak must be less than n.");

        Scalar? tg = t;
        var tweaked = Ctx.EcMultContext.Mult(q.ToGroupElementJacobian(), g, tg);
        if (tweaked.IsInfinity)
            throw new MusigException("The result of tweaking cannot be infinity.");

        return new KeyAggContext(tweaked.ToGroupElementVariable(), g.Multiply(gacc), t.Add(g.Multiply(tacc)));
    }

    /// <summary>
    /// <c>key_agg_and_tweak</c>.
    /// </summary>
    public static KeyAggContext KeyAggAndTweak(IReadOnlyList<byte[]> pubKeys,
                                               IReadOnlyList<(byte[] Tweak, bool IsXOnly)> tweaks)
    {
        var keyAggContext = KeyAgg(pubKeys);
        foreach (var (tweak, isXOnly) in tweaks)
            keyAggContext = ApplyTweak(keyAggContext, tweak, isXOnly);

        return keyAggContext;
    }

    /// <summary>
    /// BIP 341's key-path-only tweak (BIP 86): <c>tagged_hash("TapTweak", x(P))</c>, applied x-only.
    /// </summary>
    public static byte[] TaprootKeyPathTweak(ReadOnlySpan<byte> xOnlyInternalKey) =>
        TaggedHash("TapTweak", xOnlyInternalKey);

    private static byte[] HashKeys(IReadOnlyList<byte[]> pubKeys)
    {
        using var sha = NewTagged("KeyAgg list");
        foreach (var pubKey in pubKeys)
            sha.Write(pubKey);

        return sha.GetHash();
    }

    private static byte[] GetSecondKey(IReadOnlyList<byte[]> pubKeys)
    {
        for (var j = 1; j < pubKeys.Count; j++)
            if (!pubKeys[j].AsSpan().SequenceEqual(pubKeys[0]))
                return pubKeys[j];

        return s_infinityEncoding;
    }

    private static Scalar KeyAggCoeff(IReadOnlyList<byte[]> pubKeys, ReadOnlySpan<byte> pubKey) =>
        KeyAggCoeffInternal(HashKeys(pubKeys), pubKey, GetSecondKey(pubKeys));

    private static Scalar KeyAggCoeffInternal(byte[] keysHash, ReadOnlySpan<byte> pubKey, byte[] pk2)
    {
        if (pubKey.SequenceEqual(pk2))
            return Scalar.One;

        using var sha = NewTagged("KeyAgg coefficient");
        sha.Write(keysHash);
        sha.Write(pubKey);
        return ScalarFromHash(sha.GetHash());
    }

    #endregion

    #region Nonces

    /// <summary>
    /// <c>nonce_gen_internal</c>: the 97-byte secret nonce and the 66-byte public nonce from caller-supplied
    /// <paramref name="rand"/> (<c>rand'</c>). A null optional argument is absent, which differs from an empty one
    /// for <paramref name="msg"/>.
    /// </summary>
    public static (byte[] SecNonce, byte[] PubNonce) NonceGen(ReadOnlySpan<byte> rand, ReadOnlySpan<byte> pubKey,
                                                              byte[]? secretKey, byte[]? aggPubKey, byte[]? msg,
                                                              byte[]? extraIn)
    {
        if (rand.Length != MusigConstants.NonceRandomnessLen)
            throw new MusigException("The byte array rand' must have length 32.");
        if (secretKey is not null && secretKey.Length != ScalarLen)
            throw new MusigException("The optional byte array sk must have length 32.");
        if (aggPubKey is not null && aggPubKey.Length != MusigConstants.XOnlyPubKeyLen)
            throw new MusigException("The optional byte array aggpk must have length 32.");

        Span<byte> mixedRand = stackalloc byte[ScalarLen];
        var k1 = Scalar.Zero;
        var k2 = Scalar.Zero;
        byte[]? secNonce = null;
        try
        {
            if (secretKey is not null)
                MixWithAuxHash(secretKey, rand, mixedRand);
            else
                rand.CopyTo(mixedRand);

            var msgPrefixed = MessagePrefixed(msg);
            k1 = NonceHash(mixedRand, pubKey, aggPubKey ?? [], 0, msgPrefixed, extraIn ?? []);
            k2 = NonceHash(mixedRand, pubKey, aggPubKey ?? [], 1, msgPrefixed, extraIn ?? []);

            // k1 == 0 or k2 == 0 cannot occur except with negligible probability
            if (k1.IsZero || k2.IsZero)
                throw new InvalidOperationException("A MuSig2 nonce scalar is zero.");

            secNonce = new byte[MusigConstants.SecretNonceLen];
            k1.WriteToSpan(secNonce.AsSpan(0, ScalarLen));
            k2.WriteToSpan(secNonce.AsSpan(ScalarLen, ScalarLen));
            pubKey.CopyTo(secNonce.AsSpan(MusigConstants.SecretNonceScalarsLen));

            return (secNonce, PublicNonceOf(in k1, in k2));
        }
        catch
        {
            // The secret nonce never leaves a failed call
            if (secNonce is not null)
                CryptographicOperations.ZeroMemory(secNonce);
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(mixedRand);
            Scalar.Clear(ref k1);
            Scalar.Clear(ref k2);
        }
    }

    /// <summary>
    /// <c>nonce_agg</c>: the 66-byte aggregate nonce, with a half at infinity encoded as 33 zero bytes.
    /// </summary>
    public static byte[] NonceAgg(IReadOnlyList<byte[]> pubNonces)
    {
        var aggNonce = new byte[MusigConstants.AggregateNonceLen];
        for (var j = 0; j < 2; j++)
        {
            var rj = GEJ.Infinity;
            for (var i = 0; i < pubNonces.Count; i++)
            {
                if (!TryCpoint(Half(pubNonces[i], j), out var rij))
                    throw new MusigInvalidContributionException(i, MusigContribution.PubNonce);

                rj = rj.AddVariable(rij);
            }

            CbytesExt(rj, aggNonce.AsSpan(j * PointLen, PointLen));
        }

        return aggNonce;
    }

    private static Scalar NonceHash(ReadOnlySpan<byte> rand, ReadOnlySpan<byte> pubKey, ReadOnlySpan<byte> aggPubKey,
                                    byte i, ReadOnlySpan<byte> msgPrefixed, ReadOnlySpan<byte> extraIn)
    {
        Span<byte> extraInLength = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(extraInLength, (uint)extraIn.Length);

        // rand' is secret: a wiping hasher, and the digest only ever on the stack (NL-911)
        using var sha = WipingSha256.CreateTagged("MuSig/nonce");
        sha.Write(rand);
        sha.Write((byte)pubKey.Length);
        sha.Write(pubKey);
        sha.Write((byte)aggPubKey.Length);
        sha.Write(aggPubKey);
        sha.Write(msgPrefixed);
        sha.Write(extraInLength);
        sha.Write(extraIn);
        sha.Write(i);
        return SecretScalarFromHash(sha);
    }

    private static byte[] MessagePrefixed(byte[]? msg)
    {
        if (msg is null)
            return [0x00];

        var prefixed = new byte[1 + 8 + msg.Length];
        prefixed[0] = 0x01;
        BinaryPrimitives.WriteUInt64BigEndian(prefixed.AsSpan(1, 8), (ulong)msg.Length);
        msg.CopyTo(prefixed.AsSpan(9));
        return prefixed;
    }

    private static byte[] PublicNonceOf(in Scalar k1, in Scalar k2)
    {
        var pubNonce = new byte[MusigConstants.PublicNonceLen];
        Cbytes(Ctx.EcMultGenContext.MultGen(k1).ToGroupElement(), pubNonce.AsSpan(0, PointLen));
        Cbytes(Ctx.EcMultGenContext.MultGen(k2).ToGroupElement(), pubNonce.AsSpan(PointLen, PointLen));
        return pubNonce;
    }

    #endregion

    #region Signing

    /// <summary>
    /// <c>sign</c>: the 32-byte partial signature. Overwrites the two scalars of <paramref name="secNonce"/> with
    /// zeros once the session is known to be valid, as the reference does, so a second call with it fails.
    /// </summary>
    public static byte[] Sign(Span<byte> secNonce, ReadOnlySpan<byte> secretKey, SessionContext session)
    {
        if (secNonce.Length != MusigConstants.SecretNonceLen)
            throw new MusigException("The secret nonce must be 97 bytes.");

        var values = GetSessionValues(session);
        var k1Raw = new Scalar(secNonce[..ScalarLen], out var k1Overflow);
        var k2Raw = new Scalar(secNonce.Slice(ScalarLen, ScalarLen), out var k2Overflow);

        // Overwrite the secnonce with zeros such that subsequent calls of sign with the same secnonce fail
        CryptographicOperations.ZeroMemory(secNonce[..MusigConstants.SecretNonceScalarsLen]);

        // Every secret scalar and every intermediate that mixes one in is a named local, cleared on every path (NL-911)
        var dRaw = Scalar.Zero;
        var d = Scalar.Zero;
        var k1 = Scalar.Zero;
        var k2 = Scalar.Zero;
        var gd = Scalar.Zero;
        var bk2 = Scalar.Zero;
        var ead = Scalar.Zero;
        var k1Bk2 = Scalar.Zero;
        var s = Scalar.Zero;
        try
        {
            if (k1Overflow != 0 || k1Raw.IsZero)
                throw new MusigException("first secnonce value is out of range.");
            if (k2Overflow != 0 || k2Raw.IsZero)
                throw new MusigException("second secnonce value is out of range.");

            var evenR = HasEvenY(values.R);
            k1 = evenR ? k1Raw : k1Raw.Negate();
            k2 = evenR ? k2Raw : k2Raw.Negate();

            dRaw = ScalarInRange(secretKey, "secret key value is out of range.");
            var pubKey = Cbytes(Ctx.EcMultGenContext.MultGen(dRaw).ToGroupElement());
            if (!secNonce[MusigConstants.SecretNonceScalarsLen..].SequenceEqual(pubKey))
                throw new MusigException("Public key does not match nonce_gen argument");

            var a = GetSessionKeyAggCoeff(session, pubKey);
            var g = HasEvenY(values.Q) ? Scalar.One : Scalar.MinusOne;
            gd = g.Multiply(values.Gacc);
            d = gd.Multiply(dRaw);
            Scalar.Clear(ref dRaw);

            bk2 = values.B.Multiply(k2);
            ead = values.E.Multiply(a).Multiply(d);
            k1Bk2 = k1.Add(bk2);
            s = k1Bk2.Add(ead);
            var partialSig = s.ToBytes();

            // Optional correctness check of the reference: the result must pass verification (a fault attack guard)
            if (!PartialSigVerifyInternal(partialSig, PublicNonceOf(in k1Raw, in k2Raw), pubKey, session))
                throw new InvalidOperationException("The MuSig2 partial signature does not verify.");

            return partialSig;
        }
        finally
        {
            Scalar.Clear(ref k1Raw);
            Scalar.Clear(ref k2Raw);
            Scalar.Clear(ref k1);
            Scalar.Clear(ref k2);
            Scalar.Clear(ref dRaw);
            Scalar.Clear(ref d);
            Scalar.Clear(ref gd);
            Scalar.Clear(ref bk2);
            Scalar.Clear(ref ead);
            Scalar.Clear(ref k1Bk2);
            Scalar.Clear(ref s);
        }
    }

    /// <summary>
    /// <c>partial_sig_verify</c>: aggregates <paramref name="pubNonces"/> and checks signer <paramref name="i"/>'s
    /// partial signature.
    /// </summary>
    public static bool PartialSigVerify(ReadOnlySpan<byte> partialSig, IReadOnlyList<byte[]> pubNonces,
                                        IReadOnlyList<byte[]> pubKeys,
                                        IReadOnlyList<(byte[] Tweak, bool IsXOnly)> tweaks, byte[] msg, int i)
    {
        if (pubNonces.Count != pubKeys.Count)
            throw new MusigException("The `pubnonces` and `pubkeys` arrays must have the same length.");

        var aggNonce = NonceAgg(pubNonces);
        var session = new SessionContext(aggNonce, pubKeys, tweaks, msg);
        return PartialSigVerifyInternal(partialSig, pubNonces[i], pubKeys[i], session);
    }

    /// <summary>
    /// <c>partial_sig_verify_internal</c>. A partial signature at or above n is false; a public nonce or key that
    /// does not decode is an invalid contribution (the reference raises there too).
    /// </summary>
    public static bool PartialSigVerifyInternal(ReadOnlySpan<byte> partialSig, ReadOnlySpan<byte> pubNonce,
                                                ReadOnlySpan<byte> pubKey, SessionContext session)
    {
        var values = GetSessionValues(session);
        if (partialSig.Length != MusigConstants.PartialSignatureLen)
            return false;

        var s = new Scalar(partialSig, out var overflow);
        if (overflow != 0)
            return false;

        if (pubNonce.Length != MusigConstants.PublicNonceLen
         || !TryCpoint(pubNonce[..PointLen], out var rs1)
         || !TryCpoint(pubNonce[PointLen..], out var rs2))
            throw new MusigInvalidContributionException(null, MusigContribution.PubNonce);

        var res = rs1.ToGroupElementJacobian().AddVariable(Mul(rs2, values.B));
        if (!HasEvenY(values.R))
            res = res.Negate();

        if (!TryCpoint(pubKey, out var p))
            throw new MusigInvalidContributionException(null, MusigContribution.PubKey);

        var a = GetSessionKeyAggCoeff(session, pubKey);
        var g = HasEvenY(values.Q) ? Scalar.One : Scalar.MinusOne;
        var gPrime = g.Multiply(values.Gacc);

        // s*G == Re_s + (e*a*g')*P  <=>  (e*a*g')*P + (-s)*G + Re_s is the point at infinity
        Scalar? negS = s.Negate();
        var check = Ctx.EcMultContext.Mult(p.ToGroupElementJacobian(), values.E.Multiply(a).Multiply(gPrime), negS)
                       .AddVariable(res);
        return check.IsInfinity;
    }

    /// <summary>
    /// <c>partial_sig_agg</c>: the 64-byte BIP-340 signature.
    /// </summary>
    public static byte[] PartialSigAgg(IReadOnlyList<byte[]> partialSigs, SessionContext session)
    {
        var values = GetSessionValues(session);
        var s = Scalar.Zero;
        for (var i = 0; i < partialSigs.Count; i++)
        {
            if (partialSigs[i] is not { Length: MusigConstants.PartialSignatureLen })
                throw new MusigInvalidContributionException(i, MusigContribution.PartialSignature);

            var si = new Scalar(partialSigs[i], out var overflow);
            if (overflow != 0)
                throw new MusigInvalidContributionException(i, MusigContribution.PartialSignature);

            s = s.Add(si);
        }

        var g = HasEvenY(values.Q) ? Scalar.One : Scalar.MinusOne;
        s = s.Add(values.E.Multiply(g).Multiply(values.Tacc));

        var signature = new byte[MusigConstants.SchnorrSignatureLen];
        Xbytes(values.R, signature.AsSpan(0, ScalarLen));
        s.WriteToSpan(signature.AsSpan(ScalarLen, ScalarLen));
        return signature;
    }

    /// <summary>
    /// <c>deterministic_sign</c>: the stateless signer's public nonce and partial signature.
    /// </summary>
    public static (byte[] PubNonce, byte[] PartialSig) DeterministicSign(
        byte[] secretKey, byte[] aggOtherNonce, IReadOnlyList<byte[]> pubKeys,
        IReadOnlyList<(byte[] Tweak, bool IsXOnly)> tweaks, byte[] msg, byte[]? rand)
    {
        if (secretKey.Length != ScalarLen)
            throw new MusigException("The secret key must be 32 bytes.");
        if (rand is not null && rand.Length != ScalarLen)
            throw new MusigException("The optional byte array rand must have length 32.");

        Span<byte> skPrime = stackalloc byte[ScalarLen];
        var secNonce = GC.AllocateArray<byte>(MusigConstants.SecretNonceLen, pinned: true);
        var k1 = Scalar.Zero;
        var k2 = Scalar.Zero;
        try
        {
            if (rand is not null)
                MixWithAuxHash(secretKey, rand, skPrime);
            else
                secretKey.CopyTo(skPrime);

            var aggPubKey = GetXOnlyPubKey(KeyAggAndTweak(pubKeys, tweaks));
            k1 = DetNonceHash(skPrime, aggOtherNonce, aggPubKey, msg, 0);
            k2 = DetNonceHash(skPrime, aggOtherNonce, aggPubKey, msg, 1);

            // k1 == 0 or k2 == 0 cannot occur except with negligible probability
            if (k1.IsZero || k2.IsZero)
                throw new InvalidOperationException("A MuSig2 nonce scalar is zero.");

            var pubNonce = PublicNonceOf(in k1, in k2);
            k1.WriteToSpan(secNonce.AsSpan(0, ScalarLen));
            k2.WriteToSpan(secNonce.AsSpan(ScalarLen, ScalarLen));
            IndividualPubKey(secretKey).CopyTo(secNonce, MusigConstants.SecretNonceScalarsLen);

            byte[] aggNonce;
            try
            {
                aggNonce = NonceAgg([pubNonce, aggOtherNonce]);
            }
            catch (MusigInvalidContributionException)
            {
                throw new MusigInvalidContributionException(null, MusigContribution.AggOtherNonce);
            }

            var partialSig = Sign(secNonce, secretKey, new SessionContext(aggNonce, pubKeys, tweaks, msg));
            return (pubNonce, partialSig);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(skPrime);
            CryptographicOperations.ZeroMemory(secNonce);
            Scalar.Clear(ref k1);
            Scalar.Clear(ref k2);
        }
    }

    /// <summary>
    /// BIP-340 <c>schnorr_verify</c> for a message of any length.
    /// </summary>
    public static bool SchnorrVerify(ReadOnlySpan<byte> msg, ReadOnlySpan<byte> xOnlyPubKey,
                                     ReadOnlySpan<byte> signature)
    {
        if (xOnlyPubKey.Length != MusigConstants.XOnlyPubKeyLen
         || signature.Length != MusigConstants.SchnorrSignatureLen
         || !ECXOnlyPubKey.TryCreate(xOnlyPubKey, Ctx, out var key) || key is null)
            return false;

        var s = new Scalar(signature[ScalarLen..], out var overflow);
        if (overflow != 0)
            return false;

        using var sha = NewTagged("BIP0340/challenge");
        sha.Write(signature[..ScalarLen]);
        sha.Write(xOnlyPubKey);
        sha.Write(msg);
        var e = ScalarFromHash(sha.GetHash());

        // R = s*G - e*P
        Scalar? sg = s;
        var r = Ctx.EcMultContext.Mult(key.Q.ToGroupElementJacobian(), e.Negate(), sg);
        if (r.IsInfinity)
            return false;

        var rAffine = r.ToGroupElementVariable();
        return HasEvenY(rAffine) && Xbytes(rAffine).AsSpan().SequenceEqual(signature[..ScalarLen]);
    }

    private static Scalar DetNonceHash(ReadOnlySpan<byte> skPrime, ReadOnlySpan<byte> aggOtherNonce,
                                       ReadOnlySpan<byte> aggPubKey, ReadOnlySpan<byte> msg, byte i)
    {
        Span<byte> msgLength = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(msgLength, (ulong)msg.Length);

        // sk' is secret: a wiping hasher, and the digest only ever on the stack (NL-911)
        using var sha = WipingSha256.CreateTagged("MuSig/deterministic/nonce");
        sha.Write(skPrime);
        sha.Write(aggOtherNonce);
        sha.Write(aggPubKey);
        sha.Write(msgLength);
        sha.Write(msg);
        sha.Write(i);
        return SecretScalarFromHash(sha);
    }

    /// <summary>
    /// Checks that a session's keys, tweaks and aggregate nonce decode (what Sign checks before it touches the
    /// secret nonce); throws as Sign would.
    /// </summary>
    public static void ValidateSession(SessionContext session) => GetSessionValues(session);

    private static SessionValues GetSessionValues(SessionContext session)
    {
        var (q, gacc, tacc) = KeyAggAndTweak(session.PubKeys, session.Tweaks);
        var aggNonce = session.AggNonce;
        var qx = Xbytes(q);

        Scalar b;
        using (var sha = NewTagged("MuSig/noncecoef"))
        {
            sha.Write(aggNonce);
            sha.Write(qx);
            sha.Write(session.Message);
            b = ScalarFromHash(sha.GetHash());
        }

        if (aggNonce.Length != MusigConstants.AggregateNonceLen
         || !TryCpointExt(aggNonce.AsSpan(0, PointLen), out var r1)
         || !TryCpointExt(aggNonce.AsSpan(PointLen, PointLen), out var r2))
        {
            // Nonce aggregator sent invalid nonces
            throw new MusigInvalidContributionException(null, MusigContribution.AggNonce);
        }

        var rPrime = r1.ToGroupElementJacobian();
        if (!r2.IsInfinity)
            rPrime = rPrime.AddVariable(Mul(r2, b));

        var r = rPrime.IsInfinity ? s_generator : rPrime.ToGroupElementVariable();

        Scalar e;
        using (var sha = NewTagged("BIP0340/challenge"))
        {
            sha.Write(Xbytes(r));
            sha.Write(qx);
            sha.Write(session.Message);
            e = ScalarFromHash(sha.GetHash());
        }

        return new SessionValues(q, gacc, tacc, b, r, e);
    }

    private static Scalar GetSessionKeyAggCoeff(SessionContext session, ReadOnlySpan<byte> pubKey)
    {
        var included = false;
        foreach (var key in session.PubKeys)
            if (key.AsSpan().SequenceEqual(pubKey))
            {
                included = true;
                break;
            }

        if (!included)
            throw new MusigException("The signer's pubkey must be included in the list of pubkeys.");

        return KeyAggCoeff(session.PubKeys, pubKey);
    }

    #endregion

    #region Encoding and arithmetic helpers

    private static TaggedSha256 NewTagged(string tag)
    {
        var sha = new TaggedSha256();
        sha.InitializeTagged(tag);
        return sha;
    }

    private static byte[] TaggedHash(string tag, ReadOnlySpan<byte> data)
    {
        using var sha = NewTagged(tag);
        sha.Write(data);
        return sha.GetHash();
    }

    /// <summary>
    /// <c>int_from_bytes(hash) % n</c>.
    /// </summary>
    private static Scalar ScalarFromHash(ReadOnlySpan<byte> hash) => new(hash, out _);

    /// <summary>
    /// <see cref="ScalarFromHash"/> of a secret digest: finishes (and so wipes) <paramref name="sha"/> into a stack
    /// buffer that is zeroed before returning.
    /// </summary>
    private static Scalar SecretScalarFromHash(WipingSha256 sha)
    {
        Span<byte> hash = stackalloc byte[WipingSha256.HashLen];
        try
        {
            sha.GetHash(hash);
            return ScalarFromHash(hash);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(hash);
        }
    }

    /// <summary>
    /// <c>xor(sk, tagged_hash("MuSig/aux", rand))</c> into <paramref name="destination"/>, with the aux digest only on
    /// the stack (zeroed) and the hasher wiped (NL-903, NL-911).
    /// </summary>
    private static void MixWithAuxHash(ReadOnlySpan<byte> secretKey, ReadOnlySpan<byte> rand, Span<byte> destination)
    {
        Span<byte> auxHash = stackalloc byte[WipingSha256.HashLen];
        try
        {
            using (var sha = WipingSha256.CreateTagged("MuSig/aux"))
            {
                sha.Write(rand);
                sha.GetHash(auxHash);
            }

            for (var i = 0; i < ScalarLen; i++)
                destination[i] = (byte)(secretKey[i] ^ auxHash[i]);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(auxHash);
        }
    }

    private static Scalar ScalarInRange(ReadOnlySpan<byte> bytes, string error)
    {
        if (bytes.Length != ScalarLen)
            throw new MusigException(error);

        var scalar = new Scalar(bytes, out var overflow);
        if (overflow != 0 || scalar.IsZero)
            throw new MusigException(error);

        return scalar;
    }

    private static ReadOnlySpan<byte> Half(byte[]? nonce, int j) =>
        nonce is { Length: MusigConstants.PublicNonceLen } ? nonce.AsSpan(j * PointLen, PointLen) : [];

    /// <summary>
    /// <c>cpoint</c>: a 33-byte compressed point with prefix 02 or 03 and x on the curve.
    /// </summary>
    private static bool TryCpoint(ReadOnlySpan<byte> bytes, out GE point)
    {
        point = default;
        if (bytes.Length != PointLen || (bytes[0] != 0x02 && bytes[0] != 0x03)
         || !ECPubKey.TryCreate(bytes, Ctx, out var compressed, out var pubKey) || !compressed || pubKey is null)
            return false;

        point = pubKey.Q;
        return true;
    }

    /// <summary>
    /// <c>cpoint_ext</c>: <see cref="TryCpoint"/>, or 33 zero bytes for the point at infinity.
    /// </summary>
    private static bool TryCpointExt(ReadOnlySpan<byte> bytes, out GE point)
    {
        if (bytes.SequenceEqual(s_infinityEncoding))
        {
            point = GE.Infinity;
            return true;
        }

        return TryCpoint(bytes, out point);
    }

    private static GEJ Mul(GE point, Scalar scalar) => Ctx.EcMultContext.Mult(point.ToGroupElementJacobian(), scalar, null);

    private static bool HasEvenY(GE point) => !point.y.NormalizeVariable().IsOdd;

    private static byte[] Xbytes(GE point)
    {
        var bytes = new byte[ScalarLen];
        Xbytes(point, bytes);
        return bytes;
    }

    private static void Xbytes(GE point, Span<byte> destination) => point.x.NormalizeVariable().WriteToSpan(destination);

    private static byte[] Cbytes(GE point)
    {
        var bytes = new byte[PointLen];
        Cbytes(point, bytes);
        return bytes;
    }

    private static void Cbytes(GE point, Span<byte> destination)
    {
        destination[0] = HasEvenY(point) ? (byte)0x02 : (byte)0x03;
        Xbytes(point, destination[1..]);
    }

    private static void CbytesExt(GEJ point, Span<byte> destination)
    {
        if (point.IsInfinity)
        {
            destination.Clear();
            return;
        }

        Cbytes(point.ToGroupElementVariable(), destination);
    }

    #endregion
}