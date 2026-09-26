using System.Buffers.Binary;
using System.Net;
using Docker.DotNet;
using Docker.DotNet.Models;
using Lnrpc;
using LNUnit.LND;

namespace NLightning.Integration.Tests.Docker.Onchain.Cheater;

using Fixtures;
using Utils;

/// <summary>
/// Makes an LND container cheat (BOLT 5 plan §5 Proof O5 (a)): a copy of its <c>channel.db</c> is taken while the
/// container is stopped, and later put back while it is stopped again, so LND restarts on an old channel state and a
/// force close broadcasts a revoked commitment.
/// </summary>
/// <remarks>
/// The database lives under the data directory of the image (<c>test/Docker/custom_lnd</c>: <c>VOLUME
/// /home/lnd/.lnd</c>, LND runs as the <c>lnd</c> user); the plan's <c>/root/.lnd/...</c> is tried as well and the
/// path found is logged. A restarted container gets the lowest free address of its Docker network (NL-262), so the
/// free addresses below the container's are held by idle containers during every restart, and the address is checked
/// unchanged (the fixture's gRPC connection keeps working).
/// </remarks>
public sealed class LndChannelDbRollback : IDisposable
{
    public static readonly IReadOnlyList<string> CandidatePaths =
    [
        "/home/lnd/.lnd/data/graph/regtest/channel.db",
        "/root/.lnd/data/graph/regtest/channel.db"
    ];

    private static readonly TimeSpan s_syncTimeout = TimeSpan.FromMinutes(2);

    private readonly string _container;
    private readonly DockerClient _docker = new DockerClientConfiguration().CreateClient();
    private readonly LightningRegtestNetworkFixture _fixture;
    private byte[]? _snapshot;

    /// <summary>The database path found in the container.</summary>
    public string? DatabasePath { get; private set; }

    public LndChannelDbRollback(LightningRegtestNetworkFixture fixture, string container)
    {
        _fixture = fixture;
        _container = container;
    }

    /// <summary>Stops the container, copies its <c>channel.db</c> (a tar archive) and starts it again.</summary>
    public async Task TakeSnapshotAsync(CancellationToken ct)
    {
        await RestartAroundAsync(async () =>
        {
            foreach (var path in CandidatePaths)
            {
                try
                {
                    var archive = await _docker.Containers.GetArchiveFromContainerAsync(
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

    /// <summary>Stops the container, puts the snapshot back and starts it again.</summary>
    public async Task RestoreSnapshotAsync(CancellationToken ct)
    {
        if (_snapshot is null || DatabasePath is null)
            throw new InvalidOperationException("Take a snapshot first");

        await RestartAroundAsync(async () =>
        {
            using var archive = new MemoryStream(_snapshot);
            await _docker.Containers.ExtractArchiveToContainerAsync(
                _container, new ContainerPathStatParameters { Path = Path.GetDirectoryName(DatabasePath)! }, archive,
                ct);
            Console.WriteLine($"[lnd-rollback] {_container}: restored {DatabasePath}");
        }, ct);
    }

    /// <summary>
    /// Waits until the container's LND answers and is synced to the chain (after a restart).
    /// </summary>
    public async Task<LNDNodeConnection> WaitSyncedAsync(CancellationToken ct)
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
        var addressBefore = await GetAddressAsync(ct);
        await _docker.Containers.StopContainerAsync(_container,
                                                    new ContainerStopParameters { WaitBeforeKillSeconds = 30 }, ct);
        var placeholders = new List<string>();
        try
        {
            await whileStopped();
            placeholders = await HoldAddressesBelowAsync(addressBefore, ct);
            await _docker.Containers.StartContainerAsync(_container, new ContainerStartParameters(), ct);
        }
        finally
        {
            foreach (var placeholder in placeholders)
                await DockerContainerUtils.RemoveContainerAsync(_docker, placeholder);
        }

        Assert.Equal(addressBefore, await GetAddressAsync(ct));
        await WaitSyncedAsync(ct);
    }

    private async Task<IPAddress> GetAddressAsync(CancellationToken ct)
    {
        var inspection = await _docker.Containers.InspectContainerAsync(_container, ct);
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
        var inspection = await _docker.Containers.InspectContainerAsync(_container, ct);
        var networkName = inspection.NetworkSettings.Networks.Single().Key;
        var ownValue = ToUInt32(own);
        var placeholders = new List<string>();
        while (placeholders.Count < maxPlaceholders)
        {
            var network = await _docker.Networks.InspectNetworkAsync(networkName, ct);
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
            await DockerContainerUtils.RemoveContainerAsync(_docker, name);
            await _docker.Containers.CreateContainerAsync(new CreateContainerParameters
            {
                Name = name,
                Image = inspection.Config.Image,
                Entrypoint = ["sleep"],
                Cmd = ["600"],
                HostConfig = new HostConfig { NetworkMode = networkName }
            }, ct);
            placeholders.Add(name);
            await _docker.Containers.StartContainerAsync(name, new ContainerStartParameters(), ct);
        }

        return placeholders;

        static uint ToUInt32(IPAddress address) => BinaryPrimitives.ReadUInt32BigEndian(address.GetAddressBytes());
    }

    public void Dispose() => _docker.Dispose();
}