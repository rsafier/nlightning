using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using NBitcoin;

namespace NLightning.Infrastructure.Bitcoin.KeyRing;

using Domain.Crypto.Interfaces;
using Domain.Crypto.KeyRing;
using Domain.Crypto.Models;
using Domain.Crypto.ValueObjects;
using Domain.Protocol.Interfaces;
using Services;
using Taproot;

/// <summary>Raw swap signing with isolated ring keys. Neither private keys nor secret nonces leave this layer.</summary>
public sealed class SwapSigner : ISwapSigner, IDisposable
{
    private readonly IKeyRing _ring;
    private readonly Func<KeyRingLocator, Key> _open;
    private readonly IMusig2Service _musig;
    private readonly ISecp256K1Math _math;
    private readonly KeyRingOptions _options;
    private readonly TimeProvider _clock;
    private readonly object _gate = new();
    private readonly Dictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    private readonly ITimer _timer;
    private readonly SwapSessionStateStore? _store;
    private bool _failed;
    private bool _disposed;

    public SwapSigner(KeyRingService ring, IMusig2Service musig, ISecp256K1Math math,
                        IOptions<KeyRingOptions> options, TimeProvider? clock = null, string? sessionStatePath = null)
        : this(ring, ring.Open, musig, math, options, clock, sessionStatePath) { }

    public SwapSigner(ISecureKeyManager keys, IMusig2Service musig, ISecp256K1Math math,
                        IOptions<KeyRingOptions> options, TimeProvider? clock = null, string? sessionStatePath = null)
        : this(new SignerSwapKeyRing(keys, options.Value), locator => SignerSwapKeyRing.Open(keys, options.Value, locator),
            musig, math, options, clock, sessionStatePath)
    { }

    private SwapSigner(IKeyRing ring, Func<KeyRingLocator, Key> open, IMusig2Service musig, ISecp256K1Math math,
                        IOptions<KeyRingOptions> options, TimeProvider? clock, string? sessionStatePath)
    {
        _ring = ring;
        _open = open;
        _musig = musig;
        _math = math;
        _options = options.Value;
        _clock = clock ?? TimeProvider.System;
        if (_options.MaxSessions is < 1 or > 10_000 || _options.SessionLifetime <= TimeSpan.Zero)
            throw new ArgumentException("Invalid swap signer session limits.");
        if (sessionStatePath is not null)
        {
            using var journalKey = _open(new KeyRingLocator(99, int.MaxValue));
            var secret = journalKey.ToBytes();
            var encryptionKey = HMACSHA256.HashData(secret, Encoding.ASCII.GetBytes("NLightning/native-swap-session-journal/v1"));
            try { _store = new SwapSessionStateStore(sessionStatePath, encryptionKey); Restore(); }
            catch { _store?.Dispose(); throw; }
            finally { CryptographicOperations.ZeroMemory(secret); CryptographicOperations.ZeroMemory(encryptionKey); }
        }
        _timer = _clock.CreateTimer(_ =>
        {
            lock (_gate)
            {
                if (_disposed || _store is not null) return;
                try { Prune(); }
                catch { _failed = true; }
            }
            // Durable expiry is committed only by an explicit operation inside its authority fence.
        }, null, _store is null ? TimeSpan.FromMinutes(1) : Timeout.InfiniteTimeSpan,
            _store is null ? TimeSpan.FromMinutes(1) : Timeout.InfiniteTimeSpan);
    }

    public static string TransactionId(byte[] rawTransaction) => Transaction.Load(rawTransaction, Network.Main).GetHash().ToString();

    public async Task<KeyRingKey> ResolveAsync(KeyRingLocator? locator, byte[] publicKey, CancellationToken ct)
    {
        if (locator is { } loc)
        {
            var key = await _ring.DeriveAsync(loc, ct);
            if (publicKey.Length != 0 && !((byte[])key.PublicKey).AsSpan().SequenceEqual(publicKey))
                throw new ArgumentException("public key does not match key locator");
            return key;
        }
        if (publicKey.Length != 33)
            throw new UnauthorizedAccessException("an explicit ring key locator or public key is required");
        return await _ring.FindAsync(publicKey, ct)
            ?? throw new UnauthorizedAccessException("public key is not in the swap key ring");
    }

