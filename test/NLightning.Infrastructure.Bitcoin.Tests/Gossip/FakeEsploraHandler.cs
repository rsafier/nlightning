using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
using NBitcoin;
using NBitcoin.Crypto;
using NLightning.Tests.Utils.Mocks;

namespace NLightning.Infrastructure.Bitcoin.Tests.Gossip;

/// <summary>
/// An Esplora HTTP API over a <see cref="FakeBitcoinChain"/>: <c>GET block/{hash}/txid/{index}</c>,
/// <c>GET tx/{txid}/merkle-proof</c> and <c>GET block-height/{height}</c>, with hooks to answer something else
/// (429, 5xx, a lie).
/// </summary>
[ExcludeFromCodeCoverage]
internal sealed class FakeEsploraHandler(FakeBitcoinChain chain) : HttpMessageHandler
{
    private readonly Lock _gate = new();
    private readonly List<string> _requests = [];

    /// <summary>The request paths (after the base), in order.</summary>
    public IReadOnlyList<string> Requests
    {
        get
        {
            lock (_gate)
                return _requests.ToList();
        }
    }

    /// <summary>Responses served before the real ones, one per request (then the queue is empty).</summary>
    public Queue<Func<HttpResponseMessage>> Scripted { get; } = new();

    /// <summary>When set, the txid answered for a position (a lying index).</summary>
    public Func<uint256, uint, uint256>? TxIdOverride { get; set; }

    /// <summary>When set, replaces the proof answered for a txid (a lying index).</summary>
    public Func<uint256, (IReadOnlyList<uint256> Branch, int Position)?, (IReadOnlyList<uint256> Branch, int Position)?>?
        ProofOverride
    { get; set; }

    /// <summary>When set, the block hash answered for a height (an index on another chain, NL-424).</summary>
    public Func<uint, uint256>? BlockHeightOverride { get; set; }

    /// <summary>Set when a request arrives.</summary>
    public TaskCompletionSource RequestSeen { get; private set; } = NewSignal();

    public void ResetRequestSeen() => RequestSeen = NewSignal();

    public static HttpResponseMessage TooManyRequests(TimeSpan? retryAfter = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        if (retryAfter is { } delay)
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(delay);
        return response;
    }

    /// <summary>Bitcoin's merkle branch of <paramref name="index"/> (an odd level's last node paired with itself).</summary>
    public static List<uint256> Branch(IReadOnlyList<uint256> leaves, int index)
    {
        var level = leaves.ToList();
        var branch = new List<uint256>();
        while (level.Count > 1)
        {
            if (level.Count % 2 == 1)
                level.Add(level[^1]);

            branch.Add(level[index ^ 1]);
            var next = new List<uint256>(level.Count / 2);
            for (var i = 0; i < level.Count; i += 2)
                next.Add(Hashes.DoubleSHA256(level[i].ToBytes().Concat(level[i + 1].ToBytes()).ToArray()));
            level = next;
            index >>= 1;
        }

        return branch;
    }

    public static string ProofJson(IReadOnlyList<uint256> branch, int position, uint height) =>
        $"{{\"block_height\":{height},\"merkle\":[{string.Join(",", branch.Select(h => $"\"{h}\""))}],\"pos\":{position}}}";

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
                                                           CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath.TrimStart('/');
        if (path.StartsWith("api/", StringComparison.Ordinal))
            path = path["api/".Length..];

        lock (_gate)
            _requests.Add(path);
        RequestSeen.TrySetResult();

        if (Scripted.TryDequeue(out var scripted))
            return Task.FromResult(scripted());

        var parts = path.Split('/');
        if (parts is ["block-height", var heightText] && uint.TryParse(heightText, out var blockHeight))
        {
            if (BlockHeightOverride is { } overridden)
                return Task.FromResult(Text(overridden(blockHeight).ToString()));
            return Task.FromResult(blockHeight <= chain.TipHeight
                                       ? Text(chain[blockHeight].GetHash().ToString())
                                       : new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        if (parts is ["block", var hash, "txid", var indexText]
         && uint256.TryParse(hash, out var blockHash) && uint.TryParse(indexText, out var index))
        {
            var block = FindBlock(blockHash);
            if (block is null || index >= block.Transactions.Count)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

            var txId = block.Transactions[(int)index].GetHash();
            txId = TxIdOverride?.Invoke(txId, index) ?? txId;
            return Task.FromResult(Text(txId.ToString()));
        }

        if (parts is ["tx", var txIdText, "merkle-proof"] && uint256.TryParse(txIdText, out var provenTxId))
        {
            (IReadOnlyList<uint256> Branch, int Position, uint Height)? proof = null;
            for (var height = 0u; height <= chain.TipHeight && proof is null; height++)
            {
                var leaves = chain[height].Transactions.Select(t => t.GetHash()).ToList();
                var position = leaves.IndexOf(provenTxId);
                if (position >= 0)
                    proof = (Branch(leaves, position), position, height);
            }

            (IReadOnlyList<uint256> Branch, int Position)? answered =
                proof is { } p ? (p.Branch, p.Position) : null;
            if (ProofOverride is not null)
                answered = ProofOverride(provenTxId, answered);
            return Task.FromResult(answered is { } a
                                       ? Text(ProofJson(a.Branch, a.Position, proof?.Height ?? 0))
                                       : new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    private Block? FindBlock(uint256 blockHash)
    {
        for (var height = 0u; height <= chain.TipHeight; height++)
            if (chain[height].GetHash() == blockHash)
                return chain[height];
        return null;
    }

    private static HttpResponseMessage Text(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/plain") };

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>A logger that keeps what was logged (level and formatted message).</summary>
[ExcludeFromCodeCoverage]
internal sealed class CapturingLogger<T> : ILogger<T>
{
    private readonly Lock _gate = new();
    private readonly List<(LogLevel Level, string Message)> _entries = [];

    public IReadOnlyList<(LogLevel Level, string Message)> Entries
    {
        get
        {
            lock (_gate)
                return _entries.ToList();
        }
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                            Func<TState, Exception?, string> formatter)
    {
        lock (_gate)
            _entries.Add((logLevel, formatter(state, exception)));
    }
}