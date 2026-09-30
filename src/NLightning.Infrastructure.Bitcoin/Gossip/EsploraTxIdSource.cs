using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NBitcoin;
using NBitcoin.Crypto;

namespace NLightning.Infrastructure.Bitcoin.Gossip;

using Wallet.Interfaces;

/// <summary>
/// The funding txid at a short channel id's position from an Esplora HTTP API (BOLT 7 plan D12, for nodes without an
/// unpruned bitcoind). The index is trusted for nothing: the block hash comes from our node (<c>getblockhash</c>), the
/// index's txid at <c>GET /block/{hash}/txid/{index}</c> must come with a merkle proof
/// (<c>GET /tx/{txid}/merkle-proof</c>) that reaches the merkle root of our node's header (<c>getblockheader</c>, kept
/// by a pruned node) at that very position, and <see cref="FundingOutputLookup"/> then reads the output's script,
/// amount, height and spentness from our node's <c>gettxout</c>.
/// </summary>
/// <remarks>
/// <para>
/// Every request waits for the rate limit (<see cref="FundingTxIdSourceOptions.EsploraRequestsPerSecond"/>, default 2
/// per second, polite for public servers) and for any pause a 429 started: <c>Retry-After</c> when given, else
/// <see cref="FundingTxIdSourceOptions.EsploraInitialBackoff"/> doubled per 429 in a row, never over
/// <see cref="FundingTxIdSourceOptions.EsploraMaxBackoff"/>; the pause holds every request, not just the one refused.
/// A pause up to <see cref="FundingTxIdSourceOptions.EsploraMaxInlineWait"/> (5 s) is waited out inside the lookup; a
/// longer one fails the requests that meet it as transient at once (the ingress defers and retries the announcement),
/// so a rate-limiting index never holds the lookup's concurrency slots. After
/// <see cref="FundingTxIdSourceOptions.EsploraMaxRetries"/> retries the lookup gives up as transient.
/// </para>
/// <para>
/// Out of range is answered only from our node's transaction count (<c>nTx</c>), never from the index. Anything the
/// index gets wrong or cannot answer (a 404 for a block it has not indexed yet, a 5xx, a proof that misses our header)
/// throws <see cref="EsploraUnavailableException"/>, which the lookup reports as <c>ChainUnavailable</c>: transient and
/// never held against the peer that sent the announcement. Proven answers are cached by (block hash, index), so a
/// reorg never serves one for another block.
/// </para>
/// <para>
/// A wrong-network index would otherwise show up only as a graph that never fills (NL-424): the first failed lookup
/// therefore reads which chain the index serves (<c>GET block-height/0</c> against our node's genesis hash) — an
/// error, and a refusal of the source, when it serves another one; a warning when it cannot be checked. Once refused,
/// lookups fail at once without asking the index.
/// </para>
/// </remarks>
public sealed class EsploraTxIdSource : IFundingTxIdSource, IDisposable
{
    private readonly IBitcoinChainService _chain;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly ILogger<EsploraTxIdSource> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly TokenBucketRateLimiter _rateLimiter;
    private readonly Uri _baseUri;
    private readonly int _maxRetries;
    private readonly TimeSpan _initialBackoff;
    private readonly TimeSpan _maxBackoff;
    private readonly TimeSpan _maxInlineWait;
    private readonly int _cacheCapacity;

    private readonly Lock _gate = new();
    private readonly Dictionary<(uint256 BlockHash, uint Index), LinkedListNode<CachedTxId>> _cache = new();
    private readonly LinkedList<CachedTxId> _lru = new();
    private DateTimeOffset _pausedUntil = DateTimeOffset.MinValue;
    private int _rateLimitedInARow;
    private long _rateLimitedTotal;
    private int _chainChecked;
    private int _refused;

