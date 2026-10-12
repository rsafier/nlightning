using Newtonsoft.Json.Linq;

namespace NLightning.Testing.Cluster.Tests.Nodes.BitcoinCore;

using Cluster.Nodes.BitcoinCore.Rpc;

/// <summary>Answers RPC calls from a per-method queue of results or errors and records every call.</summary>
internal sealed class FakeRpcTransport : IBitcoinRpcTransport
{
    private readonly Dictionary<string, Queue<Func<JToken>>> _answers = new(StringComparer.Ordinal);

    public List<(string Method, IReadOnlyDictionary<string, object?> Args)> Calls { get; } = [];

    public string Description => "fake transport";

    public FakeRpcTransport Returns(string method, JToken result) => Enqueue(method, () => result);

    public FakeRpcTransport Fails(string method, int? code, string message = "error") =>
        Enqueue(method, () => throw new BitcoinRpcException(method, code, message));

    public Task<JToken> CallAsync(string method, IReadOnlyDictionary<string, object?>? namedArgs,
                                  CancellationToken cancellationToken)
    {
        Calls.Add((method, namedArgs ?? new Dictionary<string, object?>()));
        if (!_answers.TryGetValue(method, out var queue) || queue.Count == 0)
            throw new InvalidOperationException($"No answer for {method}");

        return Task.FromResult(queue.Dequeue()());
    }

    public IReadOnlyDictionary<string, object?> ArgsOf(string method) => Calls.Single(c => c.Method == method).Args;

    private FakeRpcTransport Enqueue(string method, Func<JToken> answer)
    {
        if (!_answers.TryGetValue(method, out var queue))
            _answers[method] = queue = new Queue<Func<JToken>>();
        queue.Enqueue(answer);
        return this;
    }
}