    public async Task<byte[]> SharedKeyAsync(KeyRingLocator? locator, byte[] publicKey, byte[] ephemeral, CancellationToken ct)
    {
        var resolved = await ResolveAsync(locator, publicKey, ct);
        if (resolved.Locator.Family != 21 && resolved.Locator.Family != 99)
            throw new UnauthorizedAccessException("shared key derivation is available only in families 21 and 99");
        using var key = _open(resolved.Locator);
        var secret = key.ToBytes();
        try { return SHA256.HashData((byte[])_math.MultiplyPubKey(ephemeral, secret)); }
        finally { CryptographicOperations.ZeroMemory(secret); }
    }

    public async Task<byte[]> SignOutputAsync(byte[] rawTransaction, SwapSignDescriptor descriptor,
                                               IReadOnlyList<SwapPrevOutput> prevOutputs, CancellationToken ct)
    {
        if (rawTransaction.Length > 400_000 || prevOutputs.Count > 100)
            throw new ArgumentException("invalid signing request size");
        var resolved = await ResolveAsync(descriptor.Locator, descriptor.PublicKey, ct);
        using var baseKey = _open(resolved.Locator);
        using var key = Tweak(baseKey, descriptor.SingleTweak, descriptor.DoubleTweak);
        var tx = Transaction.Load(rawTransaction, Network.Main);
        if (descriptor.InputIndex < 0 || descriptor.InputIndex >= tx.Inputs.Count || descriptor.Output.Value < 0)
            throw new ArgumentException("invalid signing input index or output value");
        var output = new TxOut(Money.Satoshis(descriptor.Output.Value), new Script(descriptor.Output.Script));
        var witnessScript = new Script(descriptor.WitnessScript);
        if (descriptor.SignMethod == 0)
        {
            if (descriptor.Sighash is not (1 or 2 or 3 or 0x81 or 0x82 or 0x83))
                throw new ArgumentException("invalid witness-v0 sighash");
            if (descriptor.WitnessScript.Length == 0)
                witnessScript = key.PubKey.Hash.ScriptPubKey;
            var digest = tx.GetSignatureHash(witnessScript, descriptor.InputIndex, (SigHash)descriptor.Sighash,
                                             output, HashVersion.WitnessV0);
            return key.Sign(digest).ToDER();
        }
        if (descriptor.SignMethod is < 1 or > 3 || descriptor.Sighash is not (0 or 1 or 2 or 3 or 0x81 or 0x82 or 0x83))
            throw new ArgumentException("unsupported sign method or taproot sighash");
        if (prevOutputs.Count != tx.Inputs.Count || prevOutputs.Any(p => p.Value < 0))
            throw new ArgumentException("taproot requires one prev_output per input in transaction order");
        var spent = prevOutputs.Select(p => new TxOut(Money.Satoshis(p.Value), new Script(p.Script))).ToArray();
        if (spent[descriptor.InputIndex].Value != output.Value || spent[descriptor.InputIndex].ScriptPubKey != output.ScriptPubKey)
            throw new ArgumentException("descriptor output does not match prev_outputs at input_index");
        var execution = descriptor.SignMethod == 3
                            ? new TaprootExecutionData(descriptor.InputIndex, new TapScript(witnessScript, (TapLeafVersion)0xc0).LeafHash)
                            : new TaprootExecutionData(descriptor.InputIndex);
        execution.SigHash = (TaprootSigHash)descriptor.Sighash;
        var hash = tx.GetSignatureHashTaproot(spent, execution);
        byte[] signature;
        if (descriptor.SignMethod == 3)
        {
            if (descriptor.WitnessScript.Length == 0 || descriptor.TapTweak.Length != 0)
                throw new ArgumentException("script signing needs a witness_script and no tap_tweak");
            signature = TaprootSignatures.Sign(key, hash);
        }
        else
        {
            if (descriptor.SignMethod == 1 && descriptor.TapTweak.Length != 0 || descriptor.TapTweak.Length is not (0 or 32))
                throw new ArgumentException("invalid taproot tweak root");
            var pair = key.CreateTaprootKeyPair(descriptor.TapTweak.Length == 0 ? null : new uint256(descriptor.TapTweak));
            signature = pair.SignTaprootKeySpend(hash, execution.SigHash).ToBytes();
            // signrpc returns the raw Schnorr signature; the caller appends its sighash to the witness.
            return signature[..64];
        }
        return signature;
    }

