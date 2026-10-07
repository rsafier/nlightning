using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using Google.Protobuf;
using NLightning.Signing.Contracts;

namespace NLightning.Infrastructure.RemoteSigning;

using Domain.Bitcoin.Interfaces;
using Domain.Channels.ValueObjects;
using Domain.Exceptions;

/// <summary>
/// Prototype signer-owned append-only safety journal. Successful state changes and nonce-consuming results are
/// flushed before a reply leaves the signer. This protects ordinary process restarts, not malicious disk rollback.
/// </summary>
public sealed class DurableSignerState : IDisposable
{
    private const int MaxRecordBytes = RemoteSignerOptions.MaxMessageBytes * 3;
    private readonly ILightningSigner _signer;
    private readonly FileStream _stream;
    private readonly Lock _gate = new();
    private readonly HashSet<ChannelId> _retired = [];
    private readonly HashSet<ChannelId> _dataLoss = [];
    private readonly byte[] _networkIdentity;
    private readonly Dictionary<string, (string PayloadHash, byte[] Response)> _sessions = [];
    private bool _failed;
    // Never evict receipt identities: eviction would allow a delayed retry to execute twice.
    public const int MaxRequestReceipts = 65536;
    private const long MaxReceiptResponseBytes = 64 * 1024 * 1024;
    private readonly Dictionary<string, RequestReceipt> _requests = [];
    private long _receiptBytes;

    public static bool SupportsReconciliation(uint operation) => IsStateChange(operation)
        || operation is SignerOperations.CreateNewChannel or SignerOperations.ReserveChannelKeyIndex
                     or SignerOperations.EnsureLastUsedChannelIndexAtLeast
                     or SignerOperations.SignClosingAsClosee or SignerOperations.SignSpliceSharedInputPartial
                     or SignerOperations.SignChannelTransaction or SignerOperations.SignChannelTransaction2
                     or SignerOperations.SignRemoteHtlcTransactions or SignerOperations.SignRemoteCommitmentPartial
                     or SignerOperations.RevealPerCommitmentSecret or SignerOperations.GetPerCommitmentPoint2
                     or SignerOperations.GetLocalVerificationNonce2 or SignerOperations.GetLocalVerificationNonce3;

    public ReconciliationResponse Reconcile(SigningRequest request)
    {
        lock (_gate)
        {
            EnsureHealthy();
            if (_requests.TryGetValue(request.RequestId, out var receipt))
            {
                if (receipt.Fingerprint != Fingerprint(request))
                    throw new ArgumentException("Request ID was reused for another operation.");
                return new ReconciliationResponse
                {
                    Outcome = receipt.Outcome,
                    Response = receipt.Outcome == RequestOutcome.Completed
                        ? new SigningResponse { Payload = ByteString.CopyFrom(receipt.Response) } : null
                };
            }
            return new ReconciliationResponse
            { Outcome = SupportsReconciliation(request.Operation) ? RequestOutcome.NotFound : RequestOutcome.Unsupported };
        }
    }

    public object?[] ExecuteRequest(SigningRequest request, JsonElement[] args, Func<object?[]> execute)
    {
        lock (_gate)
        {
            EnsureHealthy();
            var known = Reconcile(request);
            if (known.Outcome == RequestOutcome.Completed)
                return SignerWire.Decode(known.Response.Payload.ToByteArray()).Cast<object?>().ToArray();
            if (known.Outcome is RequestOutcome.Unknown or RequestOutcome.Invalidated)
                throw new SignerException("Request outcome is unknown or invalidated; reconcile before continuing.");
            if (!SupportsReconciliation(request.Operation))
                return request.Operation >= 100 ? execute() : ExecuteCore(request.Operation, args);
            // Reserve worst-case response capacity before anything can allocate or mutate safety state.
            if (_requests.Count >= MaxRequestReceipts || _receiptBytes > MaxReceiptResponseBytes - RemoteSignerOptions.MaxMessageBytes)
                throw new SignerException("Durable request receipt capacity is exhausted; operator maintenance is required.");
            var fingerprint = Fingerprint(request);
            var allocates = request.Operation is SignerOperations.CreateNewChannel
                or SignerOperations.ReserveChannelKeyIndex or SignerOperations.EnsureLastUsedChannelIndexAtLeast;
            if (allocates)
            {
                Persist(new JournalEntry(request.Operation, request.Payload.ToByteArray(), [], request.RequestId,
                                         fingerprint, Pending: true));
                _requests.Add(request.RequestId, new RequestReceipt(fingerprint, RequestOutcome.Unknown, [], null));
            }
            // ExecuteCore writes the safety mutation and the receipt in the SAME fsynced record.
            if (!allocates)
                return ExecuteCore(request.Operation, args, request.RequestId, fingerprint);
            var result = execute();
            var entry = new JournalEntry(request.Operation, request.Payload.ToByteArray(), SignerWire.Encode(result),
                                         request.RequestId, fingerprint);
            Persist(entry);
            RememberReceipt(entry);
            return result;
        }
    }

