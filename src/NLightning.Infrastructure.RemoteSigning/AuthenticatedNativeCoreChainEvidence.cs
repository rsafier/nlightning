using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using NBitcoin;
using NLightning.Domain.Protocol.ValueObjects;
using NLightning.Domain.Signing;
using NLightning.Infrastructure.Bitcoin.Networks;
using NLightning.Infrastructure.Transport.Http;

namespace NLightning.Infrastructure.RemoteSigning;

public sealed record NativeCoreChainEvidenceOptions(Uri Endpoint, string ExpectedGenesisHash,
    TimeSpan RequestTimeout, TimeSpan MaximumEvidenceAge, TimeSpan MaximumTipAge,
    TimeSpan MaximumFutureTipSkew, bool AllowTrustedPlainHttp = false);

/// <summary>
/// Signer-installed Bitcoin Core evidence. TLS or an explicitly trusted transport authenticates the source;
/// that independently administered Core and the signer's clock establish synchronization and freshness.
/// </summary>
public sealed class AuthenticatedNativeCoreChainEvidence : IAuthenticatedNativeChainEvidence, IDisposable
{
    private readonly NativeSignerBinding _binding;
    private readonly NativeCoreChainEvidenceOptions _options;
    private readonly INativeSignerWalletScriptRegistry _ownership;
    private readonly HttpClient _http;
    private readonly AuthenticationHeaderValue _authorization;
    private readonly TimeProvider _clock;
    private readonly string _coreChain;
    private readonly AsyncLocal<EvidenceBatch?> _batch = new();
    private long _requestId;