    /// <param name="chain">Our own node: block hashes and headers.</param>
    /// <param name="httpClient">The client for the index; its timeout is set from the options when owned.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="options">The <c>Gossip</c> source options; <c>EsploraUrl</c> is required.</param>
    /// <param name="timeProvider">The clock of the rate limit and the 429 pauses.</param>
    /// <param name="ownsHttpClient">True to dispose <paramref name="httpClient"/> with this source.</param>
    public EsploraTxIdSource(IBitcoinChainService chain, HttpClient httpClient, ILogger<EsploraTxIdSource> logger,
                             IOptions<FundingTxIdSourceOptions> options, TimeProvider? timeProvider = null,
                             bool ownsHttpClient = false)
    {
        ArgumentNullException.ThrowIfNull(chain);
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(options);

        var settings = options.Value;
        var errors = new List<string>(settings.GetValidationErrors());
        var baseUri = settings.GetEsploraBaseUri();
        if (baseUri is null && errors.Count == 0)
            errors.Add($"{nameof(FundingTxIdSourceOptions.EsploraUrl)} must be an absolute http(s) URL");
        if (errors.Count > 0)
            throw new ArgumentException($"Invalid Esplora funding txid source options: {string.Join("; ", errors)}",
                                        nameof(options));

        _chain = chain;
        _httpClient = httpClient;
        _ownsHttpClient = ownsHttpClient;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _rateLimiter = new TokenBucketRateLimiter(settings.EsploraRequestsPerSecond, _timeProvider);
        _baseUri = baseUri!;
        _maxRetries = settings.EsploraMaxRetries;
        _initialBackoff = settings.EsploraInitialBackoff;
        _maxBackoff = settings.EsploraMaxBackoff;
        _maxInlineWait = settings.EsploraMaxInlineWait;
        _cacheCapacity = settings.EsploraCacheEntries;

        if (_logger.IsEnabled(LogLevel.Information))
            _logger.LogInformation(
                "Gossip funding txids come from the Esplora index at {EsploraUrl} ({Rate} requests/s), each proven "
              + "against our bitcoind's block header; outputs are still read from our bitcoind", _baseUri,
                settings.EsploraRequestsPerSecond);
    }

    /// <summary>Requests answered 429 so far (tests, diagnostics).</summary>
    public long RateLimitedResponses => Interlocked.Read(ref _rateLimitedTotal);

    /// <summary>True once the index was found serving another chain; every lookup fails at once (NL-424).</summary>
    public bool Refused => Volatile.Read(ref _refused) == 1;

    /// <inheritdoc />
    public async Task<FundingTxIdAtPosition> GetTxIdAsync(uint height, uint index,
                                                          CancellationToken cancellationToken = default)
    {
        if (Refused)
            throw new EsploraUnavailableException(
                $"the Esplora index at {_baseUri} serves another chain; fix Gossip:EsploraUrl");

        try
        {
            return await GetTxIdCoreAsync(height, index, cancellationToken);
        }
        catch (EsploraUnavailableException)
        {
            // The index may simply be on another network (NL-424): surface it once instead of at Debug per lookup.
            // Awaited (a failed lookup is already the slow path), but never behind a pause a 429 of the real lookups
            // started, so the check cannot hold this lookup for the index's sake
            await CheckIndexChainOnceAsync();
            throw;
        }
    }

