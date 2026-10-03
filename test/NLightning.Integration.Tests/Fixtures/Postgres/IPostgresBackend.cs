namespace NLightning.Integration.Tests.Fixtures.Postgres;

/// <summary>
/// Where <see cref="PostgresFixture"/>'s server runs (<see cref="TestBackend"/>, test harness phase 4): a Docker
/// container with its port published on <c>127.0.0.1</c> (<see cref="DockerPostgresBackend"/>, the former fixture) or a
/// pod in a run namespace of the Kubernetes harness (<see cref="ClusterPostgresBackend"/>). The fixture's connection
/// strings are written once over these members, with the same database, user and password on both.
/// </summary>
public interface IPostgresBackend : IAsyncDisposable
{
    TestBackendKind Kind { get; }

    /// <summary>The host this process connects to (<c>127.0.0.1</c>, or the pod IP).</summary>
    string Host { get; }

    /// <summary>The server's port at <see cref="Host"/>.</summary>
    int Port { get; }

    /// <summary>Starts the server and returns once it answers <c>SELECT 1</c> over TCP from this process.</summary>
    Task StartAsync(CancellationToken cancellationToken);

    /// <summary>Whether the server is still running (its container, or its pod ready).</summary>
    Task<bool> IsRunningAsync(CancellationToken cancellationToken);
}