using System.Collections.Concurrent;
using k8s;

namespace NLightning.Testing.Cluster.Run;

using Kube;
using Nodes;
using Runner;

/// <summary>
/// One run of the harness (plan R1, R3): its own namespace <c>&lt;prefix&gt;-&lt;run id&gt;</c>, labelled
/// <c>nltg.run</c>/<c>nltg.suite</c>/<c>nltg.started</c> (and <c>nltg.spike</c> in the spike), the nodes deployed into
/// it, and its cleanup: disposing deletes the namespace, and with it everything the run created, after checking that
/// the namespace is the run's own.
/// </summary>
/// <example>
/// <code>
/// await using var run = await TestRun.StartAsync(TestRunOptions.FromEnvironment("cln"), ct);
/// var miner = await run.DeployAsync(BitcoinCoreWorkload("miner"), TimeSpan.FromMinutes(2), ct);
/// </code>
/// </example>
public sealed class TestRun : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, KubeNodeHandle> _nodes = new(StringComparer.Ordinal);
    private readonly TestRunOptions _options;
    private int _disposed;

    private TestRun(IKubernetes client, RunIdentity identity, TestRunOptions options, bool ownsNamespace)
    {
        Client = client;
        Identity = identity;
        _options = options;
        OwnsNamespace = ownsNamespace;
    }

    /// <summary>The cluster client of the run.</summary>
    public IKubernetes Client { get; }

    /// <summary>Names and labels of the run.</summary>
    public RunIdentity Identity { get; }

    public string Id => Identity.Id;

    public string Namespace => Identity.Namespace;

    /// <summary>
    /// False when the run adopted an existing namespace (<see cref="TestRunOptions.AdoptNamespace"/>): disposing
    /// then removes only the run's nodes and leaves the namespace to whoever created it.
    /// </summary>
    public bool OwnsNamespace { get; }

    /// <summary>The nodes deployed so far, by alias.</summary>
    public IReadOnlyDictionary<string, KubeNodeHandle> Nodes => _nodes;

    /// <summary>
    /// Creates the run's namespace (and quota) with a client from <see cref="KubeClientFactory"/>.
    /// </summary>
    public static async Task<TestRun> StartAsync(TestRunOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        var client = KubeClientFactory.Create(options.KubeContext);
        try
        {
            return await StartAsync(client, options, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Creates the run's namespace (and quota) with <paramref name="client"/>; the run disposes the client.
    /// </summary>
    public static async Task<TestRun> StartAsync(IKubernetes client, TestRunOptions options,
                                                 CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);

        var identity = RunIdentity.Create(options, DateTimeOffset.UtcNow);
        if (options.AdoptNamespace)
        {
            if (string.IsNullOrWhiteSpace(options.RunId))
                throw new InvalidOperationException(
                    $"{TestRunOptions.AdoptNamespaceVariable} needs the run id ({TestRunId.EnvironmentVariable})");

            await AdoptedNamespace.RequireOwnedAsync(client, identity, cancellationToken).ConfigureAwait(false);
            options.Log?.Invoke($"[nltg-cluster] run {identity.Id}: namespace {identity.Namespace} adopted "
                              + $"({KubeClientFactory.DetectSource()})");
            return new TestRun(client, identity, options, ownsNamespace: false);
        }

        await RunNamespace.CreateAsync(client, identity, options.Quota, cancellationToken).ConfigureAwait(false);
        options.Log?.Invoke($"[nltg-cluster] run {identity.Id}: namespace {identity.Namespace} created");
        return new TestRun(client, identity, options, ownsNamespace: true);
    }

    /// <summary>
    /// Creates <paramref name="workload"/>'s Service and StatefulSet in the run's namespace and, with a
    /// <paramref name="readyTimeout"/>, waits until its pod is ready.
    /// </summary>
    public async Task<KubeNodeHandle> DeployAsync(NodeWorkload workload, TimeSpan? readyTimeout,
                                                  CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workload);
        ObjectDisposedException.ThrowIf(_disposed != 0, this);

        var handle = new KubeNodeHandle(Client, Namespace, workload.Name, workload.Kind,
                                        workload.TerminationGracePeriodSeconds);
        if (!_nodes.TryAdd(workload.Name, handle))
            throw new InvalidOperationException($"Run {Id} already has a node named {workload.Name}");

        await Client.ApplyAsync(workload.Build(Identity), cancellationToken).ConfigureAwait(false);
        if (readyTimeout is { } timeout)
            await handle.WaitReadyAsync(timeout, cancellationToken).ConfigureAwait(false);

        return handle;
    }

    /// <summary>The handle of a deployed node.</summary>
    public KubeNodeHandle GetNode(string name) =>
        _nodes.TryGetValue(name, out var handle)
            ? handle
            : throw new KeyNotFoundException($"Run {Id} has no node named {name}");

    /// <summary>
    /// Deletes the run's namespace (unless <see cref="TestRunOptions.KeepNamespace"/>) and waits for it to go when
    /// <see cref="TestRunOptions.WaitForDeletion"/>; then disposes the client.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        try
        {
            if (_options.KeepNamespace)
            {
                _options.Log?.Invoke($"[nltg-cluster] run {Id}: namespace {Namespace} kept "
                                   + $"({TestRunOptions.KeepNamespaceVariable})");
                return;
            }

            using var cts = new CancellationTokenSource(_options.DeletionTimeout + TimeSpan.FromSeconds(30));
            if (!OwnsNamespace)
            {
                await AdoptedNamespace.DeleteNodesAsync(Client, Namespace, _nodes.Keys, _options.DeletionTimeout,
                                                        cts.Token)
                                      .ConfigureAwait(false);
                _options.Log?.Invoke($"[nltg-cluster] run {Id}: {_nodes.Count} node(s) removed from adopted "
                                   + $"namespace {Namespace}");
                return;
            }

            if (await RunNamespace.DeleteAsync(Client, Identity, cts.Token).ConfigureAwait(false)
             && _options.WaitForDeletion)
                await RunNamespace.WaitForDeletionAsync(Client, Namespace, _options.DeletionTimeout, cts.Token)
                                  .ConfigureAwait(false);
            _options.Log?.Invoke($"[nltg-cluster] run {Id}: namespace {Namespace} deleted");
        }
        finally
        {
            Client.Dispose();
        }
    }
}