    private void EnsureHealthy()
    {
        if (_failed)
            throw new IOException("Signer journal failed; restart and reconcile signer state before signing.");
    }

    private static string Fingerprint(SigningRequest request) => Convert.ToHexString(SHA256.HashData(request.ToByteArray()));

    public DurableSignerState(ILightningSigner signer, string path, string network)
    {
        _signer = signer;
        ArgumentException.ThrowIfNullOrWhiteSpace(network);
        var chain = Domain.Protocol.ValueObjects.BitcoinNetwork.Resolve(network).ChainHash;
        _networkIdentity = chain.Value.ToArray();
        var existing = File.Exists(path);
        if (existing && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The signer journal must not be a symbolic link.");
        var fileOptions = new FileStreamOptions
        {
            Mode = FileMode.OpenOrCreate,
            Access = FileAccess.ReadWrite,
            Share = FileShare.Read,
            Options = FileOptions.WriteThrough
        };
        if (!OperatingSystem.IsWindows())
            fileOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        _stream = new FileStream(path, fileOptions);
        try
        {
            if (!OperatingSystem.IsWindows()
             && (File.GetUnixFileMode(path) & ~(UnixFileMode.UserRead | UnixFileMode.UserWrite)) != 0)
                throw new IOException("The signer journal requires owner-only read/write permissions.");
            BindIdentity(existing);
            Restore();
        }
        catch
        {
            _stream.Dispose();
            throw;
        }
    }

    public object?[] Execute(uint operation, JsonElement[] args) => ExecuteCore(operation, args);

    private object?[] ExecuteCore(uint operation, JsonElement[] args, string? requestId = null, string? fingerprint = null)
    {
        lock (_gate)
        {
            if (_failed)
                throw new IOException("Signer journal failed; restart and reconcile signer state before signing.");
            if (args.Length > 0 && args[0].ValueKind == JsonValueKind.String
             && operation is not (SignerOperations.GetChannelBasepoints or SignerOperations.GetPerCommitmentPoint
                                  or SignerOperations.SignNodeMessage or SignerOperations.SignNodeMessageBip340
                                  or SignerOperations.SignLightningMessage or SignerOperations.VerifyNodeMessage
                                  or SignerOperations.GetBolt12PayerId or SignerOperations.SignBolt12
                                  or SignerOperations.ComputeSilentPaymentOutputs))
            {
                var channel = SignerWire.Read<ChannelId>(args[0]);
                if (_retired.Contains(channel))
                {
                    if (operation == SignerOperations.UnregisterChannel)
                    {
                        if (requestId is not null)
                        {
                            var acknowledged = new JournalEntry(operation, SignerWire.Encode(args.Cast<object?>().ToArray()),
                                SignerWire.Encode([]), requestId, fingerprint);
                            Persist(acknowledged);
                            RememberReceipt(acknowledged);
                        }
                        return [];
                    }
                    throw new SignerException("The signer has retired this channel; its safety history cannot be reset.",
                                              channel);
                }
            }

            var payload = SignerWire.Encode(args.Cast<object?>().ToArray());
            var payloadHash = Convert.ToHexString(SHA256.HashData(payload));
            var session = SessionKey(operation, args);
            if (session is not null && _dataLoss.Contains(SignerWire.Read<ChannelId>(args[0])))
                throw new SignerException("Refusing to return a signing result after channel data loss.",
                                          SignerWire.Read<ChannelId>(args[0]));
            if (session is not null && _sessions.TryGetValue(session, out var previous))
            {
                if (previous.PayloadHash != payloadHash)
                    throw new SignerException("Refusing a conflicting request for an already consumed signing nonce.");
                if (requestId is not null)
                {
                    var repeated = new JournalEntry(operation, payload, previous.Response, requestId, fingerprint);
                    Persist(repeated);
                    RememberReceipt(repeated);
                }
                return SignerWire.Decode(previous.Response).Cast<object?>().ToArray();
            }

            var result = SignerDispatcher.Execute(_signer, operation, args);
            if (IsStateChange(operation) || session is not null || requestId is not null)
            {
                var response = session is null && requestId is null ? Array.Empty<byte>() : SignerWire.Encode(result);
                var entry = new JournalEntry(operation, payload, response, requestId, fingerprint);
                try
                {
                    Append(entry);
                    Remember(entry, args, session, payloadHash);
                    RememberReceipt(entry);
                }
                catch
                {
                    // The in-memory operation may have happened. Do not allow another call after a failed save.
                    _failed = true;
                    throw;
                }
            }
            return result;
        }
    }

    private void BindIdentity(bool existingFile)
    {
        var identity = (byte[])_signer.GetNodePublicKey();
        var header = new byte[8 + identity.Length + _networkIdentity.Length];
        "NLTGSIG2"u8.CopyTo(header);
        identity.CopyTo(header, 8);
        _networkIdentity.CopyTo(header, 8 + identity.Length);
        if (_stream.Length == 0)
        {
            if (existingFile)
                throw new InvalidDataException("The existing signer journal is empty or truncated.");
            _stream.Write(header);
            _stream.Flush(flushToDisk: true);
            return;
        }
        var existing = new byte[header.Length];
        _stream.ReadExactly(existing);
        if (!existing.AsSpan().SequenceEqual(header))
            throw new InvalidDataException("Signer journal version, key identity or Bitcoin network does not match.");
    }

    private void Restore()
    {
        Span<byte> header = stackalloc byte[4];
        while (_stream.Position < _stream.Length)
        {
            _stream.ReadExactly(header);
            var size = BinaryPrimitives.ReadInt32LittleEndian(header);
            if (size is <= 0 or > MaxRecordBytes)
                throw new InvalidDataException("Invalid signer journal record size.");
            var bytes = new byte[size];
            _stream.ReadExactly(bytes);
            var entry = JsonSerializer.Deserialize<JournalEntry>(bytes)
                     ?? throw new InvalidDataException("Invalid signer journal record.");
            var args = SignerWire.Decode(entry.Payload);
            if (args.Length != SignerOperations.ArgumentCount(entry.Operation))
                throw new InvalidDataException("Invalid signer journal operation arguments.");
            if (entry.Pending)
            {
                RememberReceipt(entry);
                continue;
            }
            if (IsBroadcast(entry.Operation))
            {
                var index = entry.Operation == SignerOperations.SignLocalCommitmentForBroadcast ? 1 : 2;
                _signer.MarkBroadcastSigned(SignerWire.Read<ChannelId>(args[0]), SignerWire.Read<ulong>(args[index]));
            }
            else if (IsStateChange(entry.Operation))
            {
                SignerDispatcher.Execute(_signer, entry.Operation, args);
            }
            Remember(entry, args, SessionKey(entry.Operation, args),
                     Convert.ToHexString(SHA256.HashData(entry.Payload)));
            RememberReceipt(entry);
        }
    }

    private void Persist(JournalEntry entry)
    {
        try { Append(entry); }
        catch { _failed = true; throw; }
    }

    private void RememberReceipt(JournalEntry entry)
    {
        if (entry.RequestId is null) return;
        if (entry.Fingerprint is null)
            throw new InvalidDataException("Missing signer request fingerprint.");
        if (_requests.TryGetValue(entry.RequestId, out var previous))
        {
            if (previous.Fingerprint != entry.Fingerprint || previous.Outcome != RequestOutcome.Unknown)
                throw new InvalidDataException("Conflicting signer request receipt.");
        }
        else if (_requests.Count >= MaxRequestReceipts)
            throw new InvalidDataException("Signer request receipt limit exceeded.");
        _requests[entry.RequestId] = new RequestReceipt(entry.Fingerprint,
            entry.Pending ? RequestOutcome.Unknown : RequestOutcome.Completed, entry.Response,
            entry.Operation is SignerOperations.CreateNewChannel or SignerOperations.ReserveChannelKeyIndex
                or SignerOperations.EnsureLastUsedChannelIndexAtLeast ? (ChannelId?)null
                : SignerWire.Read<ChannelId>(SignerWire.Decode(entry.Payload)[0]));
        _receiptBytes += entry.Response.Length;
        if (_receiptBytes > MaxReceiptResponseBytes)
            throw new InvalidDataException("Signer request response limit exceeded.");
    }

    private void Append(JournalEntry entry)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(entry);
        if (bytes.Length > MaxRecordBytes)
            throw new IOException("Signer journal record exceeds the size limit.");
        Span<byte> header = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        _stream.Write(header);
        _stream.Write(bytes);
        _stream.Flush(flushToDisk: true);
    }

