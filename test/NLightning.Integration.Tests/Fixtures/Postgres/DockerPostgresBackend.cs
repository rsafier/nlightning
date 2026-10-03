using Docker.DotNet;

namespace NLightning.Integration.Tests.Fixtures.Postgres;

/// <summary>
/// The Docker backend of <see cref="PostgresFixture"/> (the former fixture, unchanged): the official image as a
/// container named <see cref="PostgresFixture.ContainerName"/>, its 5432 published on a free <c>127.0.0.1</c> port (no
/// dependency on the bridge IP being routable from the host, which only OrbStack and Linux provide).
/// </summary>
public sealed class DockerPostgresBackend(string containerName) : IPostgresBackend
{
    private const string Image = "postgres";
    private const string Tag = "16.2-alpine";

    private readonly DockerClient _client = new DockerClientConfiguration().CreateClient();

    public TestBackendKind Kind => TestBackendKind.Docker;

    public string Host => "127.0.0.1";

    public int Port { get; private set; }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await DockerContainerUtils.EnsureImageAsync(_client, Image, Tag);
        await DockerContainerUtils.RemoveContainerAsync(_client, containerName);

        Port = await DockerContainerUtils.StartWithLoopbackPortAsync(_client, $"{Image}:{Tag}", containerName, 5432,
                                                                     [
                                                                         $"POSTGRES_PASSWORD={PostgresFixture.Password}",
                                                                         $"POSTGRES_USER={PostgresFixture.User}",
                                                                         $"POSTGRES_DB={PostgresFixture.DefaultDatabase}"
                                                                     ]);

        // Postgres restarts once after initdb, so a successful query is the only reliable readiness signal
        await DockerContainerUtils.WaitUntilReadyAsync(containerName,
                                                       ct => PostgresFixture.SelectOneAsync(
                                                           PostgresFixture.BuildConnectionString(
                                                               Host, Port, PostgresFixture.DefaultDatabase), ct),
                                                       PostgresFixture.ReadyTimeout);
    }

    public async Task<bool> IsRunningAsync(CancellationToken cancellationToken)
    {
        try
        {
            var inspect = await _client.Containers.InspectContainerAsync(containerName, cancellationToken);
            return inspect.State.Running;
        }
        catch
        {
            // ignored
        }

        return false;
    }

    public async ValueTask DisposeAsync()
    {
        // Remove containers
        await DockerContainerUtils.RemoveContainerAsync(_client, containerName);
        _client.Dispose();
    }
}