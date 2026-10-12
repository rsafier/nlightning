using System.Diagnostics;
using Npgsql;

namespace NLightning.Integration.Tests.Fixtures.Postgres;

using Testing.Cluster.Kube;
using Testing.Cluster.Nodes.Postgres;
using Testing.Cluster.Run;

/// <summary>
/// The backend of <see cref="PostgresFixture"/> (test harness phase 4; the only one since NL-866 retired the Docker
/// backend): a run
/// namespace of its own (suite <c>postgres</c>, or <c>postgres-&lt;name&gt;</c> for
/// <see cref="PostgresFixture.StartNamed"/>) holding one <see cref="PostgresNode"/> (the same image release, pinned by
/// digest, with the fixture's database, user and password; its data in an <c>emptyDir</c>, never restarted). This
/// process connects to the pod IP (OrbStack routes the pod network to the host). A failed test of the collection
/// dumps the namespace (<c>[assembly: ClusterDiagnostics]</c>); disposing deletes it.
/// </summary>
public sealed class ClusterPostgresBackend(string name)
{
    private static readonly TimeSpan s_deployTimeout = TimeSpan.FromMinutes(3);

    private TestRun? _run;
    private PostgresNode? _node;

    public string Host => _node?.Host ?? throw new InvalidOperationException("The Postgres fixture is not running");

    public int Port => PostgresNode.Port;

    /// <summary>The run (namespace) of the server.</summary>
    public TestRun Run => _run ?? throw new InvalidOperationException("The Postgres fixture is not running");

    /// <summary>The suite name of the run: <c>postgres</c> for the collection's server, else one per name.</summary>
    public static string SuiteFor(string name) =>
        name == PostgresFixture.DefaultNodeName ? "postgres" : $"postgres-{name}";

    /// <summary>The node's alias: the fixture's container name when it is a valid workload name, else <c>postgres</c>.</summary>
    public static string NodeNameFor(string name) =>
        KubeNames.IsDns1123Label(name) && name.Length <= KubeNames.MaxWorkloadNameLength ? name : "postgres";

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        _run = await TestRun.StartAsync(TestRunOptions.FromEnvironment(SuiteFor(name)) with
        {
            Quota = NamespaceQuota.Spike,
            Log = Console.WriteLine
        }, cancellationToken);
        _node = await PostgresNode.DeployAsync(_run, new PostgresNodeOptions
        {
            Name = NodeNameFor(name),
            User = PostgresFixture.User,
            Password = PostgresFixture.Password,
            Database = PostgresFixture.DefaultDatabase
        }, s_deployTimeout, cancellationToken);

        // The probe already saw the server on TCP inside the pod; this is the same check from this process
        var connectionString = PostgresFixture.BuildConnectionString(Host, Port, PostgresFixture.DefaultDatabase);
        await Testing.Cluster.Poll.UntilDoneAsync(async ct =>
        {
            try
            {
                await PostgresFixture.SelectOneAsync(connectionString, ct);
                return null;
            }
            catch (NpgsqlException e)
            {
                return e.Message;
            }
        }, PostgresFixture.ReadyTimeout, $"Postgres {Host}:{Port} answers from the test process", cancellationToken,
                                                       TimeSpan.FromMilliseconds(250));
        Console.WriteLine($"[cluster] {_run.Namespace}: Postgres {_node.Handle.PodName} at {Host}:{Port} ready in "
                        + $"{watch.Elapsed.TotalSeconds:F1} s");
    }

    public async Task<bool> IsRunningAsync(CancellationToken cancellationToken)
    {
        if (_node is null)
            return false;

        try
        {
            await PostgresFixture.SelectOneAsync(
                PostgresFixture.BuildConnectionString(Host, Port, PostgresFixture.DefaultDatabase), cancellationToken);
            return true;
        }
        catch (NpgsqlException)
        {
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_run is not null)
            await _run.DisposeAsync();
    }
}