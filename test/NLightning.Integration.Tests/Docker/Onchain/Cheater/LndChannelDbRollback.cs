using System.Buffers.Binary;
using System.Net;
using Docker.DotNet;
using Docker.DotNet.Models;
using NLightning.Testing.Lnd;
using NLightning.Testing.Lnd.Lnrpc;

namespace NLightning.Integration.Tests.Docker.Onchain.Cheater;

using Fixtures;
using Utils;

/// <summary>
/// Makes an LND container cheat (BOLT 5 plan §5 Proof O5 (a)): a copy of its <c>channel.db</c> is taken while the
/// container is stopped, and later put back while it is stopped again, so LND restarts on an old channel state and a
/// force close broadcasts a revoked commitment.
/// </summary>
/// <remarks>
/// <para>The database lives under the data directory of the image (<c>test/Docker/custom_lnd</c>: <c>VOLUME
/// /home/lnd/.lnd</c>, LND runs as the <c>lnd</c> user); the plan's <c>/root/.lnd/...</c> is tried as well and the
/// path found is logged.</para>
/// <para>Docker backend: the archive is copied out of and back into the stopped container. A restarted container gets
/// the lowest free address of its Docker network (NL-262), so the free addresses below the container's are held by
/// idle containers during every restart, and the address is checked unchanged (the fixture's gRPC connection keeps
/// working).</para>
/// <para>Cluster backend (test harness phase 6): the node's StatefulSet is stopped and a maintenance pod on its PVC
/// copies the database to <see cref="SnapshotSuffix"/> next to it, and later back
/// (<c>LndRegtestNetwork.RestartAsync</c> with a stop window, which also has the network's LND peers redial the new
/// pod IP); the snapshot never leaves the PVC.</para>
/// </remarks>
public sealed class LndChannelDbRollback : IDisposable
{
    public static readonly IReadOnlyList<string> CandidatePaths =
    [
        "/home/lnd/.lnd/data/graph/regtest/channel.db",
        "/root/.lnd/data/graph/regtest/channel.db"
    ];

    /// <summary>The cluster backend's snapshot, next to the database on the node's PVC.</summary>
    public const string SnapshotSuffix = ".nltg-snapshot";

    private static readonly TimeSpan s_syncTimeout = TimeSpan.FromMinutes(2);

    private readonly string _container;
    private readonly DockerClient? _docker;
    private readonly LightningRegtestNetworkFixture _fixture;
    private byte[]? _snapshot;
    private bool _clusterSnapshot;

    /// <summary>The database path found in the container (or on the pod's PVC).</summary>
    public string? DatabasePath { get; private set; }

    /// <param name="fixture">The network (Docker or cluster backend).</param>
    /// <param name="container">The LND node's container name, its alias on the cluster.</param>
    public LndChannelDbRollback(LightningRegtestNetworkFixture fixture, string container)
    {
        _fixture = fixture;
        _container = container;
        if (fixture.Cluster is null)
            _docker = new DockerClientConfiguration().CreateClient();
    }

    private DockerClient DockerApi =>
        _docker ?? throw new InvalidOperationException("The Docker client is used on the Docker backend only");

    /// <summary>Stops the node, copies its <c>channel.db</c> and starts it again.</summary>
    public async Task TakeSnapshotAsync(CancellationToken ct)
    {
        if (_fixture.Cluster is { } cluster)
        {
            await cluster.Network.RestartAsync(_container, cancellationToken: ct, whileStopped: async (shell, token) =>
            {
                // The first candidate that exists, copied next to itself (owner and mode kept)
                var script = string.Join(" ", CandidatePaths.Select(p =>
                                 $"if [ -f '{p}' ]; then cp -p '{p}' '{p}{SnapshotSuffix}' && echo '{p}' && exit 0; fi;"))
                           + " echo 'no channel.db' >&2; exit 1";
                var result = await shell.RunScriptAsync(script, token);
                DatabasePath = result.StdOutText.Trim();
                _clusterSnapshot = true;
                Console.WriteLine($"[lnd-rollback] {_container}: copied {DatabasePath} to {DatabasePath}{SnapshotSuffix} "
                                + $"on its PVC ({shell.PodName})");
            });
            await WaitSyncedAsync(ct);
            return;
        }

        await RestartAroundAsync(async () =>
        {
            foreach (var path in CandidatePaths)
            {
                try
                {
                    var archive = await DockerApi.Containers.GetArchiveFromContainerAsync(
                                      _container, new GetArchiveFromContainerParameters { Path = path }, false, ct);
                    using var copy = new MemoryStream();
                    await archive.Stream.CopyToAsync(copy, ct);
                    _snapshot = copy.ToArray();
                    DatabasePath = path;
                    Console.WriteLine($"[lnd-rollback] {_container}: copied {path} ({_snapshot.Length} bytes of tar)");
                    return;
                }
                catch (DockerContainerNotFoundException)
                {
                    throw;
                }
                catch (DockerApiException e) when (e.StatusCode == HttpStatusCode.NotFound)
                {
                    Console.WriteLine($"[lnd-rollback] {_container}: no {path}");
                }
            }

            throw new InvalidOperationException(
                $"No channel.db in {_container} at {string.Join(" or ", CandidatePaths)}");
        }, ct);
    }