    private async Task<FundingTxIdAtPosition> GetTxIdCoreAsync(uint height, uint index,
                                                               CancellationToken cancellationToken)
    {
        var blockHash = await _chain.GetBlockHashAsync(height);
        if (TryGetCached(blockHash, index, out var cachedTxId))
            return FundingTxIdAtPosition.Found(blockHash, cachedTxId);

        var (merkleRoot, txCount) = await _chain.GetBlockHeaderSummaryAsync(blockHash)
                  ?? throw new EsploraUnavailableException($"our node has no header for block {blockHash} at {height}");
        // Without our node's nTx (a header whose block it never downloaded, e.g. below an assumeutxo snapshot) neither
        // the range nor the proof's depth can be checked, and a shorter branch could pass an inner merkle node off as a
        // txid: unprovable, so transient
        if (txCount <= 0)
            throw new EsploraUnavailableException(
                $"our node does not know the transaction count of block {blockHash} at {height}; cannot prove a txid");
        if (index >= txCount)
            return FundingTxIdAtPosition.IndexOutOfRange;

        var txIdText = await GetStringAsync($"block/{blockHash}/txid/{index}", cancellationToken)
                    ?? throw new EsploraUnavailableException(
                           $"the index has no transaction {index} in block {blockHash} ({height}); not indexed yet?");
        if (!uint256.TryParse(txIdText.Trim(), out var txId))
            throw new EsploraUnavailableException($"the index answered no txid for {height}:{index}");

        var proofJson = await GetStringAsync($"tx/{txId}/merkle-proof", cancellationToken)
                     ?? throw new EsploraUnavailableException($"the index has no merkle proof for {txId}");
        if (!TryParseMerkleProof(proofJson, out var branch, out var position)
         || position != index
         || !VerifyMerkleProof(txId, branch, position, merkleRoot, txCount))
        {
            if (_logger.IsEnabled(LogLevel.Warning))
                _logger.LogWarning(
                    "The Esplora index at {EsploraUrl} named {TxId} at {Height}:{Index}, but its merkle proof does not "
                  + "reach our bitcoind's block {BlockHash} at that position; not used (index on another chain or "
                  + "misbehaving)", _baseUri, txId, height, index, blockHash);
            throw new EsploraUnavailableException(
                $"the index's txid {txId} for {height}:{index} is not proven by block {blockHash}'s merkle root");
        }

        Store(blockHash, index, txId);
        return FundingTxIdAtPosition.Found(blockHash, txId);
    }

    /// <summary>
    /// Reads, once, which chain the index serves (NL-424): its block at height 0 must be our node's genesis hash. A
    /// mismatch is logged at Error and refuses the source (every later lookup fails at once); an index that cannot be
    /// asked (down, not indexed) only gets a warning, and the lookups stay transient. Runs off the caller's
    /// cancellation, so a cancelled lookup still leaves the check done.
    /// </summary>
    private async Task CheckIndexChainOnceAsync()
    {
        if (Interlocked.Exchange(ref _chainChecked, 1) != 0)
            return;

        try
        {
            var answered = await GetStringAsync("block-height/0", CancellationToken.None, skipInitialPause: true);
            if (answered?.Trim() is not { } body || !uint256.TryParse(body, out var indexGenesis))
            {
                if (_logger.IsEnabled(LogLevel.Warning))
                    _logger.LogWarning("Could not read which chain the Esplora index at {EsploraUrl} serves (no block "
                                     + "at height 0); its failing lookups stay transient", _baseUri);
                return;
            }

            var ourGenesis = await _chain.GetBlockHashAsync(0);
            if (indexGenesis == ourGenesis)
            {
                if (_logger.IsEnabled(LogLevel.Information))
                    _logger.LogInformation("The Esplora index at {EsploraUrl} serves our chain; its failing lookups "
                                         + "stay transient", _baseUri);
                return;
            }

            Volatile.Write(ref _refused, 1);
            _logger.LogError(
                "The Esplora index at {EsploraUrl} serves another chain: its block at height 0 is {IndexGenesis}, "
              + "ours is {OurGenesis}, so its answers would be wrong and it is refused from now on (every funding "
              + "lookup reports the index unavailable). Check Gossip:EsploraUrl against our network",
                _baseUri, indexGenesis, ourGenesis);
        }
        catch (Exception e)
        {
            if (_logger.IsEnabled(LogLevel.Warning))
                _logger.LogWarning(e, "Could not check which chain the Esplora index at {EsploraUrl} serves; its "
                                 + "failing lookups stay transient", _baseUri);
        }
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
            _httpClient.Dispose();
    }

