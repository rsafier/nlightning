namespace NLightning.Infrastructure.Bitcoin.Gossip;

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
/// The funding txid source (BOLT 7 plan D12, pruned nodes). The property names are the <c>Gossip:*</c> keys, so the
/// host binds this class from the <c>Gossip</c> section like <see cref="FundingOutputLookupOptions"/>.
/// </summary>
public sealed class FundingTxIdSourceOptions
{
    /// <summary><c>Gossip:FundingTxIdSource</c>: <c>Bitcoind</c> (default) or <c>Esplora</c>.</summary>
    public FundingTxIdSourceKind FundingTxIdSource { get; set; } = FundingTxIdSourceKind.Bitcoind;

    /// <summary>
    /// <c>Gossip:EsploraUrl</c>: the API base, e.g. <c>https://mempool.space/api</c>,
    /// <c>https://mempool.space/signet/api</c> or a self-hosted <c>http://127.0.0.1:3002</c>. Required for Esplora. It
    /// must serve the same network as our bitcoind (a block of another chain never matches our header).
    /// </summary>
    public string? EsploraUrl { get; set; }

    /// <summary>
    /// <c>Gossip:EsploraRequestsPerSecond</c>: HTTP requests started per second (burst the same), default 2, polite for
    /// public servers; raise it for a self-hosted index. Each uncached lookup costs two requests.
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

        if (GetEsploraBaseUri() is null)
            errors.Add($"{nameof(EsploraUrl)} must be an absolute http(s) URL when {nameof(FundingTxIdSource)} is Esplora");
        if (EsploraRequestsPerSecond < 1)
            errors.Add($"{nameof(EsploraRequestsPerSecond)} must be at least 1");
        if (EsploraMaxRetries < 0)
            errors.Add($"{nameof(EsploraMaxRetries)} must not be negative");
        if (EsploraInitialBackoff <= TimeSpan.Zero)
            errors.Add($"{nameof(EsploraInitialBackoff)} must be positive");
        if (EsploraMaxBackoff < EsploraInitialBackoff)
            errors.Add($"{nameof(EsploraMaxBackoff)} must be at least {nameof(EsploraInitialBackoff)}");
        if (EsploraTimeout <= TimeSpan.Zero)
            errors.Add($"{nameof(EsploraTimeout)} must be positive");
        if (EsploraCacheEntries < 1)
            errors.Add($"{nameof(EsploraCacheEntries)} must be at least 1");
        return errors;
    }
}