    private void Remember(JournalEntry entry, JsonElement[] args, string? session, string payloadHash)
    {
        if (entry.Operation == SignerOperations.MarkDataLoss
         || entry.Operation == SignerOperations.RegisterChannel
         && SignerWire.Read<ChannelSigningInfo>(args[1]).DataLossDetected)
        {
            _dataLoss.Add(SignerWire.Read<ChannelId>(args[0]));
            InvalidateReceipts(SignerWire.Read<ChannelId>(args[0]));
        }
        if (entry.Operation == SignerOperations.UnregisterChannel)
        {
            _retired.Add(SignerWire.Read<ChannelId>(args[0]));
            InvalidateReceipts(SignerWire.Read<ChannelId>(args[0]));
        }
        if (session is not null)
        {
            if (_sessions.TryGetValue(session, out var previous))
            {
                if (previous.PayloadHash != payloadHash || !previous.Response.AsSpan().SequenceEqual(entry.Response))
                    throw new InvalidDataException("Conflicting signer nonce session history.");
            }
            else _sessions.Add(session, (payloadHash, entry.Response));
        }
    }

    private void InvalidateReceipts(ChannelId channel)
    {
        foreach (var (id, receipt) in _requests)
            if (receipt.Outcome == RequestOutcome.Completed && receipt.Channel == channel)
            {
                _receiptBytes -= receipt.Response.Length;
                _requests[id] = receipt with { Outcome = RequestOutcome.Invalidated, Response = [] };
            }
    }

