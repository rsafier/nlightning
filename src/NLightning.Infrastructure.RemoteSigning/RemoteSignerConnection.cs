using System.Buffers.Binary;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using NLightning.Domain.Exceptions;
using NLightning.Domain.Signing;
using NLightning.Domain.Signing.Recovery;
using NLightning.Signing.Contracts;
using SigningRequest = NLightning.Signing.Contracts.SigningRequest;

namespace NLightning.Infrastructure.RemoteSigning;

/// <summary>One authenticated HTTP/2 stream transport. Connector can later be replaced by a vsock stream.</summary>
public sealed class RemoteSignerConnection : IDisposable
{
    private readonly GrpcChannel _channel;
    private readonly SignerRpc.SignerRpcClient _client;
    private readonly RemoteSignerOptions _options;
    private IRemoteSigningRequestCapture? _workflowCapture;
    public SignerIdentity Identity { get; }
    public NodeSigningContext Context { get; }

    public RemoteSignerConnection(RemoteSignerOptions options)
        : this(options, SocketConnector(options.SocketPath)) { }

    public RemoteSignerConnection(RemoteSignerOptions options, Func<CancellationToken, ValueTask<Stream>> connector)
    {
        options.Validate();
        _options = new RemoteSignerOptions
        {
            SocketPath = options.SocketPath,
            AuthToken = options.AuthToken,
            Network = options.Network,
            TimeoutSeconds = options.TimeoutSeconds,
            ExpectedNodePublicKey = options.ExpectedNodePublicKey,
            NodeId = options.NodeId,
            OwnerId = options.OwnerId,
            SignerId = options.SignerId,
            WriterId = options.WriterId,
            WriterEpoch = options.WriterEpoch,
            WriterCredential = options.WriterCredential
        };
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = (_, cancellation) => connector(cancellation),
            EnableMultipleHttp2Connections = false
        };
        _channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions
        {
            HttpHandler = handler,
            MaxReceiveMessageSize = RemoteSignerOptions.MaxMessageBytes,
            MaxSendMessageSize = RemoteSignerOptions.MaxMessageBytes
        });
        _client = new SignerRpc.SignerRpcClient(_channel);
        try
        {
            Identity = SignerWire.Read<SignerIdentity>(Invoke(0)[0]);
            _ = new NBitcoin.PubKey((byte[])Identity.NodePublicKey);
            Context = new NodeSigningContext(_options.NodeId, _options.OwnerId, _options.SignerId,
                                            NLightning.Domain.Protocol.ValueObjects.BitcoinNetwork.Resolve(Identity.Network).Name,
                                            Identity.NodePublicKey);
            Context.Validate();
            if (!string.Equals(Identity.Network, _options.Network, StringComparison.OrdinalIgnoreCase))
                throw new RemoteSignerTransportException("Remote signer Bitcoin network does not match the node.");
            if (_options.ExpectedNodePublicKey is { Length: > 0 } expected &&
                !string.Equals(expected, Identity.NodePublicKey.ToString(), StringComparison.OrdinalIgnoreCase))
                throw new RemoteSignerTransportException("Remote signer identity does not match ExpectedNodePublicKey.");
        }
        catch { _channel.Dispose(); throw; }
    }

    /// <summary>No automatic retries: a timed out signing operation can have executed. Secret nonce results are never regenerated here.</summary>
    public JsonElement[] Invoke(uint operation, params object?[] arguments)
    {
        var request = PrepareForContext(operation, arguments);
        var capture = Volatile.Read(ref _workflowCapture);
        if (capture is null) return Execute(request);
        var material = new byte[sizeof(uint) + request.Payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(material, operation);
        request.Payload.Span.CopyTo(material.AsSpan(sizeof(uint)));
        var payload = capture.Execute(operation, request.ToByteArray(), SHA256.HashData(material),
            envelope => ToWorkflowStatus(Reconcile(SigningRequest.Parser.ParseFrom(envelope))),
            envelope => ExecutePayload(SigningRequest.Parser.ParseFrom(envelope)));
        return SignerWire.Decode(payload);
    }

    public void AttachWorkflowCapture(IRemoteSigningRequestCapture capture)
    {
        ArgumentNullException.ThrowIfNull(capture);
        if (Interlocked.CompareExchange(ref _workflowCapture, capture, null) is { } existing
         && !ReferenceEquals(existing, capture))
            throw new InvalidOperationException("This signer connection already has a workflow capture coordinator.");
    }
    public void DetachWorkflowCapture(IRemoteSigningRequestCapture capture)
    {
        ArgumentNullException.ThrowIfNull(capture);
        Interlocked.CompareExchange(ref _workflowCapture, null, capture);
    }
    internal static RemoteSigningRequestStatus ToWorkflowStatus(ReconciliationResponse response) => new(
        response.Outcome switch
        {
            RequestOutcome.Completed => RemoteSigningRequestOutcome.Completed,
            RequestOutcome.NotFound => RemoteSigningRequestOutcome.NotFound,
            RequestOutcome.Unknown => RemoteSigningRequestOutcome.Unknown,
            RequestOutcome.Invalidated => RemoteSigningRequestOutcome.Invalidated,
            _ => RemoteSigningRequestOutcome.Unsupported
        }, response.Outcome == RequestOutcome.Completed ? response.Response?.Payload.ToByteArray() : null);

    /// <summary>Create an envelope that can be saved by the caller before dispatch for explicit crash reconciliation.</summary>
    public static SigningRequest Prepare(uint operation, params object?[] arguments)
    {
        _ = SignerOperations.ArgumentCount(operation);
        var payload = SignerWire.Encode(arguments);
        if (payload.Length > RemoteSignerOptions.MaxMessageBytes - 1024) throw new ArgumentException("Signer request is too large.");

        return new SigningRequest
        {
            NodeId = NodeSigningContext.DefaultNodeId,
            OwnerId = NodeSigningContext.DefaultOwnerId,
            SignerId = NodeSigningContext.DefaultSignerId,
            Network = "regtest",
            Version = 1,
            RequestId = Guid.NewGuid().ToString("N"),
            Operation = operation,
            Payload = ByteString.CopyFrom(payload)
        };
    }

    public SigningRequest PrepareForContext(uint operation, params object?[] arguments)
    {
        var request = Prepare(operation, arguments);
        request.NodeId = _options.NodeId;
        request.OwnerId = _options.OwnerId;
        request.SignerId = _options.SignerId;
        request.Network = NLightning.Domain.Protocol.ValueObjects.BitcoinNetwork.Resolve(_options.Network).Name;
        return request;
    }

    private void ValidateContext(SigningRequest request)
    {
        if (request.NodeId != _options.NodeId || request.OwnerId != _options.OwnerId
         || request.SignerId != _options.SignerId
         || !string.Equals(request.Network, _options.Network, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The request belongs to another enrolled signing context.");
    }

    /// <summary>Dispatch once. Retrying a saved envelope is explicit; no transport retry is configured.</summary>
    public JsonElement[] Execute(SigningRequest request) => SignerWire.Decode(ExecutePayload(request));

    internal byte[] ExecutePayload(SigningRequest request)
    {
        // Snapshot the caller's mutable protobuf envelope so diagnostics bind to what was actually sent.
        request = request.Clone();
        ValidateContext(request);
        try
        {
            var response = _client.ExecuteAsync(request, Headers(),
                deadline: DateTime.UtcNow.AddSeconds(_options.TimeoutSeconds)).ResponseAsync.GetAwaiter().GetResult();
            return response.Payload.ToByteArray();
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.FailedPrecondition)
        { throw new SignerException(ex.Status.Detail, "Internal signer error"); }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.DataLoss)
        { throw new System.Security.Cryptography.CryptographicException(ex.Status.Detail, ex); }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.InvalidArgument)
        { throw new ArgumentException(ex.Status.Detail, ex); }
        catch (RpcException ex)
        { throw new RemoteSignerTransportException($"Remote signer operation {request.Operation} ({request.RequestId}) failed ({ex.StatusCode}); its outcome may be unknown.", ex, request); }
    }
    /// <summary>Authenticated lookup only. Unknown/invalidated/unsupported outcomes require operator or protocol recovery.</summary>
    public ReconciliationResponse Reconcile(SigningRequest request)
    {
        request = request.Clone();
        ValidateContext(request);
        try
        {
            return _client.ReconcileAsync(request, Headers(),
                deadline: DateTime.UtcNow.AddSeconds(_options.TimeoutSeconds)).ResponseAsync.GetAwaiter().GetResult();
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.InvalidArgument)
        { throw new ArgumentException(ex.Status.Detail, ex); }
        catch (RpcException ex)
        { throw new RemoteSignerTransportException($"Remote signer reconciliation for {request.RequestId} failed ({ex.StatusCode}).", ex, request); }
    }

    private Metadata Headers()
    {
        var headers = new Metadata { { "x-signer-token", _options.AuthToken } };
        if (_options.WriterId is not null)
        {
            headers.Add("x-nltg-writer-id", _options.WriterId);
            headers.Add("x-nltg-writer-epoch", _options.WriterEpoch!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            headers.Add("x-nltg-writer-token", _options.WriterCredential!);
        }
        return headers;
    }

    private static Func<CancellationToken, ValueTask<Stream>> SocketConnector(string path) =>
        cancellation => ConnectSocket(path, cancellation);

    private static async ValueTask<Stream> ConnectSocket(string path, CancellationToken cancellation)
    {
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try { await socket.ConnectAsync(new UnixDomainSocketEndPoint(path), cancellation); return new NetworkStream(socket, ownsSocket: true); }
        catch { socket.Dispose(); throw; }
    }
    public void Dispose() => _channel.Dispose();
}