    /// <summary>
    /// True when <paramref name="branch"/> takes <paramref name="txId"/> at <paramref name="position"/> to
    /// <paramref name="merkleRoot"/> (Bitcoin's merkle tree: double SHA-256 of the two children's internal bytes, an
    /// odd level's last node paired with itself). The position must be below <paramref name="txCount"/> and the branch
    /// exactly the tree's depth, so a duplicated (CVE-2012-2459) or inner-node position never passes; an unknown count
    /// (zero or less) proves nothing and is refused. A right child equal to its left sibling (only the duplicated
    /// padding looks like that) is refused too.
    /// </summary>
    internal static bool VerifyMerkleProof(uint256 txId, IReadOnlyList<uint256> branch, uint position,
                                           uint256 merkleRoot, int txCount)
    {
        if (branch.Count > 32 || txCount <= 0 || position >= (uint)txCount)
            return false;

        var depth = 0;
        while ((1L << depth) < txCount)
            depth++;
        if (branch.Count != depth)
            return false;

        var buffer = new byte[64];
        var hash = txId;
        for (var level = 0; level < branch.Count; level++)
        {
            var sibling = branch[level];
            var isRight = ((position >> level) & 1) == 1;
            if (isRight && sibling == hash)
                return false;

            (isRight ? sibling : hash).ToBytes().CopyTo(buffer, 0);
            (isRight ? hash : sibling).ToBytes().CopyTo(buffer, 32);
            hash = Hashes.DoubleSHA256(buffer);
        }

        return hash == merkleRoot;
    }

    /// <summary>
    /// Reads Esplora's <c>{"block_height":h,"merkle":["hex",...],"pos":p}</c>; the hashes are in display (RPC) byte
    /// order like txids.
    /// </summary>
    internal static bool TryParseMerkleProof(string json, out IReadOnlyList<uint256> branch, out uint position)
    {
        branch = [];
        position = 0;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
             || !root.TryGetProperty("merkle", out var merkle) || merkle.ValueKind != JsonValueKind.Array
             || !root.TryGetProperty("pos", out var pos) || !pos.TryGetUInt32(out position))
                return false;

