using System.Globalization;

namespace NLightning.Testing.Cluster.Nodes.Postgres;

using Images;
using Kube;
using Run;

/// <summary>
/// How a PostgreSQL server of a run is deployed (test harness phase 4): the version table's image, the credentials and
/// database <c>PostgresFixture</c> uses on Docker, the data in an <c>emptyDir</c> by default (a database server of the
/// tests is never restarted).
/// </summary>
public sealed record PostgresNodeOptions
{
    /// <summary>The node's alias (StatefulSet, Service and container name).</summary>
    public string Name { get; init; } = "postgres";

    /// <summary>The image; the version table's PostgreSQL (16.2-alpine by digest) by default.</summary>
    public ImageRef Image { get; init; } = ImageVersions.Postgres;

    /// <summary>The superuser (<c>POSTGRES_USER</c>), also the password's owner.</summary>
    public string User { get; init; } = "superuser";

    /// <summary>The superuser's password (<c>POSTGRES_PASSWORD</c>).</summary>
    public string Password { get; init; } = "superuser";

    /// <summary>The database created at the first start (<c>POSTGRES_DB</c>).</summary>
    public string Database { get; init; } = "nlightning";

    /// <summary>
    /// The data directory in an <c>emptyDir</c> (default: nothing to provision, the node cannot be restarted) or on a
    /// PVC (<see cref="NodeStorage.Persistent"/>, for a test that restarts the server).
    /// </summary>
    public NodeStorage Storage { get; init; } = NodeStorage.Ephemeral;

    /// <summary>The size of the data volume.</summary>
    public string DataSize { get; init; } = "1Gi";

    public WorkloadResources Resources { get; init; } = WorkloadResources.Default;
}

/// <summary>
/// A PostgreSQL server in a run's namespace (<see cref="NodeKind.Postgres"/>): the cluster side of the
/// <c>PostgresFixture</c> of the integration tests. The test process connects to it by pod IP (OrbStack routes the pod
/// network to the host; the server is never restarted, so the IP stays), pods by its alias.
/// </summary>
public sealed class PostgresNode
{
    /// <summary>The server's port in its pod.</summary>
    public const int Port = 5432;

    /// <summary>The data directory in the official image.</summary>
    public const string DataPath = "/var/lib/postgresql/data";

    private PostgresNode(KubeNodeHandle handle, PostgresNodeOptions options, string host)
    {
        Handle = handle;
        Options = options;
        Host = host;
    }

    /// <summary>The deployed node.</summary>
    public KubeNodeHandle Handle { get; }

    public PostgresNodeOptions Options { get; }

    /// <summary>The host the test process connects to (the pod IP).</summary>
    public string Host { get; }

    /// <summary>
    /// The Npgsql connection string to <paramref name="database"/> (the options' database when null) at
    /// <see cref="Host"/>.
    /// </summary>
    public string ConnectionString(string? database = null) =>
        BuildConnectionString(Host, Port, database ?? Options.Database, Options.User, Options.Password);

    /// <summary>
    /// An Npgsql connection string (no Npgsql reference here: the keys are Npgsql's documented ones).
    /// </summary>
    public static string BuildConnectionString(string host, int port, string database, string user, string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentException.ThrowIfNullOrWhiteSpace(database);
        return string.Create(CultureInfo.InvariantCulture,
                             $"Host={host};Port={port};Database={database};Username={user};Password={password}");
    }

    /// <summary>
    /// The workload: the image's entrypoint (it runs <c>initdb</c> on an empty data directory, then the server),
    /// <c>PGDATA</c> in a subdirectory of the data volume (a volume's root may hold <c>lost+found</c>, which
    /// <c>initdb</c> refuses), and a readiness probe that passes only once the server accepts TCP connections.
    /// </summary>
    /// <remarks>
    /// The image's first start runs a temporary server for its init scripts that listens on the Unix socket only, then
    /// restarts: <c>pg_isready</c> over the Unix socket would pass during that window, over TCP it does not.
    /// </remarks>
    public static NodeWorkload Workload(PostgresNodeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.User);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Password);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Database);

        var workload = new NodeWorkload(options.Name, NodeKind.Postgres, options.Image)
        {
            Resources = options.Resources,
            Data = new DataVolume(DataPath, options.DataSize, Storage: options.Storage),
            ReadinessProbe = Probes.Exec(ReadinessCommand(options), periodSeconds: 1, timeoutSeconds: 5,
                                         failureThreshold: 3),
            // The server's smart shutdown on SIGTERM ends quickly without clients
            TerminationGracePeriodSeconds = 15
        };
        workload.Env["POSTGRES_USER"] = options.User;
        workload.Env["POSTGRES_PASSWORD"] = options.Password;
        workload.Env["POSTGRES_DB"] = options.Database;
        workload.Env["PGDATA"] = $"{DataPath}/pgdata";
        workload.Ports.Add(new WorkloadPort("postgres", Port));
        return workload;
    }

    /// <summary>The readiness probe: <c>pg_isready</c> over TCP on loopback.</summary>
    public static IReadOnlyList<string> ReadinessCommand(PostgresNodeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return
        [
            "pg_isready", "-h", "127.0.0.1", "-p", Port.ToString(CultureInfo.InvariantCulture), "-U", options.User,
            "-d", options.Database
        ];
    }

    /// <summary>
    /// Deploys the server in <paramref name="run"/>'s namespace and waits until it is ready and has a pod IP.
    /// </summary>
    public static async Task<PostgresNode> DeployAsync(TestRun run, PostgresNodeOptions options, TimeSpan readyTimeout,
                                                       CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(options);

        var handle = await run.DeployAsync(Workload(options), readyTimeout, cancellationToken).ConfigureAwait(false);
        var host = handle.PodIp ?? throw new InvalidOperationException($"{handle} has no pod IP");
        return new PostgresNode(handle, options, host);
    }
}