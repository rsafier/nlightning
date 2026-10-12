using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NLightning.Daemon.Contracts.Provisioning;

/// <summary>
/// The key provisioning protocol of a locked node (NL-1349, <c>docs/agents/LOCKED_START.md</c>): one frame each way per
/// exchange over any byte stream (the provisioning Unix socket, stdin/stdout, later vsock). A frame is the 4 bytes
/// <c>NLKP</c>, the version byte (<see cref="Version1"/> or <see cref="Version"/>), a big-endian u32 body length (at
/// most <see cref="MaxBodyLength"/>) and a UTF-8 JSON body (<see cref="KeyProvisioningRequest"/> or
/// <see cref="KeyProvisioningResponse"/>).
/// </summary>
/// <remarks>
/// Version 2 (NL-1352) adds the file secrets of <see cref="KeyProvisioningSecrets"/> and the macaroon return. A request
/// is written as version 1 unless it uses a version 2 field, so a version 1 node still takes it; a version 1 node
/// refuses a version 2 frame before it unlocks, instead of ignoring secrets it does not know and writing their files. A
/// node answers in the request's version and refuses version 2 fields in a version 1 frame.
/// </remarks>
public static class KeyProvisioningProtocol
{
    /// <summary>The current version.</summary>
    public const byte Version = 2;

    /// <summary>The first version, still read and written (a request without version 2 fields).</summary>
    public const byte Version1 = 1;

    public const int MaxBodyLength = 1024 * 1024;
    public const int HeaderLength = 9;

    /// <summary>Request kind: report the node's state (locked or unlocked).</summary>
    public const string StatusKind = "status";

    /// <summary>Request kind: deliver key material.</summary>
    public const string UnlockKind = "unlock";

    /// <summary>Key material: an encrypted NLightning key file (its JSON, base64) and its password.</summary>
    public const string EncryptedKeyFileMaterial = "encrypted-key-file";

    public const string LockedState = "locked";
    public const string UnlockedState = "unlocked";

    private static ReadOnlySpan<byte> Magic => "NLKP"u8;

    /// <summary>Writes a request, as version 1 unless it uses a version 2 field.</summary>
    public static Task WriteRequestAsync(Stream stream, KeyProvisioningRequest request, CancellationToken ct) =>
        WriteFrameAsync(stream, request.RequiresVersion2() ? Version : Version1,
                        JsonSerializer.SerializeToUtf8Bytes(request,
                                                            KeyProvisioningJsonContext.Default.KeyProvisioningRequest),
                        ct);

    /// <summary>Writes a response in <paramref name="version"/> (the request's).</summary>
    public static Task WriteResponseAsync(Stream stream, KeyProvisioningResponse response, CancellationToken ct,
                                          byte version = Version1) =>
        WriteFrameAsync(stream, version,
                        JsonSerializer.SerializeToUtf8Bytes(response,
                                                            KeyProvisioningJsonContext.Default.KeyProvisioningResponse),
                        ct);

