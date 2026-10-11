using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;

namespace NLightning.Integration.Tests.Cluster;

using Docker.Utils;
using Domain.Node.Options;
using Domain.Protocol.Interfaces;
using Testing.Cluster.Nodes;
using Testing.Cluster.Reach;
using Testing.Cluster.Topology;

/// <summary>
/// The <see cref="ILightningNodeDeployer"/> of <see cref="NodeKind.NLightning"/>: each such node of a topology is an
/// <see cref="NLightningTestNode"/> started in the test process (no pod), on the topology's bitcoind
/// (<see cref="ClusterChainEndpoint"/>), listening where the pods reach it (<see cref="HostEndpoints"/>). Register it
/// with <see cref="InProcessTopologyExtensions.UseInProcessNodes"/>; dispose it after the topology and before the run, which
/// stops every node it started and deletes their SQLite files.
/// </summary>
/// <remarks>
/// Why the glue lives here and not in <c>NLightning.Testing.Cluster</c>: that library references no NLightning
/// project (every implementation goes through the same seams), while the node is built from the daemon's composition
/// (<c>AddNltgNodeServices</c>) in this project. The library already has the seams (<see cref="ILightningNodeDeployer"/>,
/// <see cref="NodeKind.NLightning"/>, <see cref="ITopologyLightningNode"/>), so the adapter plugs in without a new
/// project and without a reference from the library to the product.
/// </remarks>
public sealed class InProcessNodeDeployer : ILightningNodeDeployer, IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, InProcessNode> _nodes = new(StringComparer.Ordinal);
    private bool _disposed;

    /// <summary>The <see cref="NodeOptions"/> changes every node gets (applied last, on every start).</summary>
    public Action<string, NodeOptions>? ConfigureNodeOptions { get; init; }

    /// <summary>
    /// Changes to each node before its first start (by alias): <see cref="NLightningTestNode.ExtraConfiguration"/>,
    /// <see cref="NLightningTestNode.ConfigureServices"/>, ...
    /// </summary>
    public Action<NLightningTestNode>? ConfigureNode { get; init; }

    /// <summary>Optional externally owned key manager, e.g. a proxy to a separate signer process.</summary>
    public Func<string, ISecureKeyManager>? KeyManager { get; init; }

    /// <summary>The database of each node (by alias); a SQLite file of its own when null.</summary>
    public Func<string, TestNodeDatabase?>? Database { get; init; }

    /// <summary>The open every node uses for the topology's channels it funds (v1 unless set).</summary>
    public InProcessOpenMode DefaultOpenMode { get; init; } = InProcessOpenMode.V1;

    /// <summary>The host pods dial to reach the nodes; <see cref="HostEndpoints.ForPods"/> when null.</summary>
    public string? PodFacingHost { get; init; }

    public NodeKind Kind => NodeKind.NLightning;

    /// <summary>The nodes started so far, by alias.</summary>
    public IReadOnlyDictionary<string, InProcessNode> Nodes => _nodes;

    /// <summary>
    /// Builds and starts the node: a pooled port, a fresh key, a SQLite file, the topology's bitcoind by pod IP (or
    /// Service name in the cluster). <see cref="TopologyNodeSpec.ExtraArgs"/> are <c>Section:Key=value</c> settings
    /// layered over the test node's own (<see cref="NLightningTestNode.ExtraConfiguration"/>).
    /// </summary>
    public async Task<ITopologyLightningNode> DeployAsync(TopologyDeployContext context, TopologyNodeSpec node,
                                                          CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(node);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (node.Image is not null)
            throw new ArgumentException($"{node.Name}: an in-process node has no image", nameof(node));

        var settings = ParseSettings(node.Args);
        var watch = Stopwatch.StartNew();
        var podFacingHost = PodFacingHost ?? HostEndpoints.ForPods();
        var bindAddress = HostEndpoints.BindAddressFor(podFacingHost);
        var endpoint = ClusterChainEndpoint.Create(context.Chain);

        NLightningTestNode? created = null;
        created = await NLightningTestNode.CreateAsync(endpoint, node.Name, Database?.Invoke(node.Name), options =>
        {
            // The test node binds loopback; an explicit pod-facing host needs every interface
            if (!Equals(bindAddress, IPAddress.Loopback))
                options.ListenAddresses = [$"{bindAddress}:{created!.Port}"];
            ConfigureNodeOptions?.Invoke(node.Name, options);
        }, KeyManager?.Invoke(node.Name));
        var adapter = new InProcessNode(created, context.Run.Identity, podFacingHost)
        {
            DefaultOpenMode = DefaultOpenMode
        };
        if (!_nodes.TryAdd(node.Name, adapter))
        {
            await created.DisposeAsync();
            throw new InvalidOperationException($"An in-process node named {node.Name} was already deployed");
        }

        foreach (var (key, value) in settings)
            created.ExtraConfiguration[key] = value;
        ConfigureNode?.Invoke(created);

        using var ready = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        ready.CancelAfter(context.ReadyTimeout);
        await created.StartAsync(ready.Token);
        context.Log?.Invoke($"[topology] {context.Run.Namespace}: in-process {node.Name} {created.NodeIdHex} on "
                          + $"{bindAddress}:{created.Port} (pods dial {podFacingHost}), chain {endpoint.ZmqHost}, "
                          + $"started in {watch.Elapsed.TotalSeconds:F1} s");
        return adapter;
    }

    /// <summary>The node named <paramref name="name"/>.</summary>
    public InProcessNode Node(string name) =>
        _nodes.TryGetValue(name, out var node)
            ? node
            : throw new KeyNotFoundException($"No in-process node named {name} was deployed");

    /// <summary>Stops every node and releases its port, key and SQLite file.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        foreach (var node in _nodes.Values)
        {
            try
            {
                await node.TestNode.DisposeAsync();
            }
            catch (Exception e)
            {
                Console.WriteLine($"[{node.Alias}] failed to stop: {e.Message}");
            }
        }
    }

    /// <summary>
    /// <c>Section:Key=value</c> arguments as configuration entries.
    /// </summary>
    /// <exception cref="ArgumentException">An argument without <c>=</c> or with an empty key.</exception>
    public static IReadOnlyList<KeyValuePair<string, string?>> ParseSettings(IEnumerable<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var settings = new List<KeyValuePair<string, string?>>();
        foreach (var arg in args)
        {
            var separator = arg.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0)
                throw new ArgumentException($"In-process node setting '{arg}' is not Section:Key=value", nameof(args));

            settings.Add(new KeyValuePair<string, string?>(arg[..separator].Trim(), arg[(separator + 1)..]));
        }

        return settings;
    }
}

/// <summary>Declares in-process NLightning nodes in a <see cref="TopologyBuilder"/>.</summary>
public static class InProcessTopologyExtensions
{
    /// <summary>
    /// An in-process NLightning node named <paramref name="name"/>; <paramref name="settings"/> are
    /// <c>Section:Key=value</c> configuration entries (e.g. <c>Node:Alias=nltg</c>).
    /// </summary>
    public static TopologyBuilder AddNLightning(this TopologyBuilder builder, string name, params string[] settings)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddNode(name, NodeKind.NLightning, null, settings);
    }

    /// <summary>Registers <paramref name="deployer"/> for the topology's NLightning nodes.</summary>
    public static TopologyBuilder UseInProcessNodes(this TopologyBuilder builder, InProcessNodeDeployer deployer)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.UseDeployer(deployer);
    }

    /// <summary>The in-process node named <paramref name="name"/> of a built topology.</summary>
    public static InProcessNode InProcessNode(this TestTopology topology, string name)
    {
        ArgumentNullException.ThrowIfNull(topology);
        return topology.Node<InProcessNode>(name);
    }
}