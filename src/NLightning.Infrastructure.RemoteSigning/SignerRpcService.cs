using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Google.Protobuf;
using Grpc.Core;
using NLightning.Domain.Bitcoin.Enums;
using NLightning.Domain.Bitcoin.Interfaces;
using NLightning.Domain.Exceptions;
using NLightning.Domain.Protocol.Enums;
using NLightning.Domain.Protocol.Interfaces;
using NLightning.Signing.Contracts;

namespace NLightning.Infrastructure.RemoteSigning;

/// <summary>Fixed allowlist dispatcher. Calls are serialized, including registration guards and nonce consumption.</summary>
public sealed class SignerRpcService(ILightningSigner signer, ISecureKeyManager keys, IUtxoMemoryRepository wallet,
                                     RemoteSignerOptions options, DurableSignerState? durableState = null) : SignerRpc.SignerRpcBase
{
    private readonly RemoteSignerOptions _options = ValidateOptions(options);
    private readonly Lock _gate = new();
    private readonly Dictionary<string, (byte[] Fingerprint, SigningResponse Response)> _replies = new();
    private readonly Queue<string> _replyOrder = new();
    private long _replyBytes;
    private WalletSnapshot? _walletContext;
    public override Task<SigningResponse> Execute(SigningRequest request, ServerCallContext context)
    {
        var supplied = context.RequestHeaders.GetValue("x-signer-token") ?? "";
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(supplied), Encoding.UTF8.GetBytes(_options.AuthToken)))
            throw new RpcException(new Status(StatusCode.Unauthenticated, "Signer authentication failed."));
        if (request.Version != 1 || !Guid.TryParseExact(request.RequestId, "N", out _) || request.Payload.Length > RemoteSignerOptions.MaxMessageBytes - 1024)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Invalid signer protocol version, request ID or payload size."));
        lock (_gate)
        {
            var fingerprint = SHA256.HashData(request.ToByteArray());
            if (_replies.TryGetValue(request.RequestId, out var previous))
            {
                if (!fingerprint.AsSpan().SequenceEqual(previous.Fingerprint)) throw new RpcException(new Status(StatusCode.InvalidArgument, "Request ID was reused for another operation."));
                return Task.FromResult(previous.Response);
            }
            try
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                var args = SignerWire.Decode(request.Payload.ToByteArray());
                if (args.Length != SignerOperations.ArgumentCount(request.Operation)) throw new ArgumentException("Unexpected number of signer arguments.");
                if (request.Operation is >= 27 and <= 30)
                {
                    _walletContext?.Clear(wallet);
                    _walletContext = SignerWire.Read<WalletSnapshot>(args[^1]); _walletContext.Apply(wallet);
                }
                object?[] values;
                if (request.Operation == 0) values = [new SignerIdentity(_options.Network, keys.GetNodePubKey(), keys.ChannelKeyPath, keys.HeightOfBirth)];
                else if (request.Operation >= 100) values = KeyOperation(request.Operation, args);
                else values = durableState?.Execute(request.Operation, args) ?? SignerDispatcher.Execute(signer, request.Operation, args);
                if (request.Operation is SignerOperations.MarkDataLoss or SignerOperations.UnregisterChannel
                 || request.Operation == SignerOperations.RegisterChannel
                 && SignerWire.Read<NLightning.Domain.Channels.ValueObjects.ChannelSigningInfo>(args[1]).DataLossDetected)
                {
                    _replies.Clear(); _replyOrder.Clear(); _replyBytes = 0;
                }
                var response = new SigningResponse { Payload = ByteString.CopyFrom(SignerWire.Encode(values)) };
                _replies.Add(request.RequestId, (fingerprint, response)); _replyOrder.Enqueue(request.RequestId);
                _replyBytes += response.Payload.Length;
                while (_replyOrder.Count > 4096 || _replyBytes > 64 * 1024 * 1024)
                {
                    var oldest = _replyOrder.Dequeue(); _replyBytes -= _replies[oldest].Response.Payload.Length; _replies.Remove(oldest);
                }
                return Task.FromResult(response);
            }
            catch (CryptographicException) { throw new RpcException(new Status(StatusCode.DataLoss, "Signer authenticated data verification failed.")); }
            catch (SignerException ex) { throw new RpcException(new Status(StatusCode.FailedPrecondition, ex.Message)); }
            catch (Exception ex) when (ex is ArgumentException or JsonException or FormatException or IndexOutOfRangeException)
            { throw new RpcException(new Status(StatusCode.InvalidArgument, "Invalid signer operation arguments.")); }
        }
    }
    private static RemoteSignerOptions ValidateOptions(RemoteSignerOptions options)
    {
        options.Validate(); return options;
    }
    private object?[] KeyOperation(uint op, JsonElement[] a) => op switch
    {
        100 => [ComputeSharedSecret(SignerWire.Read<byte[]>(a[0]))],
        101 => [keys.SignBolt11Invoice(SignerWire.Read<string>(a[0]), SignerWire.Read<byte[]>(a[1]))],
        102 => [keys.GetWalletPublicKey(SignerWire.Read<uint>(a[0]), SignerWire.Read<bool>(a[1]), SignerWire.Read<AddressType>(a[2]))],
        103 => [keys.EncryptNodeData(SignerWire.Read<NodeDataPurpose>(a[0]), SignerWire.Read<byte[]>(a[1]), SignerWire.Read<byte[]>(a[2]), SignerWire.Read<byte[]>(a[3]))],
        104 => [keys.DecryptNodeData(SignerWire.Read<NodeDataPurpose>(a[0]), SignerWire.Read<byte[]>(a[1]), SignerWire.Read<byte[]>(a[2]), SignerWire.Read<byte[]>(a[3]))],
        105 => [keys.ComputeOfferPathId(SignerWire.Read<byte[]>(a[0]))],
        106 => [keys.ReserveChannelKeyIndex()],
        107 => [keys.EnsureLastUsedChannelIndexAtLeast(SignerWire.Read<uint>(a[0]))],
        _ => throw new ArgumentException("Unknown key operation.")
    };
    private byte[] ComputeSharedSecret(byte[] publicKey) { var result = new byte[32]; keys.ComputeNodeSharedSecret(publicKey, result); return result; }
}