    /// <summary>Reads one request, or null when the stream ended before a frame started.</summary>
    /// <exception cref="InvalidDataException">The frame or its body is malformed.</exception>
    public static async Task<KeyProvisioningRequest?> ReadRequestAsync(Stream stream, CancellationToken ct)
    {
        var frame = await ReadFrameAsync(stream, ct);
        if (frame is not var (body, version))
            return null;

        KeyProvisioningRequest request;
        try
        {
            request = JsonSerializer.Deserialize(body, KeyProvisioningJsonContext.Default.KeyProvisioningRequest)
                   ?? throw new InvalidDataException("Empty provisioning request.");
        }
        catch (JsonException)
        {
            // The body may hold a password: never echo it
            throw new InvalidDataException("The provisioning request is not valid JSON.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(body);
        }

        if (version == Version1 && request.RequiresVersion2())
            throw new InvalidDataException("The request uses version 2 fields in a version 1 frame.");

        request.FrameVersion = version;
        return request;
    }

    /// <summary>Reads one response.</summary>
    /// <exception cref="InvalidDataException">The stream ended or the frame is malformed.</exception>
    public static async Task<KeyProvisioningResponse> ReadResponseAsync(Stream stream, CancellationToken ct)
    {
        var (body, _) = await ReadFrameAsync(stream, ct)
                     ?? throw new InvalidDataException("The node closed the connection without an answer.");
        try
        {
            return JsonSerializer.Deserialize(body, KeyProvisioningJsonContext.Default.KeyProvisioningResponse)
                ?? throw new InvalidDataException("Empty provisioning response.");
        }
        catch (JsonException)
        {
            throw new InvalidDataException("The provisioning response is not valid JSON.");
        }
    }

    /// <summary>Reads a secrets bundle (<see cref="KeyProvisioningSecrets"/>, camelCase JSON).</summary>
    /// <exception cref="InvalidDataException">The JSON is malformed.</exception>
    public static KeyProvisioningSecrets ParseSecrets(string json)
    {
        try
        {
            return JsonSerializer.Deserialize(json, KeyProvisioningJsonContext.Default.KeyProvisioningSecrets)
                ?? throw new InvalidDataException("The secrets file is empty.");
        }
        catch (JsonException)
        {
            // Never echo the content: it holds secrets
            throw new InvalidDataException("The secrets file is not valid JSON.");
        }
    }

    private static async Task WriteFrameAsync(Stream stream, byte version, byte[] body, CancellationToken ct)
    {
        try
        {
            if (body.Length > MaxBodyLength)
                throw new InvalidDataException("The provisioning message is too large.");

            var header = new byte[HeaderLength];
            Magic.CopyTo(header);
            header[4] = version;
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(5), (uint)body.Length);
            await stream.WriteAsync(header, ct);
            await stream.WriteAsync(body, ct);
            await stream.FlushAsync(ct);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(body);
        }
    }

    private static async Task<(byte[] Body, byte Version)?> ReadFrameAsync(Stream stream, CancellationToken ct)
    {
        var header = new byte[HeaderLength];
        var read = await stream.ReadAtLeastAsync(header, HeaderLength, false, ct);
        if (read == 0)
            return null;
        if (read < HeaderLength || !header.AsSpan(0, 4).SequenceEqual(Magic))
            throw new InvalidDataException("Not a key provisioning frame.");
        if (header[4] is not (Version1 or Version))
            throw new InvalidDataException($"Unsupported key provisioning version {header[4]}.");

        var length = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(5));
        if (length > MaxBodyLength)
            throw new InvalidDataException("The provisioning message is too large.");

        var body = new byte[length];
        if (await stream.ReadAtLeastAsync(body, body.Length, false, ct) < body.Length)
        {
            CryptographicOperations.ZeroMemory(body);
            throw new InvalidDataException("The provisioning message ended early.");
        }

        return (body, header[4]);
    }
}

/// <summary>A request to a locked node's provisioning endpoint.</summary>
public sealed class KeyProvisioningRequest
{
    /// <summary><see cref="KeyProvisioningProtocol.StatusKind"/> or <see cref="KeyProvisioningProtocol.UnlockKind"/>.</summary>
    public string Kind { get; set; } = KeyProvisioningProtocol.StatusKind;

    /// <summary>The key material's form; only <see cref="KeyProvisioningProtocol.EncryptedKeyFileMaterial"/> today.</summary>
    public string? Material { get; set; }

    /// <summary>The key file's bytes, base64.</summary>
    public string? KeyFile { get; set; }

    /// <summary>The key file password.</summary>
    public string? Password { get; set; }

    /// <summary>Optional at-rest secrets the node then needs in no file.</summary>
    public KeyProvisioningSecrets? Secrets { get; set; }

    /// <summary>
    /// Version 2: answer with the default LND gRPC macaroons baked in memory under the delivered
    /// <see cref="KeyProvisioningSecrets.LndGrpcMacaroonRootKey"/> (<see cref="KeyProvisioningResponse.LndGrpcMacaroons"/>);
    /// the node writes no macaroon file for a provisioned root key.
    /// </summary>
    public bool? ReturnLndGrpcMacaroons { get; set; }

    /// <summary>The version of the frame the request came in (set by the reader, never serialized).</summary>
    [JsonIgnore]
    public byte FrameVersion { get; set; } = KeyProvisioningProtocol.Version1;

