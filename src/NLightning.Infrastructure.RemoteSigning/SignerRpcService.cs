using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Google.Protobuf;
using Grpc.Core;
using NLightning.Domain.Bitcoin.Enums;
using NLightning.Domain.Bitcoin.Interfaces;
using NLightning.Domain.Bitcoin.SilentPayments.Interfaces;
using NLightning.Domain.Crypto.KeyRing;
using NLightning.Domain.Exceptions;
using NLightning.Domain.Protocol.Enums;
using NLightning.Domain.Protocol.Interfaces;
using NLightning.Signing.Contracts;

namespace NLightning.Infrastructure.RemoteSigning;

/// <summary>Fixed allowlist dispatcher. Calls are serialized, including registration guards and nonce consumption.</summary>
public sealed class SignerRpcService(ILightningSigner signer, ISecureKeyManager keys, IUtxoMemoryRepository wallet,
                                     RemoteSignerOptions options, DurableSignerState? durableState = null,
                                     ISwapSigner? swapSigner = null,
                                     NativeAuthorizedSignerExecutor? authorizedExecutor = null,
                                     INativeSignerWriterCredentialVerifier? writerCredentials = null) : SignerRpc.SignerRpcBase
{
    private readonly RemoteSignerOptions _options = ValidateOptions(options);
    private readonly Lock _gate = new();
    private readonly Dictionary<string, (byte[] Fingerprint, SigningResponse Response)> _replies = new();
    private readonly Queue<string> _replyOrder = new();
    private long _replyBytes;
    private WalletSnapshot? _walletContext;
    public override Task<SigningResponse> Execute(SigningRequest request, ServerCallContext context)
    {
        Authenticate(context);
        ValidateRequest(request);
        ValidateContext(request);
        lock (_gate)
        {
            if (authorizedExecutor is null || request.Operation == SignerOperations.Identity)
                return ExecuteNative(request, context);
            try
            {
                ValidateAuthorityEnrollment();
                var (writer, epoch) = ReadWriter(context);
                var result = authorizedExecutor.Execute(request, writer, epoch,
                    () => ExecuteNative(request, context).GetAwaiter().GetResult().ToByteArray());
                return Task.FromResult(SigningResponse.Parser.ParseFrom(result.Response));
            }
            catch (UnauthorizedAccessException)
            { throw new RpcException(new Status(StatusCode.PermissionDenied, "Signer writer or owner authorization failed.")); }
            catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or NotSupportedException)
            { throw new RpcException(new Status(StatusCode.FailedPrecondition, "Signer authority requires reconciliation or an authorized policy.")); }
        }
    }

    private void ValidateAuthorityEnrollment() => authorizedExecutor!.ValidateEnrollment(
        NativeSignerBinding.FromContext(new NLightning.Domain.Signing.NodeSigningContext(
            _options.NodeId, _options.OwnerId, _options.SignerId, _options.Network, keys.GetNodePubKey())));

    private (string Writer, long Epoch) ReadWriter(ServerCallContext context)
    {
        var writer = context.RequestHeaders.GetValue("x-nltg-writer-id");
        var epoch = context.RequestHeaders.GetValue("x-nltg-writer-epoch");
        if (string.IsNullOrWhiteSpace(writer) || !long.TryParse(epoch, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed) || parsed <= 0)
            throw new UnauthorizedAccessException("Authenticated execution metadata is required.");
        if (writerCredentials is null)
            throw new UnauthorizedAccessException("A signer-installed writer credential verifier is required.");
        writerCredentials.Verify(NativeSignerBinding.FromContext(new NLightning.Domain.Signing.NodeSigningContext(
            _options.NodeId, _options.OwnerId, _options.SignerId, _options.Network, keys.GetNodePubKey())),
            writer, parsed, context.RequestHeaders.GetValue("x-nltg-writer-token") ?? "");
        return (writer, parsed);
    }

    private Task<SigningResponse> ExecuteNative(SigningRequest request, ServerCallContext context)
    {
        Authenticate(context);
        ValidateRequest(request);
        ValidateContext(request);
        lock (_gate)
        {
            var fingerprint = SHA256.HashData(request.ToByteArray());
            if (_replies.TryGetValue(request.RequestId, out var previous))
            {
                if (!fingerprint.AsSpan().SequenceEqual(previous.Fingerprint)) throw new RpcException(new Status(StatusCode.InvalidArgument, "Request ID was reused for another operation."));
                if (durableState is null || !DurableSignerState.SupportsReconciliation(request.Operation))
                    return Task.FromResult(previous.Response);
            }
            try
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                // Check durable identities even for identity/unsupported operations before any side effects.
                var known = durableState?.Reconcile(request);
                if (known?.Outcome == RequestOutcome.Completed)
                    return Task.FromResult(known.Response);
                var args = SignerWire.Decode(request.Payload.ToByteArray());
                if (args.Length != SignerOperations.ArgumentCount(request.Operation)) throw new ArgumentException("Unexpected number of signer arguments.");
                if (request.Operation is >= 27 and <= 30 or SignerOperations.ComputeSilentPaymentOutputs)
                {
                    _walletContext?.Clear(wallet);
                    _walletContext = SignerWire.Read<WalletSnapshot>(args[^1]); _walletContext.Apply(wallet);
                }
                object?[] values;
                if (request.Operation == 0) values = [new SignerIdentity(_options.Network, keys.GetNodePubKey(), keys.ChannelKeyPath, keys.HeightOfBirth)];
                else if (durableState is not null)
                    values = durableState.ExecuteRequest(request, args, () => Dispatch(request.Operation, args));
                else values = Dispatch(request.Operation, args);
                if (request.Operation is SignerOperations.MarkDataLoss or SignerOperations.UnregisterChannel
                 || request.Operation == SignerOperations.RegisterChannel
                 && SignerWire.Read<NLightning.Domain.Channels.ValueObjects.ChannelSigningInfo>(args[1]).DataLossDetected)
                {
                    _replies.Clear(); _replyOrder.Clear(); _replyBytes = 0;
                }
                // Send the exact persisted bytes, including for fresh requests that reuse a consumed session.
                var response = durableState is not null && DurableSignerState.SupportsReconciliation(request.Operation)
                    ? durableState.Reconcile(request).Response
                    : new SigningResponse { Payload = ByteString.CopyFrom(SignerWire.Encode(values)) };
                if (durableState is null || !DurableSignerState.SupportsReconciliation(request.Operation))
                {
                    _replies.Add(request.RequestId, (fingerprint, response)); _replyOrder.Enqueue(request.RequestId);
                    _replyBytes += response.Payload.Length;
                    while (_replyOrder.Count > 4096 || _replyBytes > 64 * 1024 * 1024)
                    {
                        var oldest = _replyOrder.Dequeue(); _replyBytes -= _replies[oldest].Response.Payload.Length; _replies.Remove(oldest);
                    }
                }
                return Task.FromResult(response);
            }
            catch (CryptographicException) { throw new RpcException(new Status(StatusCode.DataLoss, "Signer authenticated data verification failed.")); }
            catch (SignerException ex) { throw new RpcException(new Status(StatusCode.FailedPrecondition, ex.Message)); }
            catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
            { throw new RpcException(new Status(StatusCode.FailedPrecondition, "Signer safety state requires reconciliation or this operation is unavailable.")); }
            catch (Exception ex) when (ex is ArgumentException or JsonException or FormatException or IndexOutOfRangeException)
            { throw new RpcException(new Status(StatusCode.InvalidArgument, "Invalid signer operation arguments.")); }
        }
    }
    public override Task<ReconciliationResponse> Reconcile(SigningRequest request, ServerCallContext context)
    {
        Authenticate(context);
        ValidateRequest(request);
        ValidateContext(request);
        lock (_gate)
        {
            if (authorizedExecutor is null) return ReconcileNative(request, context);
            try
            {
                ValidateAuthorityEnrollment();
                var (writer, epoch) = ReadWriter(context);
                return Task.FromResult(authorizedExecutor.AuthorizeReconciliation(request, writer, epoch,
                    () => ReconcileNative(request, context).GetAwaiter().GetResult()));
            }
            catch (UnauthorizedAccessException)
            { throw new RpcException(new Status(StatusCode.PermissionDenied, "Signer writer authorization failed.")); }
            catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or NotSupportedException)
            { throw new RpcException(new Status(StatusCode.FailedPrecondition, "Signer authority requires reconciliation.")); }
        }
    }

    private Task<ReconciliationResponse> ReconcileNative(SigningRequest request, ServerCallContext context)
    {
        Authenticate(context);
        ValidateRequest(request);
        ValidateContext(request);
        lock (_gate)
        {
            try
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                _ = SignerOperations.ArgumentCount(request.Operation);
                return Task.FromResult(durableState?.Reconcile(request)
                    ?? new ReconciliationResponse { Outcome = RequestOutcome.Unsupported });
            }
            catch (ArgumentException)
            { throw new RpcException(new Status(StatusCode.InvalidArgument, "Invalid or conflicting signer reconciliation request.")); }
        }
    }

    private object?[] Dispatch(uint operation, JsonElement[] args)
    {
        if (SwapSignerOperations.Contains(operation))
            return SwapSignerDispatcher.Execute(swapSigner ?? throw new SignerException("Native swap signer is not configured."), operation, args);
        return operation >= 100 ? KeyOperation(operation, args) : SignerDispatcher.Execute(signer, operation, args);
    }

    private void ValidateContext(SigningRequest request)
    {
        if (request.NodeId != _options.NodeId || request.OwnerId != _options.OwnerId
         || request.SignerId != _options.SignerId
         || !string.Equals(request.Network, _options.Network, StringComparison.OrdinalIgnoreCase))
            throw new RpcException(new Status(StatusCode.PermissionDenied, "Signer context does not match its immutable enrollment."));
    }

    private void Authenticate(ServerCallContext context)
    {
        var supplied = context.RequestHeaders.GetValue("x-signer-token") ?? "";
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(supplied), Encoding.UTF8.GetBytes(_options.AuthToken)))
            throw new RpcException(new Status(StatusCode.Unauthenticated, "Signer authentication failed."));
    }

    private static void ValidateRequest(SigningRequest request)
    {
        if (request.Version != 1 || !Guid.TryParseExact(request.RequestId, "N", out _) || request.Payload.Length > RemoteSignerOptions.MaxMessageBytes - 1024)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Invalid signer protocol version, request ID or payload size."));
    }

    private static RemoteSignerOptions ValidateOptions(RemoteSignerOptions options)
    {
        options.Validate();
        return new RemoteSignerOptions
        {
            SocketPath = options.SocketPath,
            AuthToken = options.AuthToken,
            Network = options.Network,
            TimeoutSeconds = options.TimeoutSeconds,
            ExpectedNodePublicKey = options.ExpectedNodePublicKey,
            NodeId = options.NodeId,
            OwnerId = options.OwnerId,
            SignerId = options.SignerId
        };
    }
    private object?[] KeyOperation(uint op, JsonElement[] a) => op switch
    {
        >= NativeSilentPaymentOperations.Metadata and <= NativeSilentPaymentOperations.LabelTweak => NativeSilentPaymentOperations.Execute(keys as ISilentPaymentKeySource ?? throw new SignerException("Silent payment keys are not configured."), op, a),
        100 => [ComputeSharedSecret(SignerWire.Read<byte[]>(a[0]))],
        101 => [keys.SignBolt11Invoice(SignerWire.Read<string>(a[0]), SignerWire.Read<byte[]>(a[1]))],
        102 => [keys.GetWalletPublicKey(SignerWire.Read<uint>(a[0]), SignerWire.Read<bool>(a[1]), SignerWire.Read<AddressType>(a[2]))],
        103 => [keys.EncryptNodeData(SignerWire.Read<NodeDataPurpose>(a[0]), SignerWire.Read<byte[]>(a[1]), SignerWire.Read<byte[]>(a[2]), SignerWire.Read<byte[]>(a[3]))],
        104 => [keys.DecryptNodeData(SignerWire.Read<NodeDataPurpose>(a[0]), SignerWire.Read<byte[]>(a[1]), SignerWire.Read<byte[]>(a[2]), SignerWire.Read<byte[]>(a[3]))],
        105 => [keys.ComputeOfferPathId(SignerWire.Read<byte[]>(a[0]))],
        106 => [keys.ReserveChannelKeyIndex()],
        107 => [keys.EnsureLastUsedChannelIndexAtLeast(SignerWire.Read<uint>(a[0]))],
        SignerOperations.GetDepositAccount => [keys.GetDepositAccount(SignerWire.Read<AddressType>(a[0]))],
        SignerOperations.GetDepositAccount2 => [keys.GetDepositAccount(SignerWire.Read<AddressType>(a[0]), SignerWire.Read<uint>(a[1]))],
        SignerOperations.GetKeyRingPublicKey => [keys.GetKeyRingPublicKey(SignerWire.Read<int>(a[0]), SignerWire.Read<int>(a[1]))],
        _ => throw new ArgumentException("Unknown key operation.")
    };
    private byte[] ComputeSharedSecret(byte[] publicKey) { var result = new byte[32]; keys.ComputeNodeSharedSecret(publicKey, result); return result; }
}