    private static bool IsBroadcast(uint operation) => operation is
        SignerOperations.SignLocalCommitmentForBroadcast or SignerOperations.SignLocalCommitmentForBroadcast2
        or SignerOperations.SignLocalCommitmentForBroadcast3;

    private static bool IsStateChange(uint operation) => IsBroadcast(operation) || operation is
        SignerOperations.RegisterChannel or SignerOperations.UnregisterChannel or SignerOperations.AdvanceLocalCommitment
        or SignerOperations.MarkDataLoss or SignerOperations.MarkBroadcastSigned or SignerOperations.RegisterFunding
        or SignerOperations.MarkSpliceCommitmentPersisted or SignerOperations.LockFunding or SignerOperations.LockFunding2;

    private string? SessionKey(uint operation, JsonElement[] args)
    {
        object?[]? context = operation switch
        {
            // The holder verification nonce is deterministic for this channel/funding/commitment number.
            SignerOperations.SignLocalCommitmentForBroadcast2 =>
                [operation, SignerWire.Read<ChannelId>(args[0]), _signer.GetLocalVerificationNonce(SignerWire.Read<ChannelId>(args[0]),
                    SignerWire.Read<Domain.Bitcoin.ValueObjects.TxId?>(args[1]), SignerWire.Read<ulong>(args[2]))],
            // These consume a just-in-time secret nonce, identified by its public half.
            SignerOperations.SignClosingAsClosee => [operation, SignerWire.Read<ChannelId>(args[0]), SignerWire.Read<Domain.Crypto.ValueObjects.MusigPublicNonce>(args[2])],
            SignerOperations.SignSpliceSharedInputPartial => [operation, SignerWire.Read<ChannelId>(args[0]), SignerWire.Read<Domain.Crypto.ValueObjects.MusigPublicNonce>(args[5])],
            _ => null
        };
        return context is null ? null : Convert.ToHexString(SHA256.HashData(SignerWire.Encode(context)));
    }

    public void Dispose() => _stream.Dispose();

    private sealed record JournalEntry(uint Operation, byte[] Payload, byte[] Response,
                                       string? RequestId = null, string? Fingerprint = null, bool Pending = false);
    private sealed record RequestReceipt(string Fingerprint, RequestOutcome Outcome, byte[] Response, ChannelId? Channel);
}