    public MusigKeyAggregate CombineKeys(IReadOnlyList<CompactPubKey> keys, IReadOnlyList<MusigTweak> tweaks,
                                         byte[]? taprootRoot)
    {
        if (keys.Count is < 2 or > 100 || keys.Distinct().Count() != keys.Count)
            throw new ArgumentException("MuSig2 requires 2..100 distinct compressed keys");
        var sorted = _musig.SortPubKeys(keys);
        var aggregate = _musig.AggregatePubKeys(sorted, tweaks);
        if (taprootRoot is null)
            return aggregate;
        if (taprootRoot.Length is not (0 or 32))
            throw new ArgumentException("taproot script root must be empty or 32 bytes");
        var tweak = TaggedHash("TapTweak", [.. ((byte[])aggregate.OutputKey)[1..], .. taprootRoot]);
        return _musig.AggregatePubKeys(sorted, [.. tweaks, new MusigTweak(tweak, true)]);
    }

    public async Task<SwapMusigSession> CreateAsync(KeyRingLocator locator, MusigKeyAggregate aggregate,
                                                    IReadOnlyList<byte[]> otherNonces, CancellationToken ct)
    {
        if (aggregate.PubKeys.Count is < 2 or > 100 || aggregate.PubKeys.Distinct().Count() != aggregate.PubKeys.Count)
            throw new ArgumentException("MuSig2 requires 2..100 distinct compressed keys");
        var computed = _musig.AggregatePubKeys(aggregate.PubKeys, aggregate.Tweaks);
        if (computed.InternalKey != aggregate.InternalKey || computed.OutputKey != aggregate.OutputKey)
            throw new ArgumentException("MuSig2 aggregate does not match its keys and tweaks");
        var own = await _ring.DeriveAsync(locator, ct);
        if (!aggregate.PubKeys.Contains(own.PublicKey))
            throw new ArgumentException("local signing key is not in all_signer_pubkeys");
        lock (_gate)
        {
            Prune();
            if (_sessions.Count >= _options.MaxSessions)
                throw new InvalidOperationException("swap signer session limit reached");
            using var key = _open(locator);
            var bytes = key.ToBytes();
            MusigNoncePair nonce;
            var randomness = RandomNumberGenerator.GetBytes(32);
            try { nonce = _musig.GenerateNonce(randomness, own.PublicKey, new PrivKey(bytes), aggregate.XOnlyOutputKey); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
            try
            {
                var session = new Session(locator, aggregate, nonce, _clock.GetUtcNow(), randomness);
                AddNonces(session, otherNonces);
                var id = SHA256.HashData([.. aggregate.XOnlyOutputKey, .. ((byte[])nonce.PublicNonce).ToArray()]);
                _sessions.Add(Convert.ToHexString(id), session);
                Save();
                return new SwapMusigSession(id, aggregate, ((byte[])nonce.PublicNonce).ToArray(),
                                              session.Nonces.Count == aggregate.PubKeys.Count);
            }
            catch { nonce.SecretNonce.Dispose(); throw; }
        }
    }

    public bool RegisterNonces(byte[] id, IReadOnlyList<byte[]> nonces)
    {
        lock (_gate)
        {
            var session = Get(id);
            if (session.Signing is not null)
                throw new InvalidOperationException("session already signed");
            AddNonces(session, nonces);
            Save();
            return session.Nonces.Count == session.Aggregate.PubKeys.Count;
        }
    }

    public byte[] Sign(byte[] id, byte[] digest, bool cleanup)
    {
        if (digest.Length != 32)
            throw new ArgumentException("message_digest must be 32 bytes");
        lock (_gate)
        {
            var session = Get(id);
            if (session.Consumed)
            {
                if (session.Partials.Count > 0 && session.Digest is not null && session.Digest.AsSpan().SequenceEqual(digest))
                    return ((byte[])session.Partials[0]).ToArray();
                throw new InvalidOperationException("session nonce was already consumed; an uncertain result cannot be regenerated");
            }
            if (session.Nonces.Count != session.Aggregate.PubKeys.Count)
                throw new InvalidOperationException("not all signer nonces are registered");
            session.Signing = _musig.CreateSession(session.Aggregate, session.Nonces, digest);
            session.Digest = digest.ToArray();
            session.Consumed = true;
            Save();
            using var key = _open(session.Locator);
            var bytes = key.ToBytes();
            try
            {
                var signature = _musig.Sign(session.Nonce.SecretNonce, new PrivKey(bytes), session.Signing);
                session.Partials.Add(signature);
                Save();
                return ((byte[])signature).ToArray();
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
                session.Nonce.SecretNonce.Dispose();
                if (cleanup) Cleanup(id);
            }
        }
    }

    public byte[]? Combine(byte[] id, IReadOnlyList<byte[]> partials)
    {
        lock (_gate)
        {
            var session = Get(id);
            if (session.Signing is null || session.Partials.Count == 0)
                throw new InvalidOperationException("local signature is required before combining");
            if (session.Partials.Count + partials.Count > session.Aggregate.PubKeys.Count)
                throw new ArgumentException("too many partial signatures");
            var all = session.Partials.Concat(partials.Select(p => new MusigPartialSignature(p))).ToList();
            if (all.Distinct().Count() != all.Count)
                throw new ArgumentException("duplicate partial signature");
            if (all.Count != session.Aggregate.PubKeys.Count)
            {
                session.Partials.Clear();
                session.Partials.AddRange(all);
                Save();
                return null;
            }
            var signature = _musig.AggregatePartialSignatures(all, session.Signing);
            if (!_musig.VerifySignature(signature, session.Aggregate.XOnlyOutputKey, session.Signing.Message))
                throw new ArgumentException("combined signature failed verification");
            Cleanup(id);
            return signature;
        }
    }

    public void Cleanup(byte[] id)
    {
        if (id.Length != 32) throw new ArgumentException("session_id must be 32 bytes");
        lock (_gate)
        {
            RequireHealthy();
            if (_sessions.Remove(Convert.ToHexString(id), out var session))
            {
                session.Nonce.SecretNonce.Dispose();
                CryptographicOperations.ZeroMemory(session.Randomness);
                Save();
            }
        }
    }

    private Key Tweak(Key key, byte[] single, byte[] dual)
    {
        if (single.Length != 0 && dual.Length != 0)
            throw new ArgumentException("single_tweak and double_tweak are mutually exclusive");
        if (single.Length is not (0 or 32) || dual.Length is not (0 or 32))
            throw new ArgumentException("key tweak must be 32 bytes");
        var bytes = key.ToBytes();
        byte[]? tweaked = null;
        try
        {
            if (single.Length != 0)
                tweaked = _math.AddPrivKeys(new PrivKey(bytes), new PrivKey(single));
            else if (dual.Length != 0)
                tweaked = new KeyDerivationService(_math).DeriveRevocationPrivKey(new PrivKey(bytes), new PrivKey(dual));
            return new Key(tweaked ?? bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            if (tweaked is not null) CryptographicOperations.ZeroMemory(tweaked);
        }
    }

    private Session Get(byte[] id)
    {
        if (id.Length != 32) throw new ArgumentException("session_id must be 32 bytes");
        Prune();
        return _sessions.TryGetValue(Convert.ToHexString(id), out var session) ? session
            : throw new KeyNotFoundException("MuSig2 session not found or expired");
    }

    private void Prune()
    {
        RequireHealthy();
        var changed = false;
        foreach (var id in _sessions.Where(p => _clock.GetUtcNow() - p.Value.Created >= _options.SessionLifetime)
                                    .Select(p => p.Key).ToList())
        {
            _sessions[id].Nonce.SecretNonce.Dispose();
            CryptographicOperations.ZeroMemory(_sessions[id].Randomness);
            _sessions.Remove(id);
            changed = true;
        }
        if (changed) Save();
    }

    private static void AddNonces(Session session, IReadOnlyList<byte[]> values)
    {
        if (session.Nonces.Count + values.Count > session.Aggregate.PubKeys.Count)
            throw new ArgumentException("too many signer nonces");
        var added = values.Select(v => new MusigPublicNonce(v)).ToList();
        if (session.Nonces.Concat(added).Distinct().Count() != session.Nonces.Count + added.Count)
            throw new ArgumentException("duplicate signer nonce");
        session.Nonces.AddRange(added);
    }

    internal static byte[] TaggedHash(string tag, byte[] data)
    {
        var tagHash = SHA256.HashData(Encoding.ASCII.GetBytes(tag));
        return SHA256.HashData([.. tagHash, .. tagHash, .. data]);
    }

    public void Dispose()
    {
        _timer.Dispose();
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _store?.Dispose();
            foreach (var session in _sessions.Values)
            {
                session.Nonce.SecretNonce.Dispose();
                CryptographicOperations.ZeroMemory(session.Randomness);
            }
            _sessions.Clear();
        }
    }

    public byte[] GetCheckpointDigest()
    {
        lock (_gate)
        {
            RequireHealthy();
            return _store?.GetCheckpointDigest() ?? throw new InvalidOperationException("No durable swap journal is configured.");
        }
    }

    private void Save()
    {
        RequireHealthy();
        if (_store is null) return;
        try
        {
            _store.Save(_sessions.Values.Select(s => new SwapSessionSnapshot(s.Locator.Family, s.Locator.Index,
                s.Aggregate.PubKeys.Select(k => ((byte[])k).ToArray()).ToArray(),
                s.Aggregate.Tweaks.Select(t => t.Value.ToArray()).ToArray(), s.Aggregate.Tweaks.Select(t => t.IsXOnly).ToArray(),
                s.Aggregate.InternalKey, s.Aggregate.OutputKey, s.Randomness.ToArray(),
                s.Nonces.Select(n => ((byte[])n).ToArray()).ToArray(), s.Created, s.Digest,
                s.Partials.Select(p => ((byte[])p).ToArray()).ToArray(), s.Consumed)).ToArray());
        }
        catch { _failed = true; throw; }
    }

    private void RequireHealthy()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_failed) throw new InvalidOperationException("Swap session state failed; signing is blocked.");
    }

    private void Restore()
    {
        foreach (var saved in _store!.Sessions)
        {
            if (saved.Randomness.Length != 32 || saved.Keys.Length is < 2 or > 100
             || saved.Tweaks.Length != saved.XOnlyTweaks.Length || saved.Nonces.Length < 1)
                throw new InvalidDataException("Invalid swap session snapshot.");
            var locator = new KeyRingLocator(saved.Family, saved.Index);
            var aggregate = new MusigKeyAggregate(saved.Keys.Select(k => new CompactPubKey(k)).ToArray(),
                saved.Tweaks.Select((t, i) => new MusigTweak(t, saved.XOnlyTweaks[i])).ToArray(), saved.InternalKey, saved.OutputKey);
            using var key = _open(locator);
            var bytes = key.ToBytes();
            MusigNoncePair nonce;
            try { nonce = _musig.GenerateNonce(saved.Randomness, key.PubKey.ToBytes(), new PrivKey(bytes), aggregate.XOnlyOutputKey); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
            var session = new Session(locator, aggregate, nonce, saved.Created, saved.Randomness.ToArray())
            { Consumed = saved.Consumed, Digest = saved.Digest };
            if (!((byte[])nonce.PublicNonce).AsSpan().SequenceEqual(saved.Nonces[0]))
                throw new InvalidDataException("Swap session nonce does not match signer identity.");
            AddNonces(session, saved.Nonces.Skip(1).ToArray());
            session.Partials.AddRange(saved.Partials.Select(p => new MusigPartialSignature(p)));
            if (session.Consumed)
            {
                nonce.SecretNonce.Dispose();
                if (session.Digest is null) throw new InvalidDataException("Consumed swap nonce has no message binding.");
                session.Signing = _musig.CreateSession(aggregate, session.Nonces, session.Digest);
            }
            var id = SHA256.HashData([.. aggregate.XOnlyOutputKey, .. ((byte[])nonce.PublicNonce).ToArray()]);
            _sessions.Add(Convert.ToHexString(id), session);
        }
        if (_sessions.Count > _options.MaxSessions) throw new InvalidDataException("Too many saved swap sessions.");
    }

    private sealed class Session(KeyRingLocator locator, MusigKeyAggregate aggregate, MusigNoncePair nonce, DateTimeOffset created, byte[] randomness)
    {
        public KeyRingLocator Locator { get; } = locator;
        public MusigKeyAggregate Aggregate { get; } = aggregate;
        public MusigNoncePair Nonce { get; } = nonce;
        public DateTimeOffset Created { get; } = created;
        public byte[] Randomness { get; } = randomness;
        public bool Consumed { get; set; }
        public byte[]? Digest { get; set; }
        public List<MusigPublicNonce> Nonces { get; } = [nonce.PublicNonce];
        public List<MusigPartialSignature> Partials { get; } = [];
        public MusigSigningSession? Signing { get; set; }
    }
}