    /// <summary>Whether the request uses a version 2 field.</summary>
    public bool RequiresVersion2() => ReturnLndGrpcMacaroons == true || Secrets?.UsesVersion2Fields() == true;
}

/// <summary>
/// At-rest secrets delivered with the key (locked start, NL-1352). Every value is optional; a given value replaces the
/// configuration key it names, in memory only.
/// </summary>
public sealed class KeyProvisioningSecrets
{
    /// <summary><c>Database:ConnectionString</c>.</summary>
    public string? DatabaseConnectionString { get; set; }

    /// <summary><c>Bitcoin:RpcUser</c>.</summary>
    public string? BitcoinRpcUser { get; set; }

    /// <summary><c>Bitcoin:RpcPassword</c>.</summary>
    public string? BitcoinRpcPassword { get; set; }

    /// <summary>Version 2: <c>Node:Tor:ControlPassword</c>.</summary>
    public string? TorControlPassword { get; set; }

    /// <summary>Version 2: the Tor onion service key, <c>ED25519-V3:&lt;base64 of the 64-byte expanded key&gt;</c> (the
    /// content of <c>Node:Tor:OnionServiceKeyFile</c>).</summary>
    public string? TorOnionServiceKey { get; set; }

    /// <summary>Version 2: the LND gRPC macaroon root key, base64 of the 32 bytes <c>macaroons.key</c> holds.</summary>
    public string? LndGrpcMacaroonRootKey { get; set; }

    /// <summary>Version 2: the LND gRPC server certificate and key (<c>tls.cert</c>/<c>tls.key</c>) and an optional
    /// client CA (<c>LndGrpc:ClientCaPath</c>).</summary>
    public KeyProvisioningTls? LndGrpcTls { get; set; }

    /// <summary>Version 2: the Cashu payment processor's <c>server.pem</c>, <c>server.key</c> and <c>ca.pem</c>.</summary>
    public KeyProvisioningTls? CashuProcessorTls { get; set; }

    /// <summary>Version 2: the LN backend's <c>server.pem</c>, <c>server.key</c> and <c>ca.pem</c>.</summary>
    public KeyProvisioningTls? LnBackendTls { get; set; }

    /// <summary>Whether a version 2 field is set.</summary>
    public bool UsesVersion2Fields() =>
        TorControlPassword is not null || TorOnionServiceKey is not null || LndGrpcMacaroonRootKey is not null
     || LndGrpcTls is not null || CashuProcessorTls is not null || LnBackendTls is not null;
}

/// <summary>A TLS server identity: each value PEM, or base64 DER (the key as PKCS#8).</summary>
public sealed class KeyProvisioningTls
{
    /// <summary>The server certificate.</summary>
    public string? Certificate { get; set; }

    /// <summary>The server certificate's private key.</summary>
    public string? Key { get; set; }

    /// <summary>The CA client certificates must chain to (mutual TLS); optional.</summary>
    public string? ClientCa { get; set; }
}

/// <summary>A locked node's answer.</summary>
public sealed class KeyProvisioningResponse
{
    public bool Ok { get; set; }

    /// <summary><see cref="KeyProvisioningProtocol.LockedState"/> or <see cref="KeyProvisioningProtocol.UnlockedState"/>.</summary>
    public string State { get; set; } = KeyProvisioningProtocol.LockedState;

    /// <summary>The network the node runs on.</summary>
    public string? Network { get; set; }

    /// <summary>The node public key (hex) once unlocked.</summary>
    public string? NodeId { get; set; }

    /// <summary>Why the request was refused; never holds key material.</summary>
    public string? Error { get; set; }

    /// <summary>Version 2: the default LND gRPC macaroons (<c>admin</c>, <c>readonly</c>, <c>invoice</c>; base64)
    /// when the request asked for them.</summary>
    public Dictionary<string, string>? LndGrpcMacaroons { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
                             DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(KeyProvisioningRequest))]
[JsonSerializable(typeof(KeyProvisioningResponse))]
[JsonSerializable(typeof(KeyProvisioningSecrets))]
internal sealed partial class KeyProvisioningJsonContext : JsonSerializerContext;