            var hashes = new List<uint256>(merkle.GetArrayLength());
            foreach (var element in merkle.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.String || !uint256.TryParse(element.GetString(), out var hash))
                    return false;
                hashes.Add(hash);
            }

            branch = hashes;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// GET <paramref name="path"/> under the base URL: the body of a 2xx, null for a 404, a retry after the pause for a
    /// 429, and <see cref="EsploraUnavailableException"/> for anything else. With
    /// <paramref name="skipInitialPause"/> (the chain check, NL-424), a pause the real lookups' 429s started is not
    /// waited out before the first attempt — the check never holds a lookup for the index's sake; a 429 of its own
    /// still starts (and waits out) the usual pause.
    /// </summary>
    private async Task<string?> GetStringAsync(string path, CancellationToken cancellationToken,
                                               bool skipInitialPause = false)
    {
        var uri = new Uri(_baseUri, path);
        for (var attempt = 0; ; attempt++)
        {
            if (attempt > 0 || !skipInitialPause)
                await WaitForPauseAsync(uri, cancellationToken);
            await _rateLimiter.WaitAsync(cancellationToken);

            HttpResponseMessage response;
            try
            {
                response = await _httpClient.GetAsync(uri, HttpCompletionOption.ResponseContentRead, cancellationToken);
            }
            catch (Exception ex) when (ex is HttpRequestException
                                    || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
            {
                throw new EsploraUnavailableException($"GET {uri} failed: {ex.Message}", ex);
            }

            using (response)
            {
                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    var pause = StartPause(response);
                    if (attempt >= _maxRetries)
                        throw new EsploraUnavailableException(
                            $"GET {uri} was rate limited {attempt + 1} times in a row (429)");

                    if (_logger.IsEnabled(LogLevel.Debug))
                        _logger.LogDebug("GET {Uri} rate limited (429), retrying after {Pause}", uri, pause);
                    continue;
                }

                if (response.StatusCode == HttpStatusCode.NotFound)
                    return null;

                if (!response.IsSuccessStatusCode)
                    throw new EsploraUnavailableException(
                        $"GET {uri} answered {(int)response.StatusCode} {response.ReasonPhrase}");

                lock (_gate)
                    _rateLimitedInARow = 0;
                return await response.Content.ReadAsStringAsync(cancellationToken);
            }
        }
    }

    /// <summary>Starts (or extends) the pause every request waits for after a 429 and returns its length.</summary>
    private TimeSpan StartPause(HttpResponseMessage response)
    {
        var now = _timeProvider.GetUtcNow();
        var retryAfter = response.Headers.RetryAfter;
        var requested = retryAfter?.Delta ?? (retryAfter?.Date is { } date ? date - now : null);

        TimeSpan pause;
        long total;
        lock (_gate)
        {
            _rateLimitedInARow++;
            if (requested is { } fromServer && fromServer > TimeSpan.Zero)
            {
                pause = fromServer;
            }
            else
            {
                var factor = Math.Pow(2, Math.Min(_rateLimitedInARow - 1, 30));
                pause = TimeSpan.FromTicks((long)Math.Min(_initialBackoff.Ticks * factor, _maxBackoff.Ticks));
            }

            if (pause > _maxBackoff)
                pause = _maxBackoff;

            var until = now + pause;
            if (until > _pausedUntil)
                _pausedUntil = until;
            total = Interlocked.Increment(ref _rateLimitedTotal);
        }

        // The first 429 and then every 10th at Warning, so a slow public server is visible without flooding the log
        var level = total == 1 || total % 10 == 0 ? LogLevel.Warning : LogLevel.Debug;
        if (_logger.IsEnabled(level))
            _logger.Log(level,
                        "The Esplora index at {EsploraUrl} asked us to slow down (HTTP 429, {Count} so far); pausing "
                      + "its requests for {Pause}. Lower Gossip:EsploraRequestsPerSecond or use a self-hosted index",
                        _baseUri, total, pause.ToString("c", CultureInfo.InvariantCulture));
        return pause;
    }

    /// <summary>
    /// Waits out a short 429 pause; a longer one (over <see cref="FundingTxIdSourceOptions.EsploraMaxInlineWait"/>)
    /// fails the request as transient at once, so the lookup gives its concurrency slot back and the announcement is
    /// deferred instead of every chain lookup stalling behind the index.
    /// </summary>
    private Task WaitForPauseAsync(Uri uri, CancellationToken cancellationToken)
    {
        DateTimeOffset until;
        lock (_gate)
            until = _pausedUntil;

        var wait = until - _timeProvider.GetUtcNow();
        if (wait <= TimeSpan.Zero)
            return Task.CompletedTask;
        if (wait > _maxInlineWait)
            throw new EsploraUnavailableException(
                $"GET {uri} not sent: the index asked us to pause for another "
              + $"{wait.ToString("c", CultureInfo.InvariantCulture)} (429)");

        return Task.Delay(wait, _timeProvider, cancellationToken);
    }

    private bool TryGetCached(uint256 blockHash, uint index, out uint256 txId)
    {
        lock (_gate)
        {
            if (_cache.TryGetValue((blockHash, index), out var node))
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
                txId = node.Value.TxId;
                return true;
            }
        }

        txId = uint256.Zero;
        return false;
    }

    private void Store(uint256 blockHash, uint index, uint256 txId)
    {
        lock (_gate)
        {
            var key = (blockHash, index);
            if (_cache.Remove(key, out var existing))
                _lru.Remove(existing);

            _cache[key] = _lru.AddFirst(new CachedTxId(blockHash, index, txId));
            while (_cache.Count > _cacheCapacity)
            {
                var oldest = _lru.Last!;
                _lru.RemoveLast();
                _cache.Remove((oldest.Value.BlockHash, oldest.Value.Index));
            }
        }
    }

    private sealed record CachedTxId(uint256 BlockHash, uint Index, uint256 TxId);
}