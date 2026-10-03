using System.Text.Json.Serialization;

namespace NLightning.Infrastructure.Bitcoin.Services;

/// <summary>
/// The fee service's last good estimate as it is saved in <c>FeeEstimation:CacheFile</c> (NL-706): the node-wide rate,
/// the HTTP buckets of the same answer, when it was fetched and from which source.
/// </summary>
internal sealed class FeeRateCacheEntry
{
    /// <summary>The only format written and read.</summary>
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    /// <summary>The <c>FeeEstimation:Source</c> the estimate came from (<c>Http</c> or <c>Bitcoind</c>).</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>
    /// A SHA-256 of the source settings that shape the value (URL, method, body, preferred rate and unit; or the
    /// confirmation target and mode), so an estimate from other settings is not taken for this one.
    /// </summary>
    public string SourceKey { get; set; } = string.Empty;

    /// <summary>When the estimate was fetched.</summary>
    public DateTimeOffset FetchedAt { get; set; }

    /// <summary>The node-wide rate in sat/kw.</summary>
    public long FeeRatePerKw { get; set; }

    /// <summary>The mempool.space buckets of the same answer in sat/kw (<c>Http</c> only).</summary>
    public Dictionary<string, long>? Buckets { get; set; }
}

/// <summary>
/// The source-generated JSON contract of <see cref="FeeRateCacheEntry"/> (reflection-free, for NativeAOT).
/// </summary>
[JsonSerializable(typeof(FeeRateCacheEntry))]
internal partial class FeeRateCacheJsonContext : JsonSerializerContext;