    /// <summary>Stops the node, puts the snapshot back and starts it again.</summary>
    public async Task RestoreSnapshotAsync(CancellationToken ct)
    {
        if (_fixture.Cluster is { } cluster)
        {
            if (!_clusterSnapshot || DatabasePath is null)
                throw new InvalidOperationException("Take a snapshot first");

            var path = DatabasePath;
            await cluster.Network.RestartAsync(_container, cancellationToken: ct, whileStopped: async (shell, token) =>
            {
                await shell.RunScriptAsync($"cp -p '{path}{SnapshotSuffix}' '{path}'", token);
                Console.WriteLine($"[lnd-rollback] {_container}: restored {path} on its PVC ({shell.PodName})");
            });
            await WaitSyncedAsync(ct);
            return;
        }

        if (_snapshot is null || DatabasePath is null)
            throw new InvalidOperationException("Take a snapshot first");

        await RestartAroundAsync(async () =>
        {
            using var archive = new MemoryStream(_snapshot);
            await DockerApi.Containers.ExtractArchiveToContainerAsync(
                _container, new ContainerPathStatParameters { Path = Path.GetDirectoryName(DatabasePath)! }, archive,
                ct);
            Console.WriteLine($"[lnd-rollback] {_container}: restored {DatabasePath}");
        }, ct);
    }

    /// <summary>
    /// Waits until the container's LND answers and is synced to the chain (after a restart).
    /// </summary>
    public async Task<LndNodeConnection> WaitSyncedAsync(CancellationToken ct)
    {
        return await Poll.ForAsync(async () =>
        {
            try
            {
                var lnd = _fixture.GetLndNode(_container);
                var info = await lnd.LightningClient.GetInfoAsync(new GetInfoRequest(),
                                                                  deadline: DateTime.UtcNow.AddSeconds(5),
                                                                  cancellationToken: ct);
                return info.SyncedToChain ? lnd : null;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                return null;
            }
        }, s_syncTimeout, $"{_container} synced after its restart", ct);
    }

    private async Task RestartAroundAsync(Func<Task> whileStopped, CancellationToken ct)
    {
        _fixture.RequireDocker(nameof(LndChannelDbRollback));
        var addressBefore = await GetAddressAsync(ct);
        await DockerApi.Containers.StopContainerAsync(_container,
                                                    new ContainerStopParameters { WaitBeforeKillSeconds = 30 }, ct);
        var placeholders = new List<string>();
        try
        {
            await whileStopped();
            placeholders = await HoldAddressesBelowAsync(addressBefore, ct);
            await DockerApi.Containers.StartContainerAsync(_container, new ContainerStartParameters(), ct);
        }
        finally
        {
            foreach (var placeholder in placeholders)
                await DockerContainerUtils.RemoveContainerAsync(DockerApi, placeholder);
        }

        Assert.Equal(addressBefore, await GetAddressAsync(ct));
        await WaitSyncedAsync(ct);
    }

    private async Task<IPAddress> GetAddressAsync(CancellationToken ct)
    {
        var inspection = await DockerApi.Containers.InspectContainerAsync(_container, ct);
        var endpoint = inspection.NetworkSettings.Networks.Single().Value;
        return string.IsNullOrEmpty(endpoint.IPAddress) ? IPAddress.None : IPAddress.Parse(endpoint.IPAddress);
    }

    /// <summary>
    /// Starts idle containers until no address below <paramref name="own"/> is free in the container's network, so
    /// the restart gives the container its own address back (as <c>ReestablishFlowTests</c> does).
    /// </summary>
    private async Task<List<string>> HoldAddressesBelowAsync(IPAddress own, CancellationToken ct)
    {
        const int maxPlaceholders = 64;
        var inspection = await DockerApi.Containers.InspectContainerAsync(_container, ct);
        var networkName = inspection.NetworkSettings.Networks.Single().Key;
        var ownValue = ToUInt32(own);
        var placeholders = new List<string>();
        while (placeholders.Count < maxPlaceholders)
        {
            var network = await DockerApi.Networks.InspectNetworkAsync(networkName, ct);
            var ipam = network.IPAM.Config.First(c => c.Subnet.Contains('.'));
            var first = ToUInt32(IPAddress.Parse(ipam.Subnet.Split('/')[0])) + 1;
            var used = network.Containers.Values
                              .Select(c => ToUInt32(IPAddress.Parse(c.IPv4Address.Split('/')[0])))
                              .ToHashSet();
            if (!string.IsNullOrEmpty(ipam.Gateway))
                used.Add(ToUInt32(IPAddress.Parse(ipam.Gateway)));
            if (Enumerable.Range(0, (int)(ownValue - first)).All(i => used.Contains(first + (uint)i)))
                return placeholders;

            var name = $"nltg-o5-address-hold-{placeholders.Count}";
            await DockerContainerUtils.RemoveContainerAsync(DockerApi, name);
            await DockerApi.Containers.CreateContainerAsync(new CreateContainerParameters
            {
                Name = name,
                Image = inspection.Config.Image,
                Entrypoint = ["sleep"],
                Cmd = ["600"],
                HostConfig = new HostConfig { NetworkMode = networkName }
            }, ct);
            placeholders.Add(name);
            await DockerApi.Containers.StartContainerAsync(name, new ContainerStartParameters(), ct);
        }

        return placeholders;

        static uint ToUInt32(IPAddress address) => BinaryPrimitives.ReadUInt32BigEndian(address.GetAddressBytes());
    }

    public void Dispose() => _docker?.Dispose();
}