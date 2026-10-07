using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;

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

    public object?[] Execute(uint operation, JsonElement[] args)
    {
        lock (_gate)
        {
            if (_failed)
                throw new IOException("Signer journal failed; restart and reconcile signer state before signing.");
            if (args.Length > 0 && args[0].ValueKind == JsonValueKind.String
             && operation is not (SignerOperations.GetChannelBasepoints or SignerOperations.GetPerCommitmentPoint
                                  or SignerOperations.SignNodeMessage or SignerOperations.SignNodeMessageBip340
                                  or SignerOperations.SignLightningMessage or SignerOperations.VerifyNodeMessage
                                  or SignerOperations.GetBolt12PayerId or SignerOperations.SignBolt12))
            {
                var channel = SignerWire.Read<ChannelId>(args[0]);
                if (_retired.Contains(channel))
                {
                    if (operation == SignerOperations.UnregisterChannel)
                        return [];
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
                return SignerWire.Decode(previous.Response).Cast<object?>().ToArray();
            }

            var result = SignerDispatcher.Execute(_signer, operation, args);
            if (IsStateChange(operation) || session is not null)
            {
                var response = session is null ? Array.Empty<byte>() : SignerWire.Encode(result);
                var entry = new JournalEntry(operation, payload, response);
                try
                {
                    Append(entry);
                    Remember(entry, args, session, payloadHash);
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
        }
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
            _dataLoss.Add(SignerWire.Read<ChannelId>(args[0]));
        if (entry.Operation == SignerOperations.UnregisterChannel)
            _retired.Add(SignerWire.Read<ChannelId>(args[0]));
        if (session is not null)
            _sessions.Add(session, (payloadHash, entry.Response));
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
                [operation, args[0], _signer.GetLocalVerificationNonce(SignerWire.Read<ChannelId>(args[0]),
                    SignerWire.Read<Domain.Bitcoin.ValueObjects.TxId?>(args[1]), SignerWire.Read<ulong>(args[2]))],
            // These consume a just-in-time secret nonce, identified by its public half.
            SignerOperations.SignClosingAsClosee => [operation, args[0], args[2]],
            SignerOperations.SignSpliceSharedInputPartial => [operation, args[0], args[5]],
            _ => null
        };
        return context is null ? null : Convert.ToHexString(SHA256.HashData(SignerWire.Encode(context)));
    }

    public void Dispose() => _stream.Dispose();

    private sealed record JournalEntry(uint Operation, byte[] Payload, byte[] Response);
}