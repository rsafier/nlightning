using System.Collections.Concurrent;
using System.Net;
using k8s;
using k8s.Autorest;

namespace NLightning.Testing.Cluster.Run;

using Diagnostics;
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
        Scope = ClusterTestScope.Current();
        ClusterDiagnostics.Register(this);
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

    /// <summary>The xunit test or fixture the run was started from (names its diagnostics folder).</summary>
    public ClusterTestScope Scope { get; }

    /// <summary>Whether a failure was recorded on the run (<see cref="ClusterDiagnostics"/>), and its dumps.</summary>
    public RunDiagnosticsState Diagnostics { get; } = new();

    /// <summary>When and where the run's diagnostics are written (<see cref="TestRunOptions.Diagnostics"/>).</summary>
    public DiagnosticsSettings DiagnosticsSettings => _options.Diagnostics;

    /// <summary>Where the run writes what it does; null for nowhere.</summary>
    public Action<string>? Log => _options.Log;

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
    /// <remarks>
    /// The namespace records its owner (this process, or <see cref="TestRunOptions.Owner"/>) for the reaper. When the
    /// name is taken (a second run of a process under one <c>NLTG_TEST_RUN_ID</c>, or a namespace of that name still
    /// terminating) the run takes <c>&lt;id&gt;-2</c>, <c>&lt;id&gt;-3</c>, ... With
    /// <see cref="TestRunOptions.MaxConcurrentRuns"/> it first waits for a slot (<see cref="RunAdmission"/>).
    /// </remarks>
    public static async Task<TestRun> StartAsync(IKubernetes client, TestRunOptions options,
                                                 CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);

        if (options.AdoptNamespace)
        {
            if (string.IsNullOrWhiteSpace(options.RunId))
                throw new InvalidOperationException(
                    $"{TestRunOptions.AdoptNamespaceVariable} needs the run id ({TestRunId.EnvironmentVariable})");

            // The host created (and admitted) the namespace: adopt it as is, no cap, no derived id.
            var adopted = RunIdentity.Create(options, DateTimeOffset.UtcNow);
            await AdoptedNamespace.RequireOwnedAsync(client, adopted, cancellationToken).ConfigureAwait(false);
            options.Log?.Invoke($"[nltg-cluster] run {adopted.Id}: namespace {adopted.Namespace} adopted "
                              + $"({KubeClientFactory.DetectSource()})");
            return new TestRun(client, adopted, options, ownsNamespace: false);
        }

        var annotations = RunAnnotations.ForRun(options, options.Owner ?? RunOwner.Current());
        var baseId = TestRunId.Resolve(options.RunId);
        for (var attempt = 1; ; attempt++)
        {
            if (options.MaxConcurrentRuns is { } max)
                await RunAdmission.WaitForCapacityAsync(client, options.NamespacePrefix, max, options.AdmissionTimeout,
                                                        options.Log, cancellationToken).ConfigureAwait(false);

            var identity = await CreateNamespaceAsync(client, options, baseId, annotations, cancellationToken)
                               .ConfigureAwait(false);
            if (options.MaxConcurrentRuns is not { } cap
             || await RunAdmission.IsAdmittedAsync(client, identity, cap, cancellationToken).ConfigureAwait(false))
            {
                options.Log?.Invoke($"[nltg-cluster] run {identity.Id}: namespace {identity.Namespace} created");
                return new TestRun(client, identity, options, ownsNamespace: true);
            }

            // Another process raced past the cap at the same time and ranks before us: give the slot back.
            options.Log?.Invoke($"[nltg-cluster] run {identity.Id}: over the cap of {cap} runs, retrying");
            await RunNamespace.DeleteAsync(client, identity, cancellationToken).ConfigureAwait(false);
            await RunNamespace.WaitForDeletionAsync(client, identity.Namespace, options.DeletionTimeout,
                                                    cancellationToken).ConfigureAwait(false);
            if (attempt >= MaxAdmissionAttempts)
                throw new TimeoutException($"Run {baseId} lost the race for a slot {attempt} times");

            await Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.Next(500, 3000)), cancellationToken)
                      .ConfigureAwait(false);
        }
    }

    /// <summary>How often a run gives its slot back after a race before it gives up.</summary>
    private const int MaxAdmissionAttempts = 20;

    /// <summary>How many derived ids (<c>&lt;id&gt;-&lt;n&gt;</c>) a run tries when its name is taken.</summary>
    private const int MaxIdSuffix = 20;

    private static async Task<RunIdentity> CreateNamespaceAsync(IKubernetes client, TestRunOptions options,
                                                                string baseId,
                                                                IReadOnlyDictionary<string, string> annotations,
                                                                CancellationToken cancellationToken)
    {
        for (var n = 1; ; n++)
        {
            var identity = RunIdentity.Create(options with { RunId = TestRunId.WithSuffix(baseId, n) },
                                              DateTimeOffset.UtcNow);
            try
            {
                await RunNamespace.CreateAsync(client, identity, options.Quota, annotations, cancellationToken)
                                  .ConfigureAwait(false);
                return identity;
            }
            catch (HttpOperationException e) when (e.Response.StatusCode == HttpStatusCode.Conflict
                                                && n < MaxIdSuffix)
            {
                options.Log?.Invoke($"[nltg-cluster] namespace {identity.Namespace} exists, trying the next id");
            }
        }
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
            await this.CaptureOnFailureAsync($"node {workload.Name} ready",
                                             () => handle.WaitReadyAsync(timeout, cancellationToken))
                      .ConfigureAwait(false);

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

        ClusterDiagnostics.Unregister(this);
        try
        {
            await ClusterDiagnostics.BeforeDeletionAsync(this).ConfigureAwait(false);
            if (_options.KeepNamespace)
            {
                _options.Log?.Invoke($"[nltg-cluster] run {Id}: namespace {Namespace} kept "
                                   + $"({TestRunOptions.KeepNamespaceVariable})");
                return;
            }

            if (_options.KeepNamespaceOnFailure && Diagnostics.Failed)
            {
                await KeepFailedNamespaceAsync().ConfigureAwait(false);
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

    /// <summary>
    /// Keeps a failed run's namespace (<c>NLTG_KEEP_NAMESPACE=failure</c>): annotates it <see cref="RunAnnotations.Keep"/>
    /// so the reaper leaves it until its TTL even after this process ends. An adopted namespace keeps its nodes.
    /// </summary>
    private async Task KeepFailedNamespaceAsync()
    {
        if (OwnsNamespace)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await RunAnnotations.MarkKeptAsync(Client, Identity, cts.Token).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                _options.Log?.Invoke($"[nltg-cluster] run {Id}: namespace {Namespace} not annotated "
                                   + $"{RunAnnotations.Keep}: {e.Message}");
            }
        }

        _options.Log?.Invoke($"[nltg-cluster] run {Id}: namespace {Namespace} kept on failure "
                           + $"({TestRunOptions.KeepNamespaceVariable}=failure; reaped after its TTL): "
                           + Diagnostics.FailureReason);
    }
}