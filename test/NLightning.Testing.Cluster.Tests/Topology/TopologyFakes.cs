using System.Text;

namespace NLightning.Testing.Cluster.Tests.Topology;

using Cluster.Kube;
using Cluster.Nodes;
using Cluster.Topology;

/// <summary>
/// An <see cref="INodeHandle"/> without a cluster: <see cref="ExecAsync"/> records the command and answers with
/// <see cref="Respond"/>.
/// </summary>
internal sealed class FakeNodeHandle(string name, NodeKind kind = NodeKind.Cln, string ns = "nltg-spike-r1")
    : INodeHandle
{
    public List<IReadOnlyList<string>> Commands { get; } = [];

    public Func<IReadOnlyList<string>, ExecResult> Respond { get; set; } = _ => Ok("{}");

    public string Name { get; } = name;

    public NodeKind Kind { get; } = kind;

    public string Namespace { get; } = ns;

    public string PodName => $"{Name}-0";

    public string ContainerName => Name;

    public string ServiceDnsName => $"{Name}.{Namespace}.svc.cluster.local";

    public string PodDnsName => $"{PodName}.{Name}.{Namespace}.svc.cluster.local";

    public string? PodIp => "10.0.0.1";

    public static ExecResult Ok(string stdout) => new(0, Encoding.UTF8.GetBytes(stdout), []);

    public static ExecResult Fail(int exitCode, string stdout, string stderr = "") =>
        new(exitCode, Encoding.UTF8.GetBytes(stdout), Encoding.UTF8.GetBytes(stderr));

    public Task<ExecResult> ExecAsync(IReadOnlyList<string> command, CancellationToken cancellationToken)
    {
        Commands.Add(command);
        return Task.FromResult(Respond(command));
    }

    public Task WaitReadyAsync(TimeSpan timeout, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<byte[]> ReadFileAsync(string path, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<byte[]> WaitForFileAsync(string path, TimeSpan timeout, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task WriteFileAsync(string path, ReadOnlyMemory<byte> content, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<string> ReadLogAsync(int? tailLines, CancellationToken cancellationToken) =>
        Task.FromResult(string.Empty);

    public Task RestartAsync(TimeSpan readyTimeout, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task KillAsync(TimeSpan readyTimeout, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>A chain whose tip the test sets.</summary>
internal sealed class FakeChain(INodeHandle node) : ITopologyChain
{
    public long Tip { get; set; } = 101;

    public INodeHandle Node { get; } = node;

    public string RpcHost => Node.Name;

    public int RpcPort => 18443;

    public string RpcUser => "user";

    public string RpcPassword => "secret";

    public Task<long> GetBlockCountAsync(CancellationToken cancellationToken) => Task.FromResult(Tip);

    public Task<IReadOnlyList<string>> MineAsync(int blocks, CancellationToken cancellationToken)
    {
        Tip += blocks;
        return Task.FromResult<IReadOnlyList<string>>([]);
    }

    public Task<string> SendToAddressAsync(string address, long amountSat, CancellationToken cancellationToken) =>
        Task.FromResult("txid");
}

/// <summary>A Lightning node whose height, channels and connect outcome the test sets.</summary>
internal sealed class FakeLightningNode(string alias) : ITopologyLightningNode
{
    public long Height { get; set; }

    public List<TestChannel> Channels { get; } = [];

    /// <summary>How many connect attempts fail before one succeeds.</summary>
    public int FailingConnects { get; set; }

    public int ConnectAttempts { get; private set; }

    public INodeHandle? Node => null;

    public NodeKind Kind => NodeKind.Cln;

    public string Alias { get; } = alias;

    public Task<string> GetNodeIdAsync(CancellationToken cancellationToken) => Task.FromResult($"id-{Alias}");

    public Task<TestPeerAddress> GetAddressAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new TestPeerAddress($"id-{Alias}", Alias, 9735));

    public Task ConnectAsync(TestPeerAddress peer, CancellationToken cancellationToken)
    {
        ConnectAttempts++;
        return ConnectAttempts <= FailingConnects
                   ? Task.FromException(new InvalidOperationException($"refused {ConnectAttempts}"))
                   : Task.CompletedTask;
    }

    public Task DisconnectAsync(string nodeId, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<string> GetNewAddressAsync(CancellationToken cancellationToken) => Task.FromResult("bcrt1q");

    public Task<TestChannelOpen> OpenChannelAsync(TestOpenChannelRequest request,
                                                  CancellationToken cancellationToken) =>
        Task.FromResult(new TestChannelOpen("txid", 0));

    public Task<IReadOnlyList<TestChannel>> ListChannelsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<TestChannel>>([.. Channels]);

    public Task<TestInvoice> CreateInvoiceAsync(long? amountMsat, string description,
                                                CancellationToken cancellationToken) =>
        Task.FromResult(new TestInvoice("lnbcrt", "hash"));

    public Task<TestPaymentResult> PayInvoiceAsync(string bolt11, CancellationToken cancellationToken) =>
        Task.FromResult(new TestPaymentResult(true, "00", null));

    public Task<long> GetBlockHeightAsync(CancellationToken cancellationToken) => Task.FromResult(Height);

    public Task<long> GetConfirmedBalanceSatAsync(CancellationToken cancellationToken) => Task.FromResult(0L);
}