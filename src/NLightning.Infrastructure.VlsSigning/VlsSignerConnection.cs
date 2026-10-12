using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using NLightning.Domain.Crypto.ValueObjects;
using NLightning.Domain.Signing.Recovery;

namespace NLightning.Infrastructure.VlsSigning;

public sealed record VlsSignerIdentity(CompactPubKey NodePublicKey, string Network, string Derivation);

public sealed class VlsSignerConnection
{
    private readonly VlsGatewayTransport _transport;
    private IRemoteSigningRequestCapture? _capture;
    public VlsSignerIdentity Identity { get; }
    private string? _walletXpub;
    public string WalletExtendedPublicKey => _walletXpub ??= Invoke(VlsOperations.PublicAccount, new JsonObject { ["op"] = "public_account" }).GetProperty("xpub").GetString()!;
    public VlsSignerConnection(VlsSignerOptions options)
    {
        _transport = new VlsGatewayTransport(options.SocketPath,
            options.AuthToken ?? VlsGatewayTransport.ReadCredential(options.TokenFile ?? throw new ArgumentException("VLS credential required.")), options.TimeoutSeconds);
        var identity = Invoke(VlsOperations.Identity, new JsonObject { ["op"] = "identity" });
        Identity = new(new CompactPubKey(Convert.FromHexString(identity.GetProperty("node_id").GetString()!)),
            identity.GetProperty("network").GetString()!, identity.GetProperty("derivation").GetString()!);
        if (!string.Equals(Identity.Derivation, "native", StringComparison.Ordinal) || !string.Equals(Identity.Network, options.Network, StringComparison.OrdinalIgnoreCase)
            || (options.ExpectedNodePublicKey is { Length: > 0 } expected && !string.Equals(expected, Identity.NodePublicKey.ToString(), StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("VLS identity or network does not match configured node.");
    }
    public JsonElement Invoke(uint operation, JsonObject command)
    {
        var envelope = EncodeRequest(Guid.NewGuid(), command);
        var capture = Volatile.Read(ref _capture);
        byte[] response;
        if (capture is null) response = ExecuteEnvelope(envelope);
        else
        {
            var json = JsonSerializer.SerializeToUtf8Bytes(command);
            var material = new byte[json.Length + sizeof(uint)];
            BinaryPrimitives.WriteUInt32LittleEndian(material, operation); json.CopyTo(material, sizeof(uint));
            response = capture.Execute(operation, envelope, SHA256.HashData(material), ReconcileEnvelope, ExecuteEnvelope);
        }
        using var doc = JsonDocument.Parse(response); return doc.RootElement.Clone();
    }
    public static byte[] EncodeRequest(Guid id, JsonObject command) => JsonSerializer.SerializeToUtf8Bytes(new JsonObject { ["id"] = id.ToString("N"), ["command"] = command.DeepClone() });
    public byte[] ExecuteEnvelope(byte[] envelope)
    {
        var request = JsonNode.Parse(envelope)!.AsObject();
        return _transport.InvokeBytes(Guid.ParseExact(request["id"]!.GetValue<string>(), "N"), request["command"]!.AsObject());
    }
    public RemoteSigningRequestStatus ReconcileEnvelope(byte[] envelope)
    {
        var request = JsonNode.Parse(envelope)!.AsObject();
        var bytes = _transport.InvokeBytes(Guid.NewGuid(), new JsonObject { ["op"] = "reconcile", ["id"] = request["id"]!.DeepClone(), ["command"] = request["command"]!.DeepClone() });
        using var doc = JsonDocument.Parse(bytes);
        var result = doc.RootElement;
        return result.GetProperty("status").GetString() switch
        {
            "completed" => new(RemoteSigningRequestOutcome.Completed, System.Text.Encoding.UTF8.GetBytes(result.GetProperty("result").GetRawText())),
            "not_found" => new(RemoteSigningRequestOutcome.NotFound),
            "invalidated" => new(RemoteSigningRequestOutcome.Invalidated),
            _ => new(RemoteSigningRequestOutcome.Unknown)
        };
    }

    public void AttachWorkflowCapture(IRemoteSigningRequestCapture capture)
    {
        if (Interlocked.CompareExchange(ref _capture, capture, null) is { } existing && !ReferenceEquals(existing, capture))
            throw new InvalidOperationException("VLS connection already has request capture.");
    }
    public void DetachWorkflowCapture(IRemoteSigningRequestCapture capture) => Interlocked.CompareExchange(ref _capture, null, capture);
}