    public AuthenticatedNativeCoreChainEvidence(NativeSignerBinding binding, NativeCoreChainEvidenceOptions options,
        INativeSignerWalletScriptRegistry ownership, string rpcUsername, string rpcPassword,
        HttpMessageHandler? transport = null, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Endpoint);
        ArgumentNullException.ThrowIfNull(ownership);
        new NodeSigningContext(binding.NodeId, binding.OwnerId, binding.SignerId, binding.Network,
            new NLightning.Domain.Crypto.ValueObjects.CompactPubKey(Convert.FromHexString(binding.PublicKey))).Validate();
        var network = BitcoinNetwork.Resolve(binding.Network);
        if (!options.Endpoint.IsAbsoluteUri || options.Endpoint.UserInfo.Length != 0
         || options.Endpoint.Fragment.Length != 0 || options.Endpoint.Query.Length != 0
         || options.Endpoint.Scheme is not ("http" or "https")
         || options.Endpoint.Scheme == "http" && !IsLoopback(options.Endpoint) && !options.AllowTrustedPlainHttp)
            throw new ArgumentException("Chain evidence requires HTTPS, loopback HTTP or an explicitly trusted transport.");
        if (options.RequestTimeout <= TimeSpan.Zero || options.RequestTimeout > TimeSpan.FromMinutes(1)
         || options.MaximumEvidenceAge <= TimeSpan.Zero || options.MaximumTipAge <= TimeSpan.Zero
         || options.MaximumFutureTipSkew < TimeSpan.Zero)
            throw new ArgumentException("Chain evidence requires bounded request and freshness settings.");
        var expectedGenesis = ParseHash(options.ExpectedGenesisHash);
        if (expectedGenesis != NBitcoinNetworkResolver.Resolve(binding.Network).GetGenesis().GetHash().ToString())
            throw new ArgumentException("Chain evidence genesis does not match the installed Bitcoin network.");
        if (string.IsNullOrWhiteSpace(rpcUsername) || rpcUsername.Contains(':')
         || rpcUsername.Any(char.IsControl) || string.IsNullOrEmpty(rpcPassword) || rpcPassword.Any(char.IsControl))
            throw new ArgumentException("Authenticated Core credentials are required.");
        _binding = binding;
        _options = options with { ExpectedGenesisHash = expectedGenesis };
        _ownership = ownership;
        _clock = clock ?? TimeProvider.System;
        _coreChain = network.IsSignet ? "signet" : network.Name switch
        {
            "mainnet" => "main",
            "testnet" => "test",
            "testnet4" => "testnet4",
            "regtest" => "regtest",
            _ => throw new NotSupportedException("Core evidence network is unsupported.")
        };
        _authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes(rpcUsername + ":" + rpcPassword)));
        _http = new HttpClient(transport ?? new HttpClientHandler { AllowAutoRedirect = false }, disposeHandler: true)
        { Timeout = Timeout.InfiniteTimeSpan };
    }

    public void RequireFresh(NativeSignerBinding binding)
    {
        RequireBinding(binding);
        _batch.Value = null;
        var started = _clock.GetTimestamp();
        try
        {
            var (hash, height) = ReadChain();
            if (ParseHash(Rpc("getblockhash", [0]).GetString()) != _options.ExpectedGenesisHash)
                throw new InvalidOperationException("Core evidence genesis differs from installed network.");
            var batch = new EvidenceBatch(hash, height, started);
            RequireBatchCurrent(batch);
            _batch.Value = batch;
        }
        catch { _batch.Value = null; throw; }
    }

    public NativeWalletInputEvidence GetOutput(NativeSignerBinding binding, string transactionId, uint outputIndex)
    {
        RequireBinding(binding);
        var batch = RequireBatch();
        try
        {
            var txid = ParseHash(transactionId);
            var output = Rpc("gettxout", [txid, outputIndex, true]);
            if (output.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException("Core output is spent or unavailable.");
            if (ParseHash(output.GetProperty("bestblock").GetString()) != batch.Hash)
                throw new InvalidOperationException("Core output belongs to a changed chain tip.");
            var confirmations = output.GetProperty("confirmations").GetInt64();
            if (confirmations < 0 || confirmations > batch.Height + 1
             || output.GetProperty("coinbase").GetBoolean() && confirmations < 100)
                throw new InvalidOperationException("Core output confirmations or coinbase maturity are invalid.");
            var satoshis = output.GetProperty("value").GetDecimal() * 100_000_000m;
            if (satoshis <= 0 || satoshis > 2_100_000_000_000_000m || decimal.Truncate(satoshis) != satoshis)
                throw new InvalidDataException("Core output amount is invalid.");
            var script = Convert.FromHexString(output.GetProperty("scriptPubKey").GetProperty("hex").GetString()
                ?? throw new InvalidDataException("Core output script is missing."));
            if (script.Length is 0 or > 10_000)
                throw new InvalidDataException("Core output script is invalid.");
            RequireBatchCurrent(batch);
            // Joint channel scripts may be unregistered here; their validator proves the enrolled funding script.
            var owned = _ownership.IsOwned(binding, script.ToArray());
            return new NativeWalletInputEvidence(txid, outputIndex, (long)satoshis, script,
                owned ? binding.OwnerId : "", owned ? binding.NodeId : "", true);
        }
        catch { _batch.Value = null; throw; }
    }

    public void RequireUnchanged(NativeSignerBinding binding)
    {
        RequireBinding(binding);
        try { RequireBatchCurrent(RequireBatch()); }
        catch { _batch.Value = null; throw; }
    }

    private EvidenceBatch RequireBatch()
    {
        var batch = _batch.Value ?? throw new InvalidOperationException("Fresh Core evidence batch is required.");
        if (_clock.GetElapsedTime(batch.Started) > _options.MaximumEvidenceAge)
            throw new InvalidOperationException("Core evidence batch expired.");
        return batch;
    }

    private void RequireBatchCurrent(EvidenceBatch batch)
    {
        if (_clock.GetElapsedTime(batch.Started) > _options.MaximumEvidenceAge)
            throw new InvalidOperationException("Core evidence batch expired.");
        var (hash, height) = ReadChain();
        if (hash != batch.Hash || height != batch.Height
         || _clock.GetElapsedTime(batch.Started) > _options.MaximumEvidenceAge)
            throw new InvalidOperationException("Core evidence batch expired or its chain tip changed.");
    }

    private (string Hash, long Height) ReadChain()
    {
        var result = Rpc("getblockchaininfo", []);
        var height = result.GetProperty("blocks").GetInt64();
        var headers = result.GetProperty("headers").GetInt64();
        var progress = result.GetProperty("verificationprogress").GetDecimal();
        var tipTime = DateTimeOffset.FromUnixTimeSeconds(result.GetProperty("time").GetInt64());
        var now = _clock.GetUtcNow();
        if (result.GetProperty("chain").GetString() != _coreChain
         || result.GetProperty("initialblockdownload").GetBoolean()
         || height < 0 || height > uint.MaxValue || headers != height || progress < 0.999999m || progress > 1m
         || now - tipTime > _options.MaximumTipAge || tipTime - now > _options.MaximumFutureTipSkew)
            throw new InvalidOperationException("Core evidence network, synchronization or tip freshness is invalid.");
        return (ParseHash(result.GetProperty("bestblockhash").GetString()), height);
    }

    private JsonElement Rpc(string method, object[] parameters)
        => RpcAsync(method, parameters).GetAwaiter().GetResult();

    private async Task<JsonElement> RpcAsync(string method, object[] parameters)
    {
        var id = Interlocked.Increment(ref _requestId);
        using var cancellation = new CancellationTokenSource(_options.RequestTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Post, _options.Endpoint);
        request.Headers.Authorization = _authorization;
        request.Content = new StringContent(JsonSerializer.Serialize(new
        { jsonrpc = "1.0", id, method, @params = parameters }), Encoding.UTF8, "application/json");
        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                cancellation.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException("Authenticated Core evidence request failed.");
            var bytes = await HttpResponseLimits.ReadBoundedAsync(response.Content,
                HttpResponseLimits.SmallResponseMaxBytes, cancellation.Token).ConfigureAwait(false);
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            if (root.GetProperty("id").GetInt64() != id
             || root.GetProperty("error").ValueKind != JsonValueKind.Null)
                throw new InvalidDataException("Core evidence response identity or outcome is invalid.");
            return root.GetProperty("result").Clone();
        }
        catch (OperationCanceledException)
        { throw new InvalidOperationException("Core evidence request timed out."); }
        catch (HttpRequestException)
        { throw new InvalidOperationException("Authenticated Core evidence transport failed."); }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or FormatException)
        { throw new InvalidDataException("Core evidence response is malformed."); }
    }

    private void RequireBinding(NativeSignerBinding binding)
    {
        if (binding != _binding) throw new UnauthorizedAccessException("Core evidence belongs to another signer enrollment.");
    }

    private static string ParseHash(string? value)
    {
        if (value is not { Length: 64 } || !value.All(Uri.IsHexDigit))
            throw new InvalidDataException("Core evidence hash is invalid.");
        return uint256.Parse(value).ToString();
    }

    private static bool IsLoopback(Uri endpoint) => endpoint.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || IPAddress.TryParse(endpoint.DnsSafeHost, out var address) && IPAddress.IsLoopback(address);

    public void Dispose() => _http.Dispose();
    private sealed record EvidenceBatch(string Hash, long Height, long Started);
}