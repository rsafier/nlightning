namespace NLightning.Infrastructure.Bitcoin.Gossip;

using Domain.Node.Options;

/// <summary>Where the funding output lookup reads the txid at a short channel id's position.</summary>
public enum FundingTxIdSourceKind
{
    /// <summary>bitcoind's own block (<c>getblock &lt;hash&gt; 1</c>); needs the block's data, so an unpruned node.</summary>
    Bitcoind = 0,

    /// <summary>
    /// An Esplora HTTP API (mempool.space, blockstream.info or a self-hosted esplora/electrs): the txid at the position
    /// and its merkle proof, checked against our own node's block header; the output itself still comes from our
    /// node's <c>gettxout</c>. Works with a pruned node.
    /// </summary>
    Esplora = 1
}

/// <summary>
/// The funding txid source (BOLT 7 plan D12, pruned nodes). The property names are the <c>Gossip:*</c> keys; the host
/// must bind this class from the <c>Gossip</c> section like <see cref="FundingOutputLookupOptions"/>
/// (<c>services.Configure&lt;FundingTxIdSourceOptions&gt;(configuration.GetSection("Gossip"))</c>), or the defaults
/// (bitcoind) apply whatever the configuration says.
/// </summary>
public sealed class FundingTxIdSourceOptions
{
    /// <summary><c>Gossip:FundingTxIdSource</c>: <c>Bitcoind</c> (default) or <c>Esplora</c>.</summary>
    public FundingTxIdSourceKind FundingTxIdSource { get; set; } = FundingTxIdSourceKind.Bitcoind;

    /// <summary>
    /// <c>Gossip:EsploraUrl</c>: the API base, e.g. <c>https://mempool.space/api</c>,
    /// <c>https://mempool.space/signet/api</c> or a self-hosted <c>http://127.0.0.1:3002</c> (plain <c>http://</c> only
    /// to a loopback or <c>.onion</c> host unless <see cref="EsploraAllowPlainHttp"/>, NL-678). Required for Esplora.
    /// It must serve the same network as our bitcoind (a block of another chain never matches our header).
    /// </summary>
    public string? EsploraUrl { get; set; }

    /// <summary>
    /// <c>Gossip:EsploraAllowPlainHttp</c>: allow a plain <c>http://</c> <see cref="EsploraUrl"/> to any host (an index
    /// you trust on your own network). Default false: plain HTTP only to a loopback or <c>.onion</c> host (NL-678; the
    /// merkle proof is checked against our own header either way, but the index's answers would be open to anyone on
    /// the path).
    /// </summary>
    public bool EsploraAllowPlainHttp { get; set; }

    /// <summary>
    /// <c>Gossip:EsploraRequestsPerSecond</c>: HTTP requests started per second (burst the same), default 2, polite for
    /// public servers. An uncached block costs one request (its whole txid list, verified against our header's merkle
    /// root, NL-423) and answers every channel of that block afterwards, so an initial mainnet sync of some
    /// 45,000-50,000 channel announcements takes hours at the default rate against a public server; the per-position
    /// fallback (an index that serves no txid lists, or lists our header does not prove) costs two requests per
    /// uncached channel again. Use a self-hosted esplora/electrs with a higher rate for a faster sync.
    /// </summary>
    public int EsploraRequestsPerSecond { get; set; } = 2;

    /// <summary>
    /// <c>Gossip:EsploraMaxRetries</c>: retries of a request answered 429 (Too Many Requests) before the lookup gives
    /// up as transient, default 3.
    /// </summary>
    public int EsploraMaxRetries { get; set; } = 3;

    /// <summary>
    /// <c>Gossip:EsploraInitialBackoff</c>: the pause after a 429 without <c>Retry-After</c>, doubled for every 429 in a
    /// row, default 2 s.
    /// </summary>
    public TimeSpan EsploraInitialBackoff { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary><c>Gossip:EsploraMaxBackoff</c>: the longest pause, also for a longer <c>Retry-After</c>, default 5 min.</summary>
    public TimeSpan EsploraMaxBackoff { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// <c>Gossip:EsploraMaxInlineWait</c>: the longest 429 pause a request waits out inside the lookup, default 5 s; a
    /// longer pause fails the requests that meet it at once as transient (<c>ChainUnavailable</c>), so the lookup's
    /// concurrency slot is not held while the index is rate limiting us.
    /// </summary>
    public TimeSpan EsploraMaxInlineWait { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary><c>Gossip:EsploraTimeout</c>: the timeout of one HTTP request, default 30 s.</summary>
    public TimeSpan EsploraTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// <c>Gossip:EsploraCacheEntries</c>: proven (block, index) → txid answers kept, least recently used evicted first,
    /// default 8,192.
    /// </summary>
    public int EsploraCacheEntries { get; set; } = 8_192;

    /// <summary>The Esplora base URL as an absolute http(s) URI ending in '/', null when not set or invalid.</summary>
    public Uri? GetEsploraBaseUri()
    {
        if (string.IsNullOrWhiteSpace(EsploraUrl)
         || !Uri.TryCreate(EsploraUrl.Trim().TrimEnd('/') + "/", UriKind.Absolute, out var uri)
         || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return null;

        return uri;
    }

    /// <summary>The invalid settings, empty when valid.</summary>
    public IReadOnlyList<string> GetValidationErrors()
    {
        var errors = new List<string>();
        if (!Enum.IsDefined(FundingTxIdSource))
            errors.Add($"{nameof(FundingTxIdSource)} must be Bitcoind or Esplora");
        if (FundingTxIdSource != FundingTxIdSourceKind.Esplora)
            return errors;

        if (GetEsploraBaseUri() is not { } baseUri)
            errors.Add($"{nameof(EsploraUrl)} must be an absolute http(s) URL when {nameof(FundingTxIdSource)} is Esplora");
        else if (HttpUrlPolicy.GetError(baseUri, EsploraAllowPlainHttp, $"Gossip:{nameof(EsploraUrl)}",
                                        $"Gossip:{nameof(EsploraAllowPlainHttp)}") is { } urlError)
            errors.Add(urlError);
        if (EsploraRequestsPerSecond < 1)
            errors.Add($"{nameof(EsploraRequestsPerSecond)} must be at least 1");
        if (EsploraMaxRetries < 0)
            errors.Add($"{nameof(EsploraMaxRetries)} must not be negative");
        if (EsploraInitialBackoff <= TimeSpan.Zero)
            errors.Add($"{nameof(EsploraInitialBackoff)} must be positive");
        if (EsploraMaxBackoff < EsploraInitialBackoff)
            errors.Add($"{nameof(EsploraMaxBackoff)} must be at least {nameof(EsploraInitialBackoff)}");
        if (EsploraMaxInlineWait < TimeSpan.Zero)
            errors.Add($"{nameof(EsploraMaxInlineWait)} must not be negative");
        if (EsploraTimeout <= TimeSpan.Zero)
            errors.Add($"{nameof(EsploraTimeout)} must be positive");
        if (EsploraCacheEntries < 1)
            errors.Add($"{nameof(EsploraCacheEntries)} must be at least 1");